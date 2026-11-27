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

    private FileSettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;
    private CaptionStore _store = null!;
    private SessionPipeline _pipeline = null!;
    private CaptionWindow _caption = null!;
    private Forms.NotifyIcon? _tray;
    private HwndSource? _hotkeySource;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settingsStore = new FileSettingsStore();
        _settings = new AppSettings(_settingsStore);
        _store = new CaptionStore();

        string? whisperModel = ModelPaths.FindWhisperModel();
        string? translationModel = ModelPaths.FindTranslationModel();

        Diagnostics.Log.WriteStartupBanner(whisperModel, translationModel);

        // 后台线程上没人接的异常会静默杀掉进程，对用户就是「突然就没了」。
        // 至少先记下来。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diagnostics.Log.Error("未处理的异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diagnostics.Log.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };

        // UI 线程的异常走的是 Dispatcher，上面那两个都接不到它。
        // 原来没挂这个钩子，于是任何一个界面上的异常都会直接把进程带走——
        // 用户看到的是「点一下就闪退」，而且连个说法都没有。
        //
        // 这里标成已处理：一个设置窗画不出来不该让整个应用消失，
        // 字幕本身还在工作。错误照样写进日志，也当面告诉用户。
        DispatcherUnhandledException += (_, e) =>
        {
            Diagnostics.Log.Error("界面线程异常", e.Exception);
            e.Handled = true;
            MessageBox.Show(
                $"出错了，但程序还在运行，字幕不受影响。\n\n{e.Exception.Message}\n\n" +
                $"详细信息已写入日志：\n{Diagnostics.Log.FilePath}",
                "字幕宝",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

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
            action => Dispatcher.Invoke(action),
            time: null,
            log: (message, ex) => Diagnostics.Log.Error(message, ex));

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

            // 带上版本号：悬停就能看到自己装的是哪一版，不用去翻日志。
            Text = $"字幕宝 {AppVersion.Current}",
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
        // 出问题时让用户能一键找到日志贴给我，而不是去翻 %APPDATA%
        menu.Items.Add("打开日志文件夹", null, (_, _) => OpenLogFolder());
        menu.Items.Add("退出", null, (_, _) => Quit());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleCaptionVisibility();
    }

    /// <summary>
    /// 托盘图标。
    ///
    /// <b>0.2.0 修的。</b>原来是去 <c>输出目录/Resources/app.ico</c> 找文件，
    /// 但 <c>&lt;ApplicationIcon&gt;</c> 只把图标嵌进 exe 的 PE 资源、
    /// 从不把文件拷到输出目录，所以那条路径永远不存在，<c>File.Exists</c> 永远 false，
    /// 托盘从 0.1.0 到 0.1.5 一直是 Windows 通用图标。
    ///
    /// 改成从托管程序集的嵌入资源里读：单文件发布照样在，而且 CI 测得到
    /// （internal 就是为了这个）。按名字后缀找而不是写死全名，免得 RootNamespace
    /// 一改就又悄悄退回通用图标——那正是上一个版本犯的错。
    /// </summary>
    internal static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            System.Reflection.Assembly assembly = typeof(App).Assembly;
            string? name = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));

            if (name is not null)
            {
                using Stream? stream = assembly.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    // 指定小图标尺寸：ico 里有 16~256 七种，不挑的话托盘会拿到
                    // 一张大图再缩，边缘发虚。
                    return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
                }
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

    private void OpenSettings()
    {
        // 托盘菜单的点击回调跑在 WinForms 的嵌套消息循环里，那里抛出去的异常
        // 不一定经过 DispatcherUnhandledException，所以这条路径自己兜一层。
        try
        {
            SettingsWindow.Show(_settings, ModelPaths.DescribeLocation());
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("打开设置窗失败", ex);
            MessageBox.Show(
                $"设置窗打不开，但字幕不受影响。\n\n{ex.Message}\n\n" +
                $"详细信息已写入日志：\n{Diagnostics.Log.FilePath}",
                "字幕宝",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Diagnostics.Log.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Diagnostics.Log.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("打开日志目录失败", ex);
            _store.StatusMessage = $"日志在：{Diagnostics.Log.FilePath}";
        }
    }

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
            // 设置是延迟落盘的，这里必须冲一次，否则关窗前最后改的那下会丢。
            _settingsStore.Dispose();
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
