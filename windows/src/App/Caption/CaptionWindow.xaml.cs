using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AwesomeRealtimeTranslator.Core;
using AwesomeRealtimeTranslator.Interop;

namespace AwesomeRealtimeTranslator.Caption;

/// <summary>
/// 字幕悬浮窗，对应 macOS 版的 CaptionPanelController + CaptionView。
///
/// 两条反闪烁规则（和 macOS 版逐字一致）：
/// <list type="bullet">
/// <item>原文区：历史句子 + 当前未定稿尾句，只有最后一行会动。</item>
/// <item>译文区：只渲染已定稿句子的译文，译文一旦出现就不再改写。</item>
/// </list>
/// </summary>
public partial class CaptionWindow : Window, INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly CaptionStore _store;
    private readonly Func<Task> _onToggle;
    private readonly Action _onQuit;
    private readonly Action _onOpenSettings;
    private bool _captureExclusionFailed;

    public CaptionWindow(
        CaptionStore store,
        AppSettings settings,
        Func<Task> onToggle,
        Action onOpenSettings,
        Action onQuit)
    {
        _store = store;
        _settings = settings;
        _onToggle = onToggle;
        _onOpenSettings = onOpenSettings;
        _onQuit = onQuit;

        InitializeComponent();
        DataContext = this;

        VisibleLines = [];
        _store.Lines.CollectionChanged += (_, _) => RefreshVisibleLines();
        _store.PropertyChanged += OnStorePropertyChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        ApplyOpacityAndPlacement();
        RefreshVisibleLines();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptionStore Store => _store;

    public AppSettings Settings => _settings;

    /// <summary>窗口里实际渲染的几行，等于 store 的尾部 N 条。</summary>
    public ObservableCollection<CaptionLine> VisibleLines { get; }

    public Visibility SourceVisibility =>
        _settings.DisplayMode.ShowsSource() ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TranslationVisibility =>
        _settings.DisplayMode.ShowsTranslation() ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>译文比原文大一号，和 macOS 版一致（译文是用户真正要读的那一行）。</summary>
    public double TranslationFontSize => _settings.FontSize + 1;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32.MakeNonActivating(hwnd);
        ApplyCaptureExclusion(hwnd);
        ApplyClickThrough(hwnd);
    }

    /// <summary>
    /// 应用「录屏时不可见」，并且<b>回读确认</b>。
    ///
    /// 这是整个产品最关键的一个承诺，不能只看 API 返回值。Windows 10 2004 以下
    /// 不支持 EXCLUDEFROMCAPTURE，这种情况必须明确告诉用户——「以为对方看不到、
    /// 其实看得到」是最严重的失败模式。
    /// </summary>
    private void ApplyCaptureExclusion(IntPtr hwnd)
    {
        if (!_settings.HideFromScreenSharing)
        {
            Win32.SetExcludedFromCapture(hwnd, false);
            _captureExclusionFailed = false;
            return;
        }

        Win32.SetExcludedFromCapture(hwnd, true);
        bool ok = Win32.IsExcludedFromCapture(hwnd);
        _captureExclusionFailed = !ok;

        if (!ok)
        {
            _store.StatusMessage =
                "这台电脑的 Windows 版本太旧，字幕窗无法对屏幕共享隐藏。" +
                "共享屏幕前请自己先用会议软件的预览确认一次。";
        }
    }

    private void ApplyClickThrough(IntPtr hwnd)
    {
        Win32.SetClickThrough(hwnd, _settings.ClickThrough);
        ClickThroughHint.Visibility = _settings.ClickThrough ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CaptionStore.PendingSource))
        {
            ScrollToBottom();
        }
        else if (e.PropertyName == nameof(CaptionStore.IsRunning))
        {
            ToggleButton.Content = _store.IsRunning ? "⏸" : "▶";
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.VisibleLines):
                RefreshVisibleLines();
                break;

            case nameof(AppSettings.DisplayMode):
                Raise(nameof(SourceVisibility));
                Raise(nameof(TranslationVisibility));
                break;

            case nameof(AppSettings.FontSize):
                Raise(nameof(TranslationFontSize));
                break;

            case nameof(AppSettings.Opacity):
                ApplyOpacityAndPlacement();
                break;

            case nameof(AppSettings.ClickThrough):
            case nameof(AppSettings.HideFromScreenSharing):
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ApplyClickThrough(hwnd);
                    ApplyCaptureExclusion(hwnd);
                }

                break;

            default:
                break;
        }
    }

    private void ApplyOpacityAndPlacement()
    {
        byte alpha = (byte)Math.Clamp(_settings.Opacity * 255, 0, 255);
        Root.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
    }

    /// <summary>把窗口摆到屏幕顶部居中，和 macOS 版的初始位置一致。</summary>
    public void PlaceAtTopCenter()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = Math.Max(0, (screenWidth - Width) / 2);
        Top = 60;
    }

    private void RefreshVisibleLines()
    {
        IReadOnlyList<CaptionLine> tail = _store.Tail(_settings.VisibleLines);

        // 整个清空再填会让 ItemsControl 重建所有可视元素，表现为闪一下。
        // 只改变化的部分。
        while (VisibleLines.Count > tail.Count)
        {
            VisibleLines.RemoveAt(0);
        }

        for (int i = 0; i < tail.Count; i++)
        {
            if (i < VisibleLines.Count)
            {
                if (!ReferenceEquals(VisibleLines[i], tail[i]))
                {
                    VisibleLines[i] = tail[i];
                }
            }
            else
            {
                VisibleLines.Add(tail[i]);
            }
        }

        ScrollToBottom();
    }

    /// <summary>
    /// 刻意<b>不加动画</b>。
    ///
    /// 流式原文每秒要变好几次，每次变化都触发一次滚动；带动画的话上一次还没跑完
    /// 就被下一次打断，看起来就是持续抖动——macOS 版用户反馈的「像卡顿」主要来自这里。
    /// 无动画的瞬时定位反而稳定。
    /// </summary>
    private void ScrollToBottom() =>
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => Scroller.ScrollToEnd()));

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e) => await _onToggle();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _onOpenSettings();

    private void OnQuitClick(object sender, RoutedEventArgs e) => _onQuit();

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>状态消息为空时整行不占位。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToRunningConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "正在听" : "已暂停";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Running = new(Color.FromRgb(0x3D, 0xDC, 0x97));
    private static readonly SolidColorBrush Idle = new(Color.FromRgb(0x7A, 0x8A, 0x99));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Running : Idle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
