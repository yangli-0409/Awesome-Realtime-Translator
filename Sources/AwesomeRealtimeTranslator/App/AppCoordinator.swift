import AVFAudio
import Foundation
import Observation

/// 整条管道的装配与生命周期：
///
/// ```
/// SystemAudioCapture ──PCM──► SpeechAnalyzerTranscriber
///                                     │ TranscriptEvent
///                                     ▼
///                                 Segmenter ──Sentence──► 翻译串行队列 ──► CaptionStore ──► CaptionPanel
/// ```
///
/// 几个刻意的设计：
/// - 翻译走**串行**队列：保证译文顺序与原文一致，也避免并发请求把句子插乱（译文不闪烁）。
/// - 翻译引擎准备失败不会让整条管道挂掉，降级成「仅原文」并在字幕区给一行提示。
/// - 所有状态变更都在 MainActor 上，UI 不需要额外同步。
@MainActor
@Observable
final class AppCoordinator {
    enum State: Equatable {
        case idle
        case preparing(String)
        case running
        case paused
        case failed(String)

        var isActive: Bool {
            switch self {
            case .running, .paused, .preparing: true
            case .idle, .failed: false
            }
        }
    }

    static let shared = AppCoordinator()

    private(set) var state: State = .idle
    let store = CaptionStore()
    let settings = AppSettings.shared
    @ObservationIgnored private(set) lazy var panel = CaptionPanelController(
        store: store,
        settings: settings,
        onToggle: { [weak self] in self?.toggle() }
    )

    private var capture: SystemAudioCapture?
    private var transcriber: (any Transcriber)?
    private var translator: (any Translator)?
    private var usesRealTranslator = false
    private var segmenter = Segmenter()

    private var eventTask: Task<Void, Never>?
    private var tickTask: Task<Void, Never>?
    private var translationTask: Task<Void, Never>?
    private var sentenceContinuation: AsyncStream<Sentence>.Continuation?
    private var sessionStart = Date()

    var isRunning: Bool { state == .running }

    var statusLabel: String {
        switch state {
        case .idle: "未开始"
        case .preparing(let step): step
        case .running: "正在听"
        case .paused: "已暂停"
        case .failed(let message): "出错：\(message)"
        }
    }

    // MARK: - 对外控制（F9）

    func toggle() {
        switch state {
        case .running: pause()
        case .paused: Task { await resume() }
        case .idle, .failed: Task { await start() }
        case .preparing: break
        }
    }

    func start() async {
        guard !state.isActive else { return }

        guard Permissions.hasScreenRecordingAccess else {
            setState(.failed("缺少「屏幕与系统音频录制」权限"))
            OnboardingWindow.show()
            return
        }

        setState(.preparing("准备识别模型…"))
        sessionStart = Date()
        store.startSession()
        store.isRunning = false
        store.statusMessage = nil // 清掉启动时的「点 ▶ 开始听」提示
        var config = Segmenter.Configuration()
        // 0.45s：面试里句子之间的停顿通常在 0.3~0.6s，0.7s 偏保守，译文出得太晚。
        // 再往下调（<0.35s）会把一句话从中间切断，翻译质量明显下降。
        config.silenceFlushInterval = 0.45
        segmenter = Segmenter(configuration: config)
        panel.show()

        // 1. 识别器
        let transcriber = SpeechAnalyzerTranscriber(
            locale: settings.sourceLocale,
            contextualStrings: settings.glossary
        )
        do {
            try await transcriber.prepare()
        } catch {
            setState(.failed(error.localizedDescription))
            store.statusMessage = error.localizedDescription
            return
        }
        self.transcriber = transcriber

        // 2. 翻译引擎（失败降级，不阻断原文字幕）
        setState(.preparing("准备翻译语言包…"))
        let appleTranslator = AppleTranslator(
            source: settings.sourceLanguageCode,
            target: settings.targetLanguageCode
        )
        do {
            try await appleTranslator.prepare()
            translator = appleTranslator
            usesRealTranslator = true
            store.statusMessage = nil
        } catch {
            translator = PassthroughTranslator()
            usesRealTranslator = false
            let hint = (error as? TranslatorError)?.recoverySuggestion
            store.statusMessage = "只显示原文：\(error.localizedDescription)" + (hint.map { "\n\($0)" } ?? "")
            Log.translate.error("翻译引擎降级: \(error.localizedDescription)")
        }

        // 3. 管道（先订阅，后喂数据，避免丢前几个事件）
        startTranslationPump()
        startEventPump(transcriber)
        startTicker()

        do {
            try await transcriber.start()
        } catch {
            setState(.failed(error.localizedDescription))
            await teardown()
            return
        }

        // 4. 音频采集
        let capture = SystemAudioCapture()
        let format = await transcriber.inputFormat
        do {
            try await capture.start(
                outputFormat: format,
                onBuffer: { [weak transcriber] buffer in
                    transcriber?.append(buffer)
                },
                onFailure: { [weak self] error in
                    Task { @MainActor [weak self] in
                        guard let self else { return }
                        self.store.statusMessage = "音频采集中断：\(error.localizedDescription)"
                        self.setState(.failed(error.localizedDescription))
                        await self.teardown()
                    }
                }
            )
        } catch {
            setState(.failed(error.localizedDescription))
            store.statusMessage = error.localizedDescription
            await teardown()
            return
        }
        self.capture = capture

        setState(.running)
        store.isRunning = true
        Log.session.info("会话已开始")
    }

