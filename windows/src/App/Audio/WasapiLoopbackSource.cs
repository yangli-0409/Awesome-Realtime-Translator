using AwesomeRealtimeTranslator.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AwesomeRealtimeTranslator.Audio;

/// <summary>
/// 系统音频采集，对应 macOS 版的 SystemAudioCapture（那边是 ScreenCaptureKit）。
///
/// Windows 这边用 WASAPI loopback：直接从默认输出设备的混音抓一份副本。
/// 比 macOS 简单的地方是<b>不需要任何权限</b>——没有 TCC 那套东西，
/// 也就没有「每次重新编译都要重新授权」的问题。
///
/// 两个必须处理的坑：
///
/// 1. <b>格式</b>。loopback 给的是设备混音格式（通常 48kHz 立体声 32 位浮点），
///    Whisper 只吃 16kHz 单声道。转换放在这里做，不放到识别线程上。
///
/// 2. <b>没人出声时不吐数据</b>。WASAPI loopback 在系统完全静音时根本不给缓冲区，
///    于是「静音了多久」没法靠数采样点来算。两个应对：
///    (a) 播一路静音把音频会话钉住（<see cref="_keepAlive"/>），
///    (b) 静音时长一律按<b>挂钟时间</b>算，不依赖缓冲区到达——见 WhisperTranscriber。
/// </summary>
public sealed class WasapiLoopbackSource : IAudioSource
{
    /// <summary>每次向上游交付的音频长度。20ms 和 macOS 版 ScreenCaptureKit 的实测节奏一致。</summary>
    private const int ChunkSamples = AudioChunk.SampleRate / 50;

    private readonly object _gate = new();

    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _keepAlive;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _chain;
    private float[] _pending = new float[ChunkSamples];
    private int _pendingCount;
    private bool _stopped;

    public event Action<AudioChunk>? ChunkAvailable;

    public event Action<Exception>? Failed;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            throw new InvalidOperationException(
                "找不到正在使用的音频输出设备。插上耳机或音箱，或者在声音设置里选一个输出设备，然后重试。");
        }

        var capture = new WasapiLoopbackCapture();

        var buffered = new BufferedWaveProvider(capture.WaveFormat)
        {
            // 5 秒的余量足够吸收识别线程偶尔的卡顿；满了宁可丢最旧的，
            // 也不能把采集线程卡住——卡住采集就等于整段音频都丢。
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        ISampleProvider samples = buffered.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new StereoToMonoSampleProvider(samples)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            };
        }

        ISampleProvider chain = samples.WaveFormat.SampleRate == AudioChunk.SampleRate
            ? samples
            : new WdlResamplingSampleProvider(samples, AudioChunk.SampleRate);

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        lock (_gate)
        {
            _capture = capture;
            _buffered = buffered;
            _chain = chain;
            _stopped = false;
            _pendingCount = 0;
        }

        capture.StartRecording();
        StartKeepAlive();

        return Task.CompletedTask;
    }

    /// <summary>
    /// 播一路听不见的静音，把音频会话钉住。
    ///
    /// 没有它的话，面试刚开始、对方还没开口的那段时间里 WASAPI loopback 一个缓冲区都不给，
    /// 第一句话的前几百毫秒就会被吞掉。失败了不致命（多数机器上总有别的程序在出声），
    /// 所以只是忽略。
    /// </summary>
    private void StartKeepAlive()
    {
        try
        {
            var output = new WasapiOut(AudioClientShareMode.Shared, 200);
            output.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)).ToSampleProvider());
            output.Play();
            _keepAlive = output;
        }
        catch
        {
            _keepAlive = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        BufferedWaveProvider? buffered;
        ISampleProvider? chain;

        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            buffered = _buffered;
            chain = _chain;
        }

        if (buffered is null || chain is null || e.BytesRecorded == 0)
        {
            return;
        }

        try
        {
            buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
            DrainResampler(chain);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    /// <summary>
    /// 把重采样链里现成的样本抽干，按固定 20ms 一块交付。
    ///
    /// 固定块长的理由：上游的识别器按块数算静音时长，块长忽大忽小会让静音判定抖动。
    /// </summary>
    private void DrainResampler(ISampleProvider chain)
    {
        while (true)
        {
            int want = ChunkSamples - _pendingCount;
            int read = chain.Read(_pending, _pendingCount, want);
            if (read <= 0)
            {
                return;
            }

            _pendingCount += read;
            if (_pendingCount < ChunkSamples)
            {
                return;
            }

            float[] chunk = _pending;
            _pending = new float[ChunkSamples];
            _pendingCount = 0;
            ChunkAvailable?.Invoke(new AudioChunk(chunk));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        bool stopped;
        lock (_gate)
        {
            stopped = _stopped;
        }

        // 主动停止时 Exception 为 null；非 null 说明设备被拔了或被独占模式抢占。
        if (!stopped && e.Exception is not null)
        {
            Failed?.Invoke(e.Exception);
        }
    }

    public Task StopAsync()
    {
        WasapiLoopbackCapture? capture;
        WasapiOut? keepAlive;

        lock (_gate)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }

            _stopped = true;
            capture = _capture;
            keepAlive = _keepAlive;
            _capture = null;
            _keepAlive = null;
            _buffered = null;
            _chain = null;
        }

        try
        {
            capture?.StopRecording();
        }
        catch
        {
            // 设备已经消失时 StopRecording 会抛，停机路径上无所谓
        }

        try
        {
            keepAlive?.Stop();
            keepAlive?.Dispose();
        }
        catch
        {
            // 同上
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
