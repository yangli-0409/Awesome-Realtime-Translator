using System.Diagnostics;
using System.IO;
using AwesomeRealtimeTranslator.Core;
using Whisper.net;

namespace AwesomeRealtimeTranslator.Transcription;

/// <summary>
/// 基于 whisper.cpp 的流式识别器，对应 macOS 版的 SpeechAnalyzerTranscriber。
///
/// <b>这是整个 Windows 版与 Mac 版差别最大的一块，必须说清楚。</b>
///
/// macOS 26 的 SpeechAnalyzer 本身就是流式的：喂音频进去，它自己吐 volatile 和
/// finalized 事件。Windows 没有对等的系统 API——Win11 的实时字幕是系统功能、
/// 不对外开放；旧的 System.Speech 是命令词识别，做不了自由听写。所以这里用
/// whisper.cpp，而 Whisper 是<b>非流式</b>的：它一次吃一整段音频。
///
/// 于是流式语义得自己造，做法是「滑动窗口 + 重复解码」：
///
/// <list type="number">
/// <item>把上次定稿之后的音频攒在窗口里。</item>
/// <item>每 <see cref="StepInterval"/> 把<b>整个窗口</b>重新解码一次，结果作为 Volatile 发出去。
///       所以同一段音频会被反复解码好几遍——这是 CPU 换「边说边出字」。</item>
/// <item>检测到对方停顿超过 <see cref="SilenceGap"/>，再解码一次，作为 Finalized 发出去并清空窗口。</item>
/// <item>窗口超过 <see cref="MaxWindowSeconds"/> 也强制定稿，否则解码耗时会随窗口线性增长。</item>
/// </list>
///
/// 静音判定一律按<b>挂钟时间</b>算，不靠数静音采样点：WASAPI loopback 在系统完全
/// 没声音时根本不给缓冲区（见 WasapiLoopbackSource），数采样点会永远数不满。
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    /// <summary>多久重新解码一次窗口，决定「边说边出字」的刷新率。</summary>
    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>对方停顿多久算一句说完。和 Segmenter 的 0.45s 配合，这里取稍长一点。</summary>
    private static readonly TimeSpan SilenceGap = TimeSpan.FromMilliseconds(600);

    /// <summary>窗口上限。超过就强制定稿，防止解码耗时失控。</summary>
    private const double MaxWindowSeconds = 18.0;

    /// <summary>低于这个时长的窗口不值得解码，纯属浪费 CPU。</summary>
    private const double MinWindowSeconds = 0.4;

    /// <summary>RMS 超过这个值算「有人在说话」。经验值，对应约 -46dBFS。</summary>
    private const double SpeechRms = 0.005;

    private readonly string _modelPath;
    private readonly string _language;
    private readonly int _threads;
    private readonly object _gate = new();
    private readonly List<float> _window = [];

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastSpeechTicks;
    private bool _windowHasSpeech;
    private string _lastVolatile = string.Empty;

    public WhisperTranscriber(string modelPath, string language, int? threads = null)
    {
        _modelPath = modelPath;
        _language = language;
        _threads = threads ?? Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
    }

    public event Action<TranscriptEvent>? Event;

    public Task PrepareAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_modelPath))
        {
            throw new TranscriberException(
                $"找不到语音识别模型：{_modelPath}\n" +
                "正常安装包里自带模型。如果你是自己从源码构建的，运行 tools/fetch-models.ps1 下载。");
        }

        progress?.Report("正在加载语音识别模型…");

        try
        {
            // 加载是 CPU 密集的同步调用（base 模型约 1~3 秒），放到线程池上，
            // 否则会把 UI 线程卡住，用户会以为点了没反应。
            return Task.Run(
                () =>
                {
                    WhisperFactory factory = WhisperFactory.FromPath(_modelPath);
                    WhisperProcessor processor = factory.CreateBuilder()
                        .WithLanguage(_language)
                        .WithThreads(_threads)
                        // 每一步都是对同一段音频重新解码，不能让上一步的结果当上下文，
                        // 否则模型会越跑越偏，出现滚雪球式的幻觉。
                        .WithNoContext()
                        // 温度 0：同一段音频每次解码结果必须一致。
                        // 不一致的话屏幕上的字会自己改来改去，比延迟难受得多。
                        .WithTemperature(0f)
                        .WithNoSpeechThreshold(0.6f)
                        .Build();

                    lock (_gate)
                    {
                        _factory = factory;
                        _processor = processor;
                    }
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not TranscriberException)
        {
            throw new TranscriberException($"语音识别模型加载失败：{ex.Message}", ex);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_processor is null)
            {
                throw new TranscriberException("识别器还没准备好，请先调用 PrepareAsync。");
            }

            _window.Clear();
            _windowHasSpeech = false;
            _lastVolatile = string.Empty;
            _lastSpeechTicks = Stopwatch.GetTimestamp();
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public void Append(AudioChunk chunk)
    {
        double rms = Rms(chunk.Samples);

        lock (_gate)
        {
            _window.AddRange(chunk.Samples);
            if (rms >= SpeechRms)
            {
                _windowHasSpeech = true;
                _lastSpeechTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    private static double Rms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (float s in samples)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StepInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            float[] snapshot;
            bool hasSpeech;
            double silentFor;

            lock (_gate)
            {
                snapshot = [.. _window];
                hasSpeech = _windowHasSpeech;
                silentFor = Stopwatch.GetElapsedTime(_lastSpeechTicks).TotalSeconds;
            }

            double seconds = snapshot.Length / (double)AudioChunk.SampleRate;
            if (!hasSpeech || seconds < MinWindowSeconds)
            {
                continue;
            }

            bool shouldFinalize = silentFor >= SilenceGap.TotalSeconds || seconds >= MaxWindowSeconds;

            string text;
            try
            {
                text = await DecodeAsync(snapshot, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Event?.Invoke(TranscriptEvent.Failed(ex.Message));
                continue;
            }

            if (shouldFinalize)
            {
                lock (_gate)
                {
                    // 只丢掉已经解码过的部分：解码期间可能又进来了新音频，
                    // 那部分必须留给下一个窗口，否则对方说话快的时候会吞字。
                    int consumed = Math.Min(snapshot.Length, _window.Count);
                    _window.RemoveRange(0, consumed);
                    _windowHasSpeech = false;
                    _lastVolatile = string.Empty;
                }

                if (text.Length > 0)
                {
                    Event?.Invoke(TranscriptEvent.Finalized(text));
                }

                continue;
            }

            // 文本没变就不发：下游会据此刷新 UI，重复事件等于白刷一帧。
            if (text.Length > 0 && text != _lastVolatile)
            {
                _lastVolatile = text;
                Event?.Invoke(TranscriptEvent.Volatile(text));
            }
        }
    }

    private async Task<string> DecodeAsync(float[] samples, CancellationToken token)
    {
        WhisperProcessor? processor;
        lock (_gate)
        {
            processor = _processor;
        }

        if (processor is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        await foreach (SegmentData segment in processor.ProcessAsync(samples, token).ConfigureAwait(false))
        {
            parts.Add(segment.Text);
        }

        return Clean(string.Concat(parts));
    }

    /// <summary>
    /// whisper.cpp 会把非语音事件写成 <c>[BLANK_AUDIO]</c>、<c>(music)</c> 这类标记，
    /// 还会在每段前面加一个空格。直接显示到字幕上很难看，在这里统一清掉。
    /// </summary>
    private static string Clean(string raw)
    {
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = raw.AsSpan().Trim();
        var sb = new System.Text.StringBuilder(span.Length);
        int depth = 0;

        foreach (char c in span)
        {
            switch (c)
            {
                case '[' or '(':
                    depth++;
                    break;
                case ']' or ')':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                default:
                    if (depth == 0)
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString().Trim();
    }

    public async Task FinishAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? loop = _loop;
        _cts = null;
        _loop = null;

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停机
            }
        }

        cts?.Dispose();

        // 窗口里可能还剩最后一句没定稿的话，解码一次发出去，不能吞掉。
        float[] remaining;
        bool hasSpeech;
        lock (_gate)
        {
            remaining = [.. _window];
            hasSpeech = _windowHasSpeech;
            _window.Clear();
            _windowHasSpeech = false;
        }

        if (!hasSpeech || remaining.Length / (double)AudioChunk.SampleRate < MinWindowSeconds)
        {
            return;
        }

        try
        {
            string text = await DecodeAsync(remaining, CancellationToken.None).ConfigureAwait(false);
            if (text.Length > 0)
            {
                Event?.Invoke(TranscriptEvent.Finalized(text));
            }
        }
        catch
        {
            // 停机路径上解码失败无所谓，不值得打扰用户
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FinishAsync().ConfigureAwait(false);

        WhisperProcessor? processor;
        WhisperFactory? factory;
        lock (_gate)
        {
            processor = _processor;
            factory = _factory;
            _processor = null;
            _factory = null;
        }

        if (processor is not null)
        {
            await processor.DisposeAsync().ConfigureAwait(false);
        }

        factory?.Dispose();
    }
}
