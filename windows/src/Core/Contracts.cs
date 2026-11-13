namespace AwesomeRealtimeTranslator.Core;

/// <summary>
/// 一块 16kHz 单声道 float PCM。Whisper 要的就是这个格式，采集层负责转好，
/// 不在识别线程上做格式转换。
/// </summary>
/// <param name="Samples">样本，取值 [-1, 1]</param>
public readonly record struct AudioChunk(float[] Samples)
{
    public const int SampleRate = 16000;

    public double Seconds => Samples.Length / (double)SampleRate;
}

/// <summary>
/// 系统音频采集。协议化以便测试时换成假的音频源。
/// 对应 macOS 版的 AudioSource（那边是 ScreenCaptureKit，这边是 WASAPI loopback）。
/// </summary>
public interface IAudioSource : IAsyncDisposable
{
    /// <summary>采集到一块音频。可能在任意线程上触发。</summary>
    event Action<AudioChunk>? ChunkAvailable;

    /// <summary>采集中断（设备被拔掉、独占模式抢占等）。</summary>
    event Action<Exception>? Failed;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>
/// 流式语音识别器。协议化以便替换实现（Whisper / 未来的 Windows AI API）。
/// 对应 macOS 版的 Transcriber protocol。
/// </summary>
public interface ITranscriber : IAsyncDisposable
{
    /// <summary>识别事件流。实现方必须在构造阶段就建好，保证调用方先订阅、后 start 不丢事件。</summary>
    event Action<TranscriptEvent>? Event;

    /// <summary>准备模型（可能触发下载），耗时操作。</summary>
    Task PrepareAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 喂一块音频。
    ///
    /// 故意设计成<b>同步、不等待</b>：音频回调每 10~20ms 来一次，
    /// 如果这里要 await，就得为每块音频开一个 Task，纯粹的调度浪费。
    /// 实现方把它丢进一个带锁的环形缓冲，识别循环自己去取。
    /// </summary>
    void Append(AudioChunk chunk);

    /// <summary>结束识别，冲刷出最后的 Finalized。</summary>
    Task FinishAsync();
}

/// <summary>
/// 翻译引擎。
///
/// <paramref name="context"/> 是最近几句已定稿原文，给需要上下文的引擎用；
/// 逐句无状态的引擎（OPUS-MT）会忽略它。
/// </summary>
public interface ITranslator : IAsyncDisposable
{
    Task PrepareAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<string> TranslateAsync(string text, IReadOnlyList<string> context, CancellationToken cancellationToken = default);
}

/// <summary>翻译引擎不可用时的降级实现：原样返回，等于「只显示原文」。</summary>
public sealed class PassthroughTranslator : ITranslator
{
    public Task PrepareAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<string> TranslateAsync(string text, IReadOnlyList<string> context, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class TranscriberException : Exception
{
    public TranscriberException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public sealed class TranslatorException : Exception
{
    public TranslatorException(string message, string? recoverySuggestion = null, Exception? inner = null)
        : base(message, inner) => RecoverySuggestion = recoverySuggestion;

    public string? RecoverySuggestion { get; }
}
