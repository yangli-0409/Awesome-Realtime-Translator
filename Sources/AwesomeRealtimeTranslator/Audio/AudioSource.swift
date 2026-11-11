import AVFAudio
import Foundation

/// 音频来源。协议化以便单测时注入文件回放源。
protocol AudioSource: AnyObject, Sendable {
    /// - Parameters:
    ///   - outputFormat: 期望输出的 PCM 格式；nil 表示原样给出。
    ///   - onBuffer: 在采集队列上回调，不要在里面做重活。
    ///   - onFailure: 自动恢复都失败之后才会调用。
    func start(
        outputFormat: AVAudioFormat?,
        onBuffer: @escaping @Sendable (AVAudioPCMBuffer) -> Void,
        onFailure: @escaping @Sendable (any Error) -> Void
    ) async throws

    func stop() async
}

enum AudioCaptureError: LocalizedError {
    case noDisplayAvailable
    case screenRecordingDenied
    case formatUnsupported
    case restartsExhausted(underlying: String)

    var errorDescription: String? {
        switch self {
        case .noDisplayAvailable:
            "找不到可采集的显示器。"
        case .screenRecordingDenied:
            "没有「屏幕与系统音频录制」权限，无法采集系统声音。"
        case .formatUnsupported:
            "系统音频格式无法转换成识别器需要的格式。"
        case .restartsExhausted(let underlying):
            "系统音频采集多次重启失败：\(underlying)"
        }
    }
}

/// PCM 格式转换器。`SCStream` 给的是 48kHz Float32 非交错，识别器要的格式不一定一致（PRD 7.4 #3）。
final class PCMConverter {
    private let converter: AVAudioConverter
    private let inputFormat: AVAudioFormat
    private let outputFormat: AVAudioFormat

    init?(from input: AVAudioFormat, to output: AVAudioFormat) {
        guard let converter = AVAudioConverter(from: input, to: output) else { return nil }
        converter.sampleRateConverterQuality = AVAudioQuality.medium.rawValue
        self.converter = converter
        inputFormat = input
        outputFormat = output
    }

    /// 只在单一队列上调用（AVAudioConverter 非线程安全）。
    func convert(_ input: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        let ratio = outputFormat.sampleRate / inputFormat.sampleRate
        let capacity = AVAudioFrameCount(Double(input.frameLength) * ratio) + 1024
        guard let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else { return nil }

        var consumed = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, outStatus in
            if consumed {
                outStatus.pointee = .noDataNow
                return nil
            }
            consumed = true
            outStatus.pointee = .haveData
            return input
        }

        switch status {
        case .haveData, .inputRanDry:
            return output.frameLength > 0 ? output : nil
        case .endOfStream:
            return nil
        case .error:
            Log.audio.error("PCM 转换失败: \(error?.localizedDescription ?? "unknown")")
            return nil
        @unknown default:
            return nil
        }
    }
}
