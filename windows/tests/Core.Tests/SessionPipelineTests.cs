using AwesomeRealtimeTranslator.Core;
using Xunit;

namespace AwesomeRealtimeTranslator.Core.Tests;

/// <summary>
/// 用假的音频源 / 识别器 / 翻译器跑通整条管道。
///
/// 这验证的是装配逻辑本身——事件订阅顺序、降级路径、翻译串行性、停机冲刷——
/// 全部与 Windows 无关，所以能在开发机上跑。真正需要 Windows 的只剩
/// 「WASAPI 到底有没有抓到声音」和「窗口到底有没有被录屏排除」两件事。
/// </summary>
public class SessionPipelineTests
{
    private sealed class FakeAudioSource : IAudioSource
    {
        public event Action<AudioChunk>? ChunkAvailable;

        public event Action<Exception>? Failed;

        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public Exception? StartError { get; init; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (StartError is not null)
            {
                return Task.FromException(StartError);
            }

            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public void EmitChunk(AudioChunk chunk) => ChunkAvailable?.Invoke(chunk);

        public void EmitFailure(Exception ex) => Failed?.Invoke(ex);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public event Action<TranscriptEvent>? Event;

        public bool Prepared { get; private set; }

        public bool Started { get; private set; }

        public bool Finished { get; private set; }

        public int AppendedChunks { get; private set; }

        public Exception? PrepareError { get; init; }

        public Task PrepareAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            if (PrepareError is not null)
            {
                return Task.FromException(PrepareError);
            }

            Prepared = true;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public void Append(AudioChunk chunk) => AppendedChunks++;

        public Task FinishAsync()
        {
            Finished = true;
            return Task.CompletedTask;
        }

        public void Emit(TranscriptEvent evt) => Event?.Invoke(evt);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTranslator : ITranslator
    {
        private int _concurrent;

        public Exception? PrepareError { get; init; }

        public List<string> Seen { get; } = [];

        public bool SawConcurrentCalls { get; private set; }

        public Task PrepareAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            PrepareError is not null ? Task.FromException(PrepareError) : Task.CompletedTask;

        public async Task<string> TranslateAsync(string text, IReadOnlyList<string> context, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _concurrent) > 1)
            {
                SawConcurrentCalls = true;
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            lock (Seen)
            {
                Seen.Add(text);
            }

            Interlocked.Decrement(ref _concurrent);
            return "【译】" + text;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>测试里「UI 线程」就是直接执行，断言得以同步观察。</summary>
    private static void Inline(Action action) => action();

    private static (SessionPipeline Pipeline, CaptionStore Store, FakeAudioSource Audio, FakeTranscriber Transcriber, FakeTranslator Translator)
        Build(FakeAudioSource? audio = null, FakeTranscriber? transcriber = null, FakeTranslator? translator = null)
    {
        var store = new CaptionStore();
        var settings = new AppSettings(new InMemorySettingsStore());
        FakeAudioSource a = audio ?? new FakeAudioSource();
        FakeTranscriber t = transcriber ?? new FakeTranscriber();
        FakeTranslator tr = translator ?? new FakeTranslator();
        var pipeline = new SessionPipeline(store, settings, () => a, () => t, () => tr, Inline);
        return (pipeline, store, a, t, tr);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        int waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(10).ConfigureAwait(false);
            waited += 10;
        }

        Assert.True(condition(), $"等待超时：{because}");
    }

    [Fact]
    public async Task StartBringsEverythingUp()
    {
        (SessionPipeline pipeline, CaptionStore store, FakeAudioSource audio, FakeTranscriber transcriber, _) = Build();

        await pipeline.StartAsync();

        Assert.Equal(PipelineState.Running, pipeline.State);
        Assert.True(transcriber.Prepared);
        Assert.True(transcriber.Started);
        Assert.True(audio.Started);
        Assert.True(store.IsRunning);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task AudioFlowsIntoTranscriber()
    {
        (SessionPipeline pipeline, _, FakeAudioSource audio, FakeTranscriber transcriber, _) = Build();
        await pipeline.StartAsync();

        audio.EmitChunk(new AudioChunk(new float[160]));
        audio.EmitChunk(new AudioChunk(new float[160]));

        Assert.Equal(2, transcriber.AppendedChunks);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task FinalizedSentenceReachesStoreAndGetsTranslated()
    {
        (SessionPipeline pipeline, CaptionStore store, _, FakeTranscriber transcriber, FakeTranslator translator) = Build();
        await pipeline.StartAsync();

        transcriber.Emit(TranscriptEvent.Finalized("Tell me about yourself."));

        await WaitUntilAsync(() => store.Lines.Count == 1, "句子应该进入 store");
        await WaitUntilAsync(() => store.Lines[0].Translation is not null, "译文应该回填");

        Assert.Equal("Tell me about yourself.", store.Lines[0].Source);
        Assert.Equal("【译】Tell me about yourself.", store.Lines[0].Translation);
        Assert.Contains("Tell me about yourself.", translator.Seen);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task VolatileOnlyUpdatesPendingSourceAndIsNeverTranslated()
    {
        (SessionPipeline pipeline, CaptionStore store, _, FakeTranscriber transcriber, FakeTranslator translator) = Build();
        await pipeline.StartAsync();

        transcriber.Emit(TranscriptEvent.Volatile("so tell me about"));

        // 末词按 StableTail 规则裁掉
        Assert.Equal("so tell me", store.PendingSource);
        Assert.Empty(store.Lines);

        await Task.Delay(50);
        Assert.Empty(translator.Seen);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task TranslationsStayInOrder()
    {
        // 串行队列的意义：译文顺序必须和原文一致，而且不能并发打架。
        (SessionPipeline pipeline, CaptionStore store, _, FakeTranscriber transcriber, FakeTranslator translator) = Build();
        await pipeline.StartAsync();

        transcriber.Emit(TranscriptEvent.Finalized("One. Two. Three. Four."));

        await WaitUntilAsync(() => translator.Seen.Count == 4, "四句都该被翻译");

        Assert.Equal(["One.", "Two.", "Three.", "Four."], translator.Seen);
        Assert.False(translator.SawConcurrentCalls, "翻译必须串行，不能并发");

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task TranslatorPrepareFailureDegradesToSourceOnly()
    {
        // 翻译引擎挂了不能让原文字幕也没了——这是 macOS 版明确的降级路径。
        var translator = new FakeTranslator
        {
            PrepareError = new TranslatorException("语言包没装", "到设置里下载"),
        };
        (SessionPipeline pipeline, CaptionStore store, _, FakeTranscriber transcriber, _) = Build(translator: translator);

        await pipeline.StartAsync();

        Assert.Equal(PipelineState.Running, pipeline.State);
        Assert.NotNull(store.StatusMessage);
        Assert.Contains("只显示原文", store.StatusMessage);
        Assert.Contains("到设置里下载", store.StatusMessage);

        transcriber.Emit(TranscriptEvent.Finalized("Still shows the source."));
        await WaitUntilAsync(() => store.Lines.Count == 1, "原文仍然要出");

        await Task.Delay(50);
        Assert.Null(store.Lines[0].Translation);
        Assert.Empty(translator.Seen);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task TranscriberPrepareFailureFailsTheSession()
    {
        // 识别器是硬依赖，它挂了整条管道没有意义，必须明确失败而不是假装在跑。
        var transcriber = new FakeTranscriber
        {
            PrepareError = new TranscriberException("模型文件找不到"),
        };
        (SessionPipeline pipeline, CaptionStore store, FakeAudioSource audio, _, _) = Build(transcriber: transcriber);

        await pipeline.StartAsync();

        Assert.Equal(PipelineState.Failed, pipeline.State);
        Assert.False(audio.Started, "识别器都没起来，不该去开麦");
        Assert.Equal("模型文件找不到", store.StatusMessage);
    }

    [Fact]
    public async Task AudioStartFailureFailsTheSession()
    {
        var audio = new FakeAudioSource
        {
            StartError = new InvalidOperationException("没有可用的音频输出设备"),
        };
        (SessionPipeline pipeline, CaptionStore store, _, _, _) = Build(audio: audio);

        await pipeline.StartAsync();

        Assert.Equal(PipelineState.Failed, pipeline.State);
        Assert.Equal("没有可用的音频输出设备", store.StatusMessage);
    }

    [Fact]
    public async Task AudioFailureMidSessionSurfacesToUser()
    {
        (SessionPipeline pipeline, CaptionStore store, FakeAudioSource audio, _, _) = Build();
        await pipeline.StartAsync();

        audio.EmitFailure(new InvalidOperationException("耳机被拔了"));

        Assert.Equal(PipelineState.Failed, pipeline.State);
        Assert.Contains("耳机被拔了", store.StatusMessage);
    }

    [Fact]
    public async Task StopFlushesTheLastSentence()
    {
        // 对方最后一句没有句末标点就停了，停止会话时必须冲刷出来，不能吞掉。
        (SessionPipeline pipeline, CaptionStore store, _, FakeTranscriber transcriber, _) = Build();
        await pipeline.StartAsync();

        transcriber.Emit(TranscriptEvent.Finalized("one last thing with no period"));
        Assert.Empty(store.Lines);

        await pipeline.StopAsync();

        Assert.Single(store.Lines);
        Assert.Equal("one last thing with no period", store.Lines[0].Source);
        Assert.Equal(string.Empty, store.PendingSource);
        Assert.True(transcriber.Finished);
        Assert.Equal(PipelineState.Idle, pipeline.State);
    }

    [Fact]
    public async Task PauseStopsAudioButKeepsTranscriber()
    {
        (SessionPipeline pipeline, CaptionStore store, FakeAudioSource audio, FakeTranscriber transcriber, _) = Build();
        await pipeline.StartAsync();

        await pipeline.PauseAsync();

        Assert.Equal(PipelineState.Paused, pipeline.State);
        Assert.True(audio.Stopped);
        Assert.False(transcriber.Finished, "暂停不该拆掉识别器，否则恢复时要重新加载模型");
        Assert.False(store.IsRunning);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task ToggleCyclesThroughStates()
    {
        (SessionPipeline pipeline, _, _, _, _) = Build();

        await pipeline.ToggleAsync();
        Assert.Equal(PipelineState.Running, pipeline.State);

        await pipeline.ToggleAsync();
        Assert.Equal(PipelineState.Paused, pipeline.State);

        await pipeline.ToggleAsync();
        Assert.Equal(PipelineState.Running, pipeline.State);

        await pipeline.StopAsync();
        Assert.Equal(PipelineState.Idle, pipeline.State);
    }

    [Fact]
    public async Task StartIsIdempotentWhileRunning()
    {
        (SessionPipeline pipeline, CaptionStore store, _, FakeTranscriber transcriber, _) = Build();
        await pipeline.StartAsync();
        transcriber.Emit(TranscriptEvent.Finalized("First question."));
        await WaitUntilAsync(() => store.Lines.Count == 1, "第一句应该在");

        // 再点一次「开始」不能把已有内容清掉
        await pipeline.StartAsync();

        Assert.Single(store.Lines);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task FailedTranscriptEventShowsStatusWithoutKillingSession()
    {
        (SessionPipeline pipeline, CaptionStore store, _, FakeTranscriber transcriber, _) = Build();
        await pipeline.StartAsync();

        transcriber.Emit(TranscriptEvent.Failed("解码出错"));

        Assert.Equal(PipelineState.Running, pipeline.State);
        Assert.Contains("识别中断", store.StatusMessage);
        Assert.Empty(store.Lines);

        await pipeline.StopAsync();
    }
}
