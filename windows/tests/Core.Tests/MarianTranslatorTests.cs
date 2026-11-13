using AwesomeRealtimeTranslator.Core;
using AwesomeRealtimeTranslator.Core.Translation;
using Xunit;

namespace AwesomeRealtimeTranslator.Core.Tests;

/// <summary>
/// 翻译层的测试。
///
/// ONNX Runtime 是跨平台的，所以这些用例在开发机（macOS）上也能真跑——
/// 这是 Windows 版里为数不多能做到「真的验证过」的功能之一，不是只编译通过。
///
/// 模型有 240MB，不进仓库。没有模型时整个类跳过，CI 上不会红。
/// </summary>
public class MarianTranslatorTests
{
    private static string? ModelDirectory
    {
        get
        {
            // 从测试程序集往上找 windows/models/opus-mt-en-zh
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "models", "opus-mt-en-zh");
                if (Directory.Exists(candidate) && MarianTranslator.FindMissingFile(candidate) is null)
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }

    private static bool HasModel => ModelDirectory is not null;

    [SkippableFact]
    public async Task TranslatesASimpleSentence()
    {
        Skip.IfNot(HasModel, "没有下载翻译模型，跳过");

        await using var translator = new MarianTranslator(ModelDirectory!);
        await translator.PrepareAsync();

        string result = await translator.TranslateAsync("Hello, how are you?", []);

        Assert.NotEmpty(result);
        Assert.True(ContainsCjk(result), $"译文里应该有中文，实际得到：{result}");
    }

    [SkippableFact]
    public async Task TranslatesATypicalInterviewQuestion()
    {
        Skip.IfNot(HasModel, "没有下载翻译模型，跳过");

        await using var translator = new MarianTranslator(ModelDirectory!);
        await translator.PrepareAsync();

        string result = await translator.TranslateAsync(
            "Tell me about your experience with distributed systems.", []);

        Assert.True(ContainsCjk(result), $"译文里应该有中文，实际得到：{result}");
        // 「分布式」「系统」「经验」里至少该命中一个，否则说明译出来的是别的东西
        Assert.True(
            result.Contains('系') || result.Contains('分') || result.Contains('经'),
            $"译文看起来和原意无关：{result}");
    }

    [SkippableFact]
    public async Task IsDeterministic()
    {
        Skip.IfNot(HasModel, "没有下载翻译模型，跳过");

        // 贪心解码必须每次结果一样。不一样的话字幕会自己改来改去。
        await using var translator = new MarianTranslator(ModelDirectory!);
        await translator.PrepareAsync();

        const string Source = "What was the root cause and how did you find it?";
        string first = await translator.TranslateAsync(Source, []);
        string second = await translator.TranslateAsync(Source, []);

        Assert.Equal(first, second);
    }

    [SkippableFact]
    public async Task EmptyInputReturnsEmpty()
    {
        Skip.IfNot(HasModel, "没有下载翻译模型，跳过");

        await using var translator = new MarianTranslator(ModelDirectory!);
        await translator.PrepareAsync();

        Assert.Equal(string.Empty, await translator.TranslateAsync("   ", []));
    }

    [SkippableFact]
    public async Task NoSpacesBetweenChineseCharacters()
    {
        Skip.IfNot(HasModel, "没有下载翻译模型，跳过");

        await using var translator = new MarianTranslator(ModelDirectory!);
        await translator.PrepareAsync();

        string result = await translator.TranslateAsync("How many nodes did you run in production?", []);

        Assert.DoesNotContain("  ", result);
        for (int i = 1; i < result.Length - 1; i++)
        {
            if (result[i] == ' ')
            {
                Assert.False(
                    IsCjk(result[i - 1]) && IsCjk(result[i + 1]),
                    $"中文之间不该有空格：{result}");
            }
        }
    }

    [Fact]
    public async Task MissingModelGivesAnActionableError()
    {
        // 模型缺失是最常见的部署问题，错误信息必须说清楚缺什么、怎么办。
        string empty = Path.Combine(Path.GetTempPath(), "art-no-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);

        try
        {
            var translator = new MarianTranslator(empty);
            TranslatorException ex = await Assert.ThrowsAsync<TranslatorException>(
                () => translator.PrepareAsync());
            Assert.Contains("翻译模型不完整", ex.Message);
            Assert.NotNull(ex.RecoverySuggestion);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Theory]
    [InlineData("你 好 世 界", "你好世界")]
    [InlineData("hello world", "hello world")]
    [InlineData("在 2024 年", "在 2024 年")]
    [InlineData("", "")]
    public void CollapsesSpacesOnlyBetweenCjk(string input, string expected) =>
        Assert.Equal(expected, MarianTranslator.CollapseCjkSpaces(input));

    private static bool ContainsCjk(string text) => text.Any(IsCjk);

    private static bool IsCjk(char c) =>
        c is >= (char)0x4E00 and <= (char)0x9FFF;
}
