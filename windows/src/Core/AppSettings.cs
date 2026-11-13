using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AwesomeRealtimeTranslator.Core;

public enum DisplayMode
{
    SourceOnly,
    TranslationOnly,
    Bilingual,
}

public static class DisplayModeExtensions
{
    public static string Label(this DisplayMode mode) => mode switch
    {
        DisplayMode.SourceOnly => "仅原文",
        DisplayMode.TranslationOnly => "仅译文",
        _ => "双语对照",
    };

    public static bool ShowsSource(this DisplayMode mode) => mode != DisplayMode.TranslationOnly;

    public static bool ShowsTranslation(this DisplayMode mode) => mode != DisplayMode.SourceOnly;
}

/// <summary>
/// 设置的持久化后端。抽象出来是为了让 Core 保持平台无关：
/// 真实实现写 %APPDATA%，单测用内存实现。
/// </summary>
public interface ISettingsStore
{
    string? GetString(string key);

    double? GetDouble(string key);

    int? GetInt(string key);

    bool? GetBool(string key);

    IReadOnlyList<string>? GetStringList(string key);

    void Set(string key, object? value);
}

/// <summary>内存实现，单测用。</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, object?> _values = [];

    public string? GetString(string key) => _values.GetValueOrDefault(key) as string;

    public double? GetDouble(string key) => _values.GetValueOrDefault(key) as double?;

    public int? GetInt(string key) => _values.GetValueOrDefault(key) as int?;

    public bool? GetBool(string key) => _values.GetValueOrDefault(key) as bool?;

    public IReadOnlyList<string>? GetStringList(string key) =>
        _values.GetValueOrDefault(key) as IReadOnlyList<string>;

    public void Set(string key, object? value) => _values[key] = value;
}

/// <summary>
/// 用户设置。改一处就落盘，和 macOS 版的 UserDefaults 行为一致。
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private readonly ISettingsStore _store;

    private string _sourceLanguage;
    private string _targetLanguage;
    private DisplayMode _displayMode;
    private double _fontSize;
    private double _opacity;
    private int _visibleLines;
    private bool _clickThrough;
    private double _revealPace;
    private bool _hideFromScreenSharing;
    private bool _saveTranscripts;
    private IReadOnlyList<string> _glossary;

    public AppSettings(ISettingsStore store)
    {
        _store = store;
        _sourceLanguage = store.GetString("sourceLanguage") ?? "en";
        _targetLanguage = store.GetString("targetLanguage") ?? "zh";
        _displayMode = Enum.TryParse(store.GetString("displayMode"), out DisplayMode m) ? m : DisplayMode.Bilingual;
        _fontSize = store.GetDouble("fontSize") ?? 17;
        _opacity = store.GetDouble("opacity") ?? 0.82;
        _visibleLines = store.GetInt("visibleLines") ?? 3;
        _clickThrough = store.GetBool("clickThrough") ?? false;
        _revealPace = Math.Clamp(store.GetDouble("revealPace") ?? 0.5, RevealPacer.MinPace, RevealPacer.MaxPace);
        _hideFromScreenSharing = store.GetBool("hideFromScreenSharing") ?? true;
        _saveTranscripts = store.GetBool("saveTranscripts") ?? false;
        _glossary = store.GetStringList("glossary") ?? [];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>面试官说的语言</summary>
    public string SourceLanguage
    {
        get => _sourceLanguage;
        set => SetField(ref _sourceLanguage, value, "sourceLanguage");
    }

    /// <summary>你要读的语言</summary>
    public string TargetLanguage
    {
        get => _targetLanguage;
        set => SetField(ref _targetLanguage, value, "targetLanguage");
    }

    public DisplayMode DisplayMode
    {
        get => _displayMode;
        set => SetField(ref _displayMode, value, "displayMode", v => v.ToString());
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetField(ref _fontSize, Math.Clamp(value, 11, 36), "fontSize");
    }

    public double Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, Math.Clamp(value, 0.3, 1.0), "opacity");
    }

    public int VisibleLines
    {
        get => _visibleLines;
        set => SetField(ref _visibleLines, Math.Clamp(value, 1, 10), "visibleLines");
    }

    public bool ClickThrough
    {
        get => _clickThrough;
        set => SetField(ref _clickThrough, value, "clickThrough");
    }

    /// <summary>
    /// 原文显现节奏：把一批到达的字摊开播放的时长（秒），也等于显示比识别结果滞后多少。
    /// 区间锁在 [0.3, 1.0]，理由见 <see cref="RevealPacer"/>。
    /// </summary>
    public double RevealPace
    {
        get => _revealPace;
        set => SetField(ref _revealPace, Math.Clamp(value, RevealPacer.MinPace, RevealPacer.MaxPace), "revealPace");
    }

    /// <summary>
    /// 屏幕共享时隐藏字幕窗。实现方式是 Win32 的
    /// <c>SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)</c>，
    /// 窗口对所有录屏/共享 API 不可见，但你自己仍然看得见 —— 和 macOS 版
    /// <c>NSWindow.sharingType = .none</c> 是同一个语义，所以同样默认开启。
    /// </summary>
    public bool HideFromScreenSharing
    {
        get => _hideFromScreenSharing;
        set => SetField(ref _hideFromScreenSharing, value, "hideFromScreenSharing");
    }

    /// <summary>
    /// 默认<b>不</b>保存。面试记录含第三方可识别信息，默认留痕对隐私不友好。
    /// </summary>
    public bool SaveTranscripts
    {
        get => _saveTranscripts;
        set => SetField(ref _saveTranscripts, value, "saveTranscripts");
    }

    public IReadOnlyList<string> Glossary
    {
        get => _glossary;
        set
        {
            _glossary = value;
            _store.Set("glossary", value.ToList());
            Raise(nameof(Glossary));
        }
    }

    private void SetField<T>(ref T field, T value, string key, Func<T, object?>? project = null, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        _store.Set(key, project is null ? value : project(value));
        Raise(name);
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 可选语言对。只验证过 英→中；其余是框架支持、未实测。
/// 语言代码用的是 Whisper / OPUS-MT 的两字母码，不是 macOS 版的 BCP-47。
/// </summary>
public static class SupportedLanguages
{
    public static readonly IReadOnlyList<(string Id, string Label)> Sources =
    [
        ("en", "英语"),
        ("fr", "法语"),
        ("de", "德语"),
        ("es", "西班牙语"),
        ("ja", "日语"),
        ("zh", "中文（普通话）"),
    ];

    public static readonly IReadOnlyList<(string Id, string Label)> Targets =
    [
        ("zh", "简体中文"),
        ("en", "英语"),
    ];
}
