using System.IO;

namespace AwesomeRealtimeTranslator;

/// <summary>
/// 找模型文件。
///
/// <b>刻意不做自动下载。</b>macOS 版的核心承诺是「一次网络请求都没有」，
/// 因为识别和翻译都是系统自带的。Windows 没有系统模型，只能自带文件——
/// 但「自带」不等于「运行时去网上拉」：一旦应用自己发请求，那条承诺就破了。
///
/// 所以：模型随安装包一起发，应用只在本地找；找不到就清楚地告诉用户缺什么，
/// 绝不偷偷联网。唯一的下载动作是用户自己在浏览器里下安装包。
/// </summary>
public static class ModelPaths
{
    public const string WhisperModelFileName = "ggml-base.bin";

    public const string TranslationModelDirectoryName = "opus-mt-en-zh";

    /// <summary>
    /// 按优先级找模型根目录：
    /// <list type="number">
    /// <item>可执行文件旁边的 <c>models\</c>——这是安装包的布局。</item>
    /// <item>仓库里的 <c>windows\models\</c>——从源码跑的时候用。</item>
    /// <item><c>%APPDATA%\AwesomeRealtimeTranslator\models\</c>——用户手工放的。</item>
    /// </list>
    /// </summary>
    public static IEnumerable<string> CandidateRoots()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "models");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "models");
            yield return Path.Combine(dir.FullName, "windows", "models");
            dir = dir.Parent;
        }

        yield return Path.Combine(Settings.FileSettingsStore.DefaultDirectory, "models");
    }

    public static string? FindWhisperModel()
    {
        foreach (string root in CandidateRoots())
        {
            string candidate = Path.Combine(root, WhisperModelFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string? FindTranslationModel()
    {
        foreach (string root in CandidateRoots())
        {
            string candidate = Path.Combine(root, TranslationModelDirectoryName);
            if (Directory.Exists(candidate) &&
                Core.Translation.MarianTranslator.FindMissingFile(candidate) is null)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>给设置窗显示用。找不到时显示应该放在哪儿。</summary>
    public static string DescribeLocation() =>
        FindTranslationModel() ?? Path.Combine(AppContext.BaseDirectory, "models");
}
