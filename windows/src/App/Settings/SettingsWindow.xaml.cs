using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AwesomeRealtimeTranslator.Core;

namespace AwesomeRealtimeTranslator.Settings;

/// <summary>
/// 设置窗，对应 macOS 版的 SettingsWindow + SettingsView。
///
/// 和 macOS 版一样是手工管理的单例窗口。那边的理由是 SwiftUI 的 Settings 场景
/// 要走 responder chain，而字幕窗不接受键盘焦点导致责任链是空的；
/// 这边没有那个问题，但保持同样的结构更好对照，也省得出现两个设置窗。
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private readonly AppSettings _settings;
    private bool _loading = true;

    private SettingsWindow(AppSettings settings, string modelDirectory)
    {
        _settings = settings;
        InitializeComponent();

        foreach (DisplayMode mode in Enum.GetValues<DisplayMode>())
        {
            DisplayModeBox.Items.Add(new ComboBoxItem { Content = mode.Label(), Tag = mode });
        }

        DisplayModeBox.SelectedIndex = Array.IndexOf(Enum.GetValues<DisplayMode>(), settings.DisplayMode);

        foreach ((string id, string label) in SupportedLanguages.Sources)
        {
            SourceLanguageBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        }

        SelectLanguage(settings.SourceLanguage);

        FontSizeSlider.Value = settings.FontSize;
        OpacitySlider.Value = settings.Opacity;
        LinesSlider.Value = settings.VisibleLines;
        PaceSlider.Value = settings.RevealPace;
        ClickThroughBox.IsChecked = settings.ClickThrough;
        HideFromSharingBox.IsChecked = settings.HideFromScreenSharing;
        SaveTranscriptsBox.IsChecked = settings.SaveTranscripts;
        ModelPathText.Text = $"模型目录：{modelDirectory}";

        UpdateLabels();
        _loading = false;
    }

    public static void Show(AppSettings settings, string modelDirectory)
    {
        if (_instance is null)
        {
            _instance = new SettingsWindow(settings, modelDirectory);
            _instance.Closed += (_, _) => _instance = null;
        }

        _instance.Show();
        _instance.Activate();
    }

    private void SelectLanguage(string id)
    {
        for (int i = 0; i < SourceLanguageBox.Items.Count; i++)
        {
            if (SourceLanguageBox.Items[i] is ComboBoxItem { Tag: string tag } && tag == id)
            {
                SourceLanguageBox.SelectedIndex = i;
                return;
            }
        }

        SourceLanguageBox.SelectedIndex = 0;
    }

    private void UpdateLabels()
    {
        FontSizeValue.Text = ((int)FontSizeSlider.Value).ToString(CultureInfo.InvariantCulture);
        OpacityValue.Text = OpacitySlider.Value.ToString("P0", CultureInfo.InvariantCulture);
        LinesValue.Text = ((int)LinesSlider.Value).ToString(CultureInfo.InvariantCulture);
        PaceValue.Text = PaceSlider.Value.ToString("0.00", CultureInfo.InvariantCulture) + "s";
        PaceHint.Text = DescribePace(PaceSlider.Value);
    }

    /// <summary>
    /// 把一个抽象的秒数翻译成用户能判断的观感。数字本身对用户没有意义，
    /// 「跟手但会跳」和「顺滑但滞后」才是他在选的东西。
    /// </summary>
    private static string DescribePace(double pace) => pace switch
    {
        < 0.4 => "最跟手，但能看到字一跳一跳地出现。",
        < 0.6 => "默认。基本连续，每批末尾有轻微停顿。",
        < 0.9 => "更顺滑，字比对方说话晚半秒以上。",
        _ => "最连续，也最滞后。",
    };

    private void OnDisplayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayModeBox.SelectedItem is not ComboBoxItem { Tag: DisplayMode mode })
        {
            return;
        }

        _settings.DisplayMode = mode;
    }

    private void OnSourceLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceLanguageBox.SelectedItem is not ComboBoxItem { Tag: string id })
        {
            return;
        }

        _settings.SourceLanguage = id;
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.FontSize = e.NewValue;
        }

        UpdateLabels();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.Opacity = e.NewValue;
        }

        UpdateLabels();
    }

    private void OnLinesChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.VisibleLines = (int)e.NewValue;
        }

        UpdateLabels();
    }

    private void OnPaceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.RevealPace = e.NewValue;
        }

        UpdateLabels();
    }

    private void OnClickThroughChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.ClickThrough = ClickThroughBox.IsChecked == true;
        }
    }

    private void OnHideFromSharingChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.HideFromScreenSharing = HideFromSharingBox.IsChecked == true;
        }
    }

    private void OnSaveTranscriptsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.SaveTranscripts = SaveTranscriptsBox.IsChecked == true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