    func pause() {
        guard state == .running else { return }
        let capture = capture
        self.capture = nil
        setState(.paused)
        store.isRunning = false
        Task { await capture?.stop() }
    }

    func resume() async {
        guard state == .paused, let transcriber else { return }
        let capture = SystemAudioCapture()
        let format = await transcriber.inputFormat
        do {
            try await capture.start(
                outputFormat: format,
                onBuffer: { [weak transcriber] buffer in transcriber?.append(buffer) },
                onFailure: { [weak self] error in
                    Task { @MainActor [weak self] in
                        self?.setState(.failed(error.localizedDescription))
                    }
                }
            )
            self.capture = capture
            setState(.running)
            store.isRunning = true
        } catch {
            setState(.failed(error.localizedDescription))
        }
    }

    func stop() async {
        guard state.isActive else { return }
        // 冲刷出最后一句
        let remaining = segmenter.flush(at: Date().timeIntervalSince(sessionStart))
        for sentence in remaining { emit(sentence) }
        store.pendingSource = ""

        await teardown()
        setState(.idle)
        store.isRunning = false

        if settings.saveTranscripts, !store.lines.isEmpty {
            SessionArchive.save(markdown: store.markdown, startedAt: store.sessionStartedAt ?? Date())
        }
        Log.session.info("会话已结束")
    }

    private func teardown() async {
        let capture = capture
        self.capture = nil
        await capture?.stop()

        // 留一点时间让最后的 finalized 结果流出来
        await transcriber?.finish()
        transcriber = nil

        eventTask?.cancel(); eventTask = nil
        tickTask?.cancel(); tickTask = nil
        sentenceContinuation?.finish(); sentenceContinuation = nil
        translationTask?.cancel(); translationTask = nil
        if let appleTranslator = translator as? AppleTranslator {
            appleTranslator.invalidate()
        }
        translator = nil
        usesRealTranslator = false
    }

    // MARK: - 管道

    private func startEventPump(_ transcriber: any Transcriber) {
        let events = transcriber.events
        eventTask = Task { [weak self] in
            for await event in events {
                guard let self else { return }
                if case .failed(let message) = event {
                    self.store.statusMessage = "识别中断：\(message)"
                    continue
                }
                let now = Date().timeIntervalSince(self.sessionStart)
                let sentences = self.segmenter.ingest(event, at: now)
                self.store.pendingSource = self.segmenter.pendingText
                for sentence in sentences { self.emit(sentence) }
            }
        }
    }

    /// 静音定稿需要一个时钟。100ms 的粒度对 700ms 的阈值足够，开销可忽略。
    private func startTicker() {
        tickTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(100))
                guard let self else { return }
                let now = Date().timeIntervalSince(self.sessionStart)
                let sentences = self.segmenter.tick(at: now)
                guard !sentences.isEmpty else { continue }
                for sentence in sentences { self.emit(sentence) }
                self.store.pendingSource = self.segmenter.pendingText
            }
        }
    }

    private func startTranslationPump() {
        let (stream, continuation) = AsyncStream<Sentence>.makeStream(bufferingPolicy: .unbounded)
        sentenceContinuation = continuation
        translationTask = Task { [weak self] in
            for await sentence in stream {
                guard let self, self.usesRealTranslator, let translator = self.translator else { continue }
                let context = self.store.recentContext(3)
                do {
                    let translated = try await translator.translate(sentence.text, context: context)
                    self.store.setTranslation(translated, for: sentence.id)
                } catch {
                    Log.translate.error("翻译失败: \(error.localizedDescription)")
                    self.store.markTranslationFailed(for: sentence.id)
                }
            }
        }
    }

    private func emit(_ sentence: Sentence) {
        store.append(sentence)
        sentenceContinuation?.yield(sentence)
    }

    private func setState(_ newState: State) {
        state = newState
    }
}
