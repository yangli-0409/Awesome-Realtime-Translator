import AVFAudio
import Foundation

/// 识别器吐出的事件。
///
/// 语义约定（整条管道依赖它）：
/// - `.volatile` 是**可被覆盖**的尾部文本，随时会被改写，只能显示在原文区末尾，绝不能翻译。
/// - `.finalized` 是**不可再改**的片段，是断句和翻译的唯一输入。
/// - 一次 `.finalized` 之后，之前的 `.volatile` 立即作废。
enum TranscriptEvent: Sendable, Equatable {
    case volatile(String)
    case finalized(String)
    case failed(String)
}

/// 流式语音识别器。协议化以便替换实现（SpeechAnalyzer / SFSpeechRecognizer / WhisperKit）。
protocol Transcriber: AnyObject, Sendable {
    /// 识别事件流。实现方必须在 init 阶段就建好，保证调用方先订阅、后 start 不丢事件。
    var events: AsyncStream<TranscriptEvent> { get }

    /// 识别器期望的 PCM 格式。采集层负责把系统音频转成这个格式，避免在识别线程上做转换。
    /// 返回 nil 表示「任意格式都接受」。
    var inputFormat: AVAudioFormat? { get async }

    /// 准备模型与语言资源（可能触发下载），耗时操作。
    func prepare() async throws

    func start() async throws

    /// 喂一块音频。必须已是 `inputFormat` 的格式。
    ///
    /// 故意设计成**同步、非隔离**：音频回调每 10~20ms 来一次，
    /// 如果这里要 `await` 跨 actor，就得为每块音频开一个 Task，纯粹的调度浪费。
    /// 实现方用一个带锁的 sink 直接转交。
    func append(_ buffer: AVAudioPCMBuffer)

    /// 结束识别，冲刷出最后的 `.finalized`。
    func finish() async
}

enum TranscriberError: LocalizedError {
    case notAvailableOnThisDevice
    case localeNotSupported(String)
    case assetInstallFailed(String)
    case notPrepared

    var errorDescription: String? {
        switch self {
        case .notAvailableOnThisDevice:
            "这台 Mac 不支持本机语音识别（需要 Apple Silicon + macOS 26）。"
        case .localeNotSupported(let id):
            "系统的本机语音识别不支持语言「\(id)」。"
        case .assetInstallFailed(let message):
            "语音识别模型下载失败：\(message)"
        case .notPrepared:
            "识别器还没准备好，请先调用 prepare()。"
        }
    }
}
