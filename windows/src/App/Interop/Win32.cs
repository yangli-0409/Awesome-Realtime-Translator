using System.Runtime.InteropServices;

namespace AwesomeRealtimeTranslator.Interop;

/// <summary>
/// 字幕窗需要的几个 Win32 窗口标志。
///
/// 这几行是整个 Windows 版能成立的前提，逐条对应 macOS 版的某个 NSWindow 属性：
///
/// <code>
/// macOS                              Windows
/// ─────────────────────────────────  ──────────────────────────────────────
/// sharingType = .none                SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE)
/// canBecomeKey = false               WS_EX_NOACTIVATE
/// LSUIElement（不进 Dock）            WS_EX_TOOLWINDOW（不进 Alt-Tab / 任务栏）
/// ignoresMouseEvents                 WS_EX_TRANSPARENT
/// level = .screenSaver               Topmost（在 XAML 里设）
/// </code>
/// </summary>
internal static partial class Win32
{
    /// <summary>
    /// 窗口对截屏和屏幕共享完全不可见，但本人正常看得见。
    /// Windows 10 2004（19041）起支持；更早的系统上这个调用会失败，
    /// 那时只能退回 <see cref="WDA_MONITOR"/>（整窗变黑，自己也看不见）。
    /// </summary>
    internal const uint WDA_NONE = 0x00000000;

    internal const uint WDA_MONITOR = 0x00000001;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    internal const int GWL_EXSTYLE = -20;

    /// <summary>点击窗口不把焦点抢过来——用户在会议软件里打字不会被打断。</summary>
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>不出现在任务栏和 Alt-Tab 列表里。</summary>
    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>鼠标穿透：点击落到后面的窗口上。</summary>
    internal const int WS_EX_TRANSPARENT = 0x00000020;

    internal const int WS_EX_LAYERED = 0x00080000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;

    /// <summary>按键不会触发系统的「粘滞键」提示音。</summary>
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const int WM_HOTKEY = 0x0312;

    /// <summary>
    /// 一次性给窗口加上「不抢焦点 + 不进任务栏」。
    ///
    /// 必须在窗口句柄已经存在之后调用（WPF 里是 SourceInitialized 之后），
    /// 否则 hWnd 还是 IntPtr.Zero，调用静默失败。
    /// </summary>
    internal static void MakeNonActivating(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>开关鼠标穿透。</summary>
    internal static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        int updated = enabled
            ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, updated);
    }

    /// <summary>
    /// 开关「录屏时不可见」。
    /// </summary>
    /// <returns>
    /// 是否设置成功。Windows 10 2004 以下不支持 EXCLUDEFROMCAPTURE，会返回 false——
    /// 这时必须<b>明确告诉用户</b>，因为「以为对方看不到、其实看得到」是这个产品
    /// 最严重的失败模式，宁可不用也不能让人误以为安全。
    /// </returns>
    internal static bool SetExcludedFromCapture(IntPtr hwnd, bool excluded) =>
        SetWindowDisplayAffinity(hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

    /// <summary>回读一次，确认设置真的生效了，而不是只相信返回值。</summary>
    internal static bool IsExcludedFromCapture(IntPtr hwnd) =>
        GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;
}
