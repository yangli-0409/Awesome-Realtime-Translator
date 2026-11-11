import AVFAudio
import Foundation
import Speech

/// macOS 26 的 `SpeechAnalyzer` + `SpeechTranscriber` 实现。
///
/// 为什么用它而不是 `SFSpeechRecognizer`（PRD 7.4 #2 的处理）：
/// `SFSpeechRecognitionTask` 是「一次请求一段话」的模型，停顿后任务自己结束，
/// 必须重建请求，重建窗口里的音频会丢。`SpeechAnalyzer` 是常驻 actor，
/// 输入是一条永不结束的 `AsyncSequence`，模型内部自己维护 volatile / finalized 区间，
/// 没有「会话时长限制」这个概念，也就不需要无缝重启逻辑。
///
/// volatile 结果的语义：每条结果带一个 `CMTimeRange`。未定稿区间内的结果会被后续结果覆盖，
/// `isFinal` 为 true 的结果对应的区间不再变化。我们把两者映射成 `.volatile` / `.finalized`，
/// 下游 `Segmenter` 只拿 `.finalized` 去翻译 —— 这是译文不闪烁的根本保证。
actor SpeechAnalyzerTranscriber: Transcriber {
    nonisolated let events: AsyncStream<TranscriptEvent>
    private let eventContinuation: AsyncStream<TranscriptEvent>.Continuation

    private let requestedLocale: Locale
    private let contextualStrings: [String]

    private var transcriber: SpeechTranscriber?
    private var analyzer: SpeechAnalyzer?
    private var resolvedLocale: Locale?
    private var analyzerFormat: AVAudioFormat?
    private let sink = AudioInputSink()
    private var inputSequence: AsyncStream<AnalyzerInput>?
    private var resultsTask: Task<Void, Never>?
    private var reservedLocale: Locale?

    /// 下载进度（0...1），语言模型首次安装时有用。
    private(set) var installationProgress: Double = 1

    init(locale: Locale, contextualStrings: [String] = []) {
        requestedLocale = locale
        self.contextualStrings = contextualStrings
        let (stream, continuation) = AsyncStream<TranscriptEvent>.makeStream(bufferingPolicy: .bufferingNewest(256))
        events = stream
        eventContinuation = continuation
    }

    var inputFormat: AVAudioFormat? {
        analyzerFormat
    }

    // MARK: - 准备

    func prepare() async throws {
        guard SpeechTranscriber.isAvailable else { throw TranscriberError.notAvailableOnThisDevice }

        guard let locale = await SpeechTranscriber.supportedLocale(equivalentTo: requestedLocale) else {
            throw TranscriberError.localeNotSupported(requestedLocale.identifier(.bcp47))
        }
        resolvedLocale = locale

        let transcriber = SpeechTranscriber(
            locale: locale,
            transcriptionOptions: [],
            // volatileResults：边说边出字（partial results，F3）
            //
            // fastResults 是实测补上的。只开 volatileResults 时，模型确实逐词出结果，
            // 但它把一整句的十几条 volatile 结果**在同一毫秒内一次性吐完**，然后静默
            // 约 3.7 秒再吐下一批（埋点实测：间隔 0ms / 0ms / ... / 3735ms / 3789ms）。
            // 屏幕上的表现就是「一段一段跳」而不是逐词推进。
            // 音频层不是瓶颈：ScreenCaptureKit 稳定每 21ms 给 960 帧（= 20ms @48kHz）。
            reportingOptions: [.volatileResults, .fastResults],
            attributeOptions: []
        )
        self.transcriber = transcriber

        try await installAssetsIfNeeded(for: transcriber, locale: locale)

        // 术语表 → 识别上下文（F11 的官方入口，不是 prompt 拼接）
        let context = AnalysisContext()
        if !contextualStrings.isEmpty {
            context.contextualStrings = [.general: contextualStrings]
        }

        let analyzer = SpeechAnalyzer(
            modules: [transcriber],
            options: SpeechAnalyzer.Options(priority: .userInitiated, modelRetention: .whileInUse)
        )
        try await analyzer.setContext(context)

        let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber])
        analyzerFormat = format
        try await analyzer.prepareToAnalyze(in: format)
        self.analyzer = analyzer

        Log.speech.info("识别器就绪 locale=\(locale.identifier(.bcp47)) format=\(format?.description ?? "any")")
    }

    private func installAssetsIfNeeded(for transcriber: SpeechTranscriber, locale: Locale) async throws {
        let installed = await SpeechTranscriber.installedLocales
        let needsInstall = !installed.contains { $0.identifier(.bcp47) == locale.identifier(.bcp47) }

        if needsInstall {
            Log.speech.info("开始下载语音识别模型 \(locale.identifier(.bcp47))")
            installationProgress = 0
            do {
                if let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
                    let progress = request.progress
                    let observation = Task { [weak self] in
                        while !Task.isCancelled, progress.fractionCompleted < 1 {
                            await self?.setInstallationProgress(progress.fractionCompleted)
                            try? await Task.sleep(for: .milliseconds(300))
                        }
                    }
                    try await request.downloadAndInstall()
                    observation.cancel()
                }
            } catch {
                installationProgress = 1
                throw TranscriberError.assetInstallFailed(error.localizedDescription)
            }
            installationProgress = 1
        }

        // 预留语言，避免系统在内存紧张时回收模型
        do {
            _ = try await AssetInventory.reserve(locale: locale)
            reservedLocale = locale
        } catch {
            Log.speech.debug("reserve(locale:) 失败（不致命）: \(error.localizedDescription)")
        }
    }

    private func setInstallationProgress(_ value: Double) {
        installationProgress = value
    }

    // MARK: - 运行

    func start() async throws {
        guard let analyzer, let transcriber else { throw TranscriberError.notPrepared }

        let (stream, continuation) = AsyncStream<AnalyzerInput>.makeStream(bufferingPolicy: .bufferingNewest(64))
        inputSequence = stream
        sink.set(continuation)

        resultsTask = Task { [eventContinuation] in
            do {
                for try await result in transcriber.results {
                    let text = String(result.text.characters)
                    guard !text.isEmpty else { continue }
                    eventContinuation.yield(result.isFinal ? .finalized(text) : .volatile(text))
                }
            } catch {
                Log.speech.error("识别结果流中断: \(error.localizedDescription)")
                eventContinuation.yield(.failed(error.localizedDescription))
            }
        }

        try await analyzer.start(inputSequence: stream)
        Log.speech.info("识别已启动")
    }

    nonisolated func append(_ buffer: AVAudioPCMBuffer) {
        sink.yield(buffer)
    }

    func finish() async {
        sink.finish()
        inputSequence = nil
        if let analyzer {
            do {
                try await analyzer.finalizeAndFinishThroughEndOfInput()
            } catch {
                Log.speech.debug("finalizeAndFinish: \(error.localizedDescription)")
                await analyzer.cancelAndFinishNow()
            }
        }
        resultsTask?.cancel()
        resultsTask = nil
        if let reservedLocale {
            _ = await AssetInventory.release(reservedLocale: reservedLocale)
            self.reservedLocale = nil
        }
        analyzer = nil
        transcriber = nil
        eventContinuation.finish()
        Log.speech.info("识别已停止")
    }
}
