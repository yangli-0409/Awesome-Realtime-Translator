using System.IO;
using System.Windows;
using System.Windows.Interop;
using AwesomeRealtimeTranslator.Audio;
using AwesomeRealtimeTranslator.Caption;
using AwesomeRealtimeTranslator.Core;
using AwesomeRealtimeTranslator.Core.Translation;
using AwesomeRealtimeTranslator.Interop;
using AwesomeRealtimeTranslator.Settings;
using AwesomeRealtimeTranslator.Transcription;
using Forms = System.Windows.Forms;

namespace AwesomeRealtimeTranslator;

/// <summary>
/// 应用入口与装配，对应 macOS 版的 AwesomeRealtimeTranslatorApp + AppCoordinator 的外壳部分。
/// 管道本身在 <see cref="SessionPipeline"/> 里（平台无关、有单测），这里只负责
/// 把 Windows 的具体实现插进去，再管窗口、托盘和快捷键。
/// </summary>
// 必须写全名：UseWindowsForms + ImplicitUsings 会把 System.Windows.Forms
// 也做成 global using，Application 这个名字就同时存在于 WPF 和 WinForms 两边。
public partial class App : System.Windows.Application
{
    private const int HotKeyToggle = 1;
    private const int HotKeyVisibility = 2;

    private AppSettings _settings = null!;
    private CaptionStore _store = null!;
    private SessionPipeline _pipeline = null!;
    private CaptionWindow _caption = null!;
    private Forms.NotifyIcon? _tray;
    private HwndSource? _hotkeySource;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = new AppSettings(new FileSettingsStore());
        _store = new CaptionStore();

        string? whisperModel = ModelPaths.FindWhisperModel();
        string? translationModel = ModelPaths.FindTranslationModel();

        _pipeline = new SessionPipeline(
            _store,
            _settings,
            () => new WasapiLoopbackSource(),
            () => new WhisperTranscriber(
                whisperModel ?? Path.Combine(AppContext.BaseDirectory, "models", ModelPaths.WhisperModelFileName),
                _settings.SourceLanguage),
            () => translationModel is null
                ? throw new TranslatorException(
                    "找不到翻译模型。",
                    $"把 opus-mt-en-zh 目录放到 {Path.Combine(AppContext.BaseDirectory, "models")} 下面。")
                : new MarianTranslator(translationModel),
            action => Dispatcher.Invoke(action));

        _caption = new CaptionWindow(
            _store,
            _settings,
            onToggle: () => _pipeline.ToggleAsync(),
            onOpenSettings: OpenSettings,
            onQuit: Quit);

        _caption.Show();
        _caption.PlaceAtTopCenter();

        SetUpTray();
        SetUpHotKeys();

        // 和 macOS 版同样的选择：启动后<b>不</b>自动开始采集。
        // 静默录系统声音不该由程序替用户决定。
        _store.StatusMessage = whisperModel is null
            ? "缺少语音识别模型，无法开始。把 ggml-base.bin 放到程序目录的 models 文件夹下。"
            : "点右上角 ▶ 或按 Ctrl+Alt+T 开始听";
    }

    private void SetUpTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "面试字幕",
            Icon = LoadTrayIcon(),
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("开始 / 暂停 (Ctrl+Alt+T)", null, async (_, _) => await _pipeline.ToggleAsync());
        menu.Items.Add("显示 / 隐藏字幕窗 (Ctrl+Alt+H)", null, (_, _) => ToggleCaptionVisibility());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("复制本场记录", null, (_, _) => CopyTranscript());
        menu.Items.Add("导出为 Markdown…", null, (_, _) => ExportTranscript());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add("退出", null, (_, _) => Quit());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleCaptionVisibility();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
        try
        {
            if (File.Exists(path))
            {
                return new System.Drawing.Icon(path);
            }
        }
        catch
        {
            // 图标坏了不该让应用起不来
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>
    /// 全局快捷键。用 RegisterHotKey 而不是键盘钩子：
    /// 钩子要「输入监控」级别的权限、会被杀软盯上，而且面试场景下很敏感。
    /// RegisterHotKey 是系统正规入口，不需要任何额外授权。
    /// </summary>
    private void SetUpHotKeys()
    {
        var helper = new WindowInteropHelper(_caption);
        _hotkeySource = HwndSource.FromHwnd(helper.Handle);
        if (_hotkeySource is null)
        {
            return;
        }

        _hotkeySource.AddHook(HotKeyHook);

        uint modifiers = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT;
        if (!Win32.RegisterHotKey(helper.Handle, HotKeyToggle, modifiers, 'T'))
        {
            _store.StatusMessage = "Ctrl+Alt+T 被别的程序占用了，只能用字幕窗上的按钮。";
        }

        Win32.RegisterHotKey(helper.Handle, HotKeyVisibility, modifiers, 'H');
    }

    private IntPtr HotKeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Win32.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        switch (wParam.ToInt32())
        {
            case HotKeyToggle:
                handled = true;
                _ = _pipeline.ToggleAsync();
                break;

            case HotKeyVisibility:
                handled = true;
                ToggleCaptionVisibility();
                break;

            default:
                break;
        }

        return IntPtr.Zero;
    }

    private void ToggleCaptionVisibility()
    {
        if (_caption.IsVisible)
        {
            _caption.Hide();
        }
        else
        {
            _caption.Show();
        }
    }

    private void OpenSettings() => SettingsWindow.Show(_settings, ModelPaths.DescribeLocation());

    private void CopyTranscript()
    {
        if (_store.Lines.Count == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_store.PlainText);
            _store.StatusMessage = "本场记录已复制到剪贴板。";
        }
        catch
        {
            _store.StatusMessage = "剪贴板被别的程序占用，复制失败，请稍后再试。";
        }
    }

    private void ExportTranscript()
    {
        if (_store.Lines.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"面试记录-{DateTime.Now:yyyyMMdd-HHmm}.md",
            Filter = "Markdown (*.md)|*.md",
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, _store.Markdown);
        }
    }

    private void Quit()
    {
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            await _pipeline.StopAsync();

            if (_settings.SaveTranscripts && _store.Lines.Count > 0)
            {
                SaveTranscript();
            }
        }
        catch
        {
            // 退出路径上出什么事都得退出去
        }
        finally
        {
            Shutdown();
        }
    }

    private void SaveTranscript()
    {
        try
        {
            string dir = Path.Combine(FileSettingsStore.DefaultDirectory, "transcripts");
            Directory.CreateDirectory(dir);
            string name = $"{_store.SessionStartedAt ?? DateTimeOffset.Now:yyyyMMdd-HHmmss}.md";
            File.WriteAllText(Path.Combine(dir, name), _store.Markdown);
        }
        catch
        {
            // 存不下也不该阻止退出
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotkeySource is not null)
        {
            IntPtr handle = _hotkeySource.Handle;
            Win32.UnregisterHotKey(handle, HotKeyToggle);
            Win32.UnregisterHotKey(handle, HotKeyVisibility);
            _hotkeySource.RemoveHook(HotKeyHook);
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.OnExit(e);
    }
}
