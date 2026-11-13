using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AwesomeRealtimeTranslator.Core;

namespace AwesomeRealtimeTranslator.Caption;

/// <summary>
/// 逐字显现的文本控件，对应 macOS 版的 StreamingText。
///
/// 所有的节奏数学都在 <see cref="RevealPacer"/> 里（平台无关、有单测），
/// 这个控件只负责「每帧问一次该显示几个字，然后显示」。
///
/// 用 <see cref="CompositionTarget.Rendering"/> 而不是 DispatcherTimer：
/// 它跟着 WPF 的渲染帧走，不会出现「定时器触发了但这一帧没渲染」的空转，
/// 也不会和渲染抢时间片。
/// </summary>
public sealed class StreamingTextBlock : TextBlock
{
    public static readonly DependencyProperty FullTextProperty = DependencyProperty.Register(
        nameof(FullText),
        typeof(string),
        typeof(StreamingTextBlock),
        new PropertyMetadata(string.Empty, OnFullTextChanged));

    public static readonly DependencyProperty PaceProperty = DependencyProperty.Register(
        nameof(Pace),
        typeof(double),
        typeof(StreamingTextBlock),
        new PropertyMetadata(0.5, OnPaceChanged));

    private RevealPacer _pacer = new(0.5);
    private long _lastTicks;
    private bool _subscribed;

    public StreamingTextBlock()
    {
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    /// <summary>完整文本。显示的是它的前缀，长度由 pacer 决定。</summary>
    public string FullText
    {
        get => (string)GetValue(FullTextProperty);
        set => SetValue(FullTextProperty, value);
    }

    /// <summary>显现节奏（秒），见 <see cref="RevealPacer.Pace"/>。</summary>
    public double Pace
    {
        get => (double)GetValue(PaceProperty);
        set => SetValue(PaceProperty, value);
    }

    private static void OnFullTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (StreamingTextBlock)d;

        // 文本清空（上一句定稿了）时，进度必须归零，否则下一句会从半截开始显示。
        if (string.IsNullOrEmpty((string)e.NewValue))
        {
            self._pacer.Reset();
            self.Text = string.Empty;
        }

        self.Subscribe();
    }

    private static void OnPaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (StreamingTextBlock)d;

        // RevealPacer 的 Pace 是只读的（构造时夹紧），换节奏就换一个实例。
        // 进度不继承：设置项是用户主动改的，这一瞬间重新起步不会被察觉。
        self._pacer = new RevealPacer((double)e.NewValue);
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _lastTicks = DateTime.UtcNow.Ticks;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = DateTime.UtcNow.Ticks;
        double delta = (now - _lastTicks) / (double)TimeSpan.TicksPerSecond;
        _lastTicks = now;

        // 窗口被遮住或者机器卡了一下，delta 可能很大。截断一下，
        // 否则恢复的那一帧会把积压一次性全放出来，正好是要避免的「跳一下」。
        delta = Math.Clamp(delta, 0, 0.1);

        string full = FullText ?? string.Empty;
        int revealed = _pacer.Advance(full.Length, delta);

        if (revealed >= full.Length)
        {
            // 已经放完，这一帧之后没事可做，退订以免每帧空转。
            // 新文本进来时 OnFullTextChanged 会重新订阅。
            if (Text != full)
            {
                Text = full;
            }

            Unsubscribe();
            return;
        }

        string next = full[..revealed];
        if (Text != next)
        {
            Text = next;
        }
    }
}
