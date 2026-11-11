import AVFAudio
import CoreMedia
import Foundation
import ScreenCaptureKit

/// 用 ScreenCaptureKit 采集「电脑正在播放的声音」。
///
/// 关键点：
/// - `excludesCurrentProcessAudio = true`：不采集本应用自己的声音，避免自激（F1）。
/// - 同时把本应用的窗口从内容过滤器里排除，字幕窗不会进入采集内容。
/// - 视频帧配置到最小（2×2、1fps）并直接丢弃：SCStream 需要一个内容过滤器，
///   但我们只要音频；这样 CPU/能耗接近零。
/// - 戴耳机同样有效：SCK 抓的是系统混音输出，与物理输出设备无关。
/// - `didStopWithError` + 看门狗双重保险，覆盖插拔耳机/切换输出设备后的自动恢复。
final class SystemAudioCapture: NSObject, AudioSource, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private let audioQueue = DispatchQueue(label: "com.leon.art.audio", qos: .userInitiated)
    private let videoQueue = DispatchQueue(label: "com.leon.art.video", qos: .utility)

    private var stream: SCStream?
    private var converter: PCMConverter?
    private var converterInputFormat: AVAudioFormat?
    private var outputFormat: AVAudioFormat?
    private var onBuffer: (@Sendable (AVAudioPCMBuffer) -> Void)?
    private var onFailure: (@Sendable (any Error) -> Void)?

    private var restartAttempts = 0
    private var isStopping = false
    private var lastBufferAt = Date.distantPast
    private var watchdog: DispatchSourceTimer?

    private static let maxRestartAttempts = 5
    /// 超过这个时间没有任何音频回调就认为采集已经死了（设备切换的典型表现）
    private static let watchdogSilenceLimit: TimeInterval = 12

    // MARK: - AudioSource

    func start(
        outputFormat: AVAudioFormat?,
        onBuffer: @escaping @Sendable (AVAudioPCMBuffer) -> Void,
        onFailure: @escaping @Sendable (any Error) -> Void
    ) async throws {
        lock.withLock {
            self.outputFormat = outputFormat
            self.onBuffer = onBuffer
            self.onFailure = onFailure
            self.isStopping = false
            self.restartAttempts = 0
            self.lastBufferAt = Date()
        }
        try await startStream()
        startWatchdog()
    }

    func stop() async {
        let current: SCStream? = lock.withLock {
            isStopping = true
            let s = stream
            stream = nil
            onBuffer = nil
            onFailure = nil
            converter = nil
            converterInputFormat = nil
            return s
        }
        stopWatchdog()
        if let current {
            do { try await current.stopCapture() } catch {
                Log.audio.debug("stopCapture: \(error.localizedDescription)")
            }
        }
    }

    // MARK: - 建流

    private func startStream() async throws {
        guard Permissions.hasScreenRecordingAccess else { throw AudioCaptureError.screenRecordingDenied }

        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let display = content.displays.first else { throw AudioCaptureError.noDisplayAvailable }

        let ownBundleID = Bundle.main.bundleIdentifier
        let ownWindows = content.windows.filter { $0.owningApplication?.bundleIdentifier == ownBundleID }
        let filter = SCContentFilter(display: display, excludingWindows: ownWindows)

        let config = SCStreamConfiguration()
        config.capturesAudio = true
        config.excludesCurrentProcessAudio = true
        config.sampleRate = 48_000
        config.channelCount = 1
        // 视频通道压到最小：SCStream 必须有内容过滤器，但我们不消费画面。
        config.width = 2
        config.height = 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        config.queueDepth = 5
        config.showsCursor = false
        config.captureMicrophone = false

        let newStream = SCStream(filter: filter, configuration: config, delegate: self)
        try newStream.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
        // 画面输出也注册并立即丢弃：部分系统版本上不注册 screen output 会导致流不产出任何样本。
        try newStream.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        try await newStream.startCapture()

        lock.withLock {
            self.stream = newStream
            self.lastBufferAt = Date()
        }
        Log.audio.info("系统音频采集已启动")
    }

    // MARK: - 样本回调

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio else { return } // 画面帧直接丢弃
        guard sampleBuffer.isValid, sampleBuffer.numSamples > 0 else { return }
        guard let formatDescription = sampleBuffer.formatDescription,
              formatDescription.mediaType == .audio,
              let asbd = formatDescription.audioStreamBasicDescription
        else { return }

        var mutableASBD = asbd
        guard let inputFormat = AVAudioFormat(streamDescription: &mutableASBD) else { return }

        let frames = AVAudioFrameCount(sampleBuffer.numSamples)
        guard let inputBuffer = AVAudioPCMBuffer(pcmFormat: inputFormat, frameCapacity: frames) else { return }
        inputBuffer.frameLength = frames

        // CMSampleBuffer 的内存在回调返回后失效，这里必须深拷贝。
        let status = CMSampleBufferCopyPCMDataIntoAudioBufferList(
            sampleBuffer,
            at: 0,
            frameCount: Int32(frames),
            into: inputBuffer.mutableAudioBufferList
        )
        guard status == noErr else {
            Log.audio.error("PCM 拷贝失败: \(status)")
            return
        }

        let (handler, converted) = lock.withLock { () -> ((@Sendable (AVAudioPCMBuffer) -> Void)?, AVAudioPCMBuffer?) in
            lastBufferAt = Date()
            guard let handler = onBuffer else { return (nil, nil) }
            guard let target = outputFormat, target != inputFormat else { return (handler, inputBuffer) }
            if converter == nil || converterInputFormat != inputFormat {
                converter = PCMConverter(from: inputFormat, to: target)
                converterInputFormat = inputFormat
                if converter == nil {
                    onFailure?(AudioCaptureError.formatUnsupported)
                    return (nil, nil)
                }
            }
            return (handler, converter?.convert(inputBuffer))
        }

        if let handler, let converted { handler(converted) }
    }

    // MARK: - SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: any Error) {
        Log.audio.error("SCStream 中断: \(error.localizedDescription)")
        scheduleRestart(reason: error.localizedDescription)
    }

    // MARK: - 自动恢复

    private func scheduleRestart(reason: String) {
        let shouldRestart: Bool = lock.withLock {
            guard !isStopping else { return false }
            guard restartAttempts < Self.maxRestartAttempts else { return false }
            restartAttempts += 1
            stream = nil
            return true
        }

        guard shouldRestart else {
            let failure = lock.withLock { onFailure }
            if !lock.withLock({ isStopping }) {
                failure?(AudioCaptureError.restartsExhausted(underlying: reason))
            }
            return
        }

        let attempt = lock.withLock { restartAttempts }
        let delay = min(0.5 * Double(attempt), 3.0)
        Log.audio.info("第 \(attempt) 次尝试重建音频采集，\(delay)s 后")

        Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard let self, !self.lock.withLock({ self.isStopping }) else { return }
            do {
                try await self.startStream()
                self.lock.withLock { self.restartAttempts = 0 }
            } catch {
                self.scheduleRestart(reason: error.localizedDescription)
            }
        }
    }

    private func startWatchdog() {
        stopWatchdog()
        let timer = DispatchSource.makeTimerSource(queue: videoQueue)
        timer.schedule(deadline: .now() + 5, repeating: 5)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let (silent, running) = self.lock.withLock {
                (Date().timeIntervalSince(self.lastBufferAt), !self.isStopping && self.stream != nil)
            }
            guard running, silent > Self.watchdogSilenceLimit else { return }
            Log.audio.info("看门狗：\(Int(silent))s 没有音频，重建采集（可能切换了输出设备）")
            self.lock.withLock { self.lastBufferAt = Date() }
            Task { [weak self] in
                guard let self else { return }
                let old = self.lock.withLock { () -> SCStream? in
                    let s = self.stream
                    self.stream = nil
                    return s
                }
                try? await old?.stopCapture()
                self.scheduleRestart(reason: "watchdog")
            }
        }
        timer.resume()
        lock.withLock { watchdog = timer }
    }

    private func stopWatchdog() {
        let timer = lock.withLock { () -> DispatchSourceTimer? in
            let t = watchdog
            watchdog = nil
            return t
        }
        timer?.cancel()
    }
}
