import AVFAudio
import Foundation
import Speech

/// 把音频采集队列和识别器的输入流连起来的转交点。
///
/// 存在的唯一理由：音频回调在 ScreenCaptureKit 的串行队列上、每 10~20ms 一次，
/// 不能为每块音频开一个 Task 去 await actor。锁的持有时间是一次 `yield`，可以忽略。
final class AudioInputSink: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: AsyncStream<AnalyzerInput>.Continuation?

    func set(_ continuation: AsyncStream<AnalyzerInput>.Continuation?) {
        lock.withLock { self.continuation = continuation }
    }

    func yield(_ buffer: AVAudioPCMBuffer) {
        let continuation = lock.withLock { self.continuation }
        continuation?.yield(AnalyzerInput(buffer: buffer))
    }

    func finish() {
        let continuation = lock.withLock { () -> AsyncStream<AnalyzerInput>.Continuation? in
            let current = self.continuation
            self.continuation = nil
            return current
        }
        continuation?.finish()
    }
}
