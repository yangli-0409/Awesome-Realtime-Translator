using AwesomeRealtimeTranslator.Core.Translation;
using Xunit;

namespace AwesomeRealtimeTranslator.Core.Tests;

public class TermProtectorTests
{
    private static TermProtector Make(params (string Term, string Replacement)[] terms) =>
        new(terms.ToDictionary(t => t.Term, t => t.Replacement, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void ReplacesAKnownTermWithASentinel()
    {
        TermProtector p = Make(("architecture", "架构"));
        (string text, IReadOnlyDictionary<string, string> restore) = p.Protect("the architecture you designed");

        Assert.DoesNotContain("architecture", text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(restore);
        Assert.Equal("架构", restore.Values.Single());
        Assert.Equal("the ZQX1 you designed", text);
    }

    [Fact]
    public void RestoresTheChineseTerm()
    {
        TermProtector p = Make(("architecture", "架构"));
        (string text, IReadOnlyDictionary<string, string> restore) = p.Protect("the architecture");
        Assert.Equal("你设计的架构", TermProtector.Restore("你设计的ZQX1", restore));
        Assert.Equal("the ZQX1", text);
    }

    [Fact]
    public void LongestTermWinsSoCompoundsAreNotBrokenUp()
    {
        // "container orchestration" 必须整体命中，不能被 "orchestration" 先切掉
        TermProtector p = Make(("orchestration", "编排"), ("container orchestration", "容器编排"));
        (_, IReadOnlyDictionary<string, string> restore) = p.Protect("we use container orchestration");

        Assert.Equal("容器编排", Assert.Single(restore).Value);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        TermProtector p = Make(("architecture", "架构"));
        (string text, _) = p.Protect("The Architecture matters");
        Assert.Equal("The ZQX1 matters", text);
    }

    [Fact]
    public void RespectsWordBoundaries()
    {
        // "on call" 不能命中 "falcon called"，否则会把无关的词换掉
        TermProtector p = Make(("on call", "值班"));
        (string text, IReadOnlyDictionary<string, string> restore) = p.Protect("the falcon called out");

        Assert.Equal("the falcon called out", text);
        Assert.Empty(restore);
    }

    [Fact]
    public void SubstringInsideAWordIsNotReplaced()
    {
        TermProtector p = Make(("latency", "延迟"));
        (string text, _) = p.Protect("latencyvalue is high");
        Assert.Equal("latencyvalue is high", text);
    }

    [Fact]
    public void RepeatedTermReusesTheSameSentinel()
    {
        TermProtector p = Make(("latency", "延迟"));
        (string text, IReadOnlyDictionary<string, string> restore) = p.Protect("latency here and latency there");

        Assert.Equal("ZQX1 here and ZQX1 there", text);
        Assert.Single(restore);
        Assert.Equal("延迟 here and 延迟 there", TermProtector.Restore(text, restore));
    }

    [Fact]
    public void SeveralDifferentTermsGetDistinctSentinels()
    {
        TermProtector p = Make(("latency", "延迟"), ("throughput", "吞吐量"));
        (string text, IReadOnlyDictionary<string, string> restore) = p.Protect("latency and throughput");

        Assert.Equal(2, restore.Count);
        Assert.Equal(2, restore.Values.Distinct().Count());
        Assert.Equal("延迟 and 吞吐量", TermProtector.Restore(text, restore));
    }

    [Fact]
    public void DroppedSentinelLeavesNoTrace()
    {
        // 模型偶尔会吞掉哨兵。还原时不能在译文里留下 "ZQX1" 这种鬼东西，
        // 这条用例确认：哨兵不在译文里时，Restore 什么也不做。
        TermProtector p = Make(("latency", "延迟"));
        (_, IReadOnlyDictionary<string, string> restore) = p.Protect("latency matters");

        const string Translated = "这句话里模型把哨兵丢了";
        Assert.Equal(Translated, TermProtector.Restore(Translated, restore));
    }

    [Fact]
    public void EmptyInputIsSafe()
    {
        TermProtector p = Make(("latency", "延迟"));
        (string text, IReadOnlyDictionary<string, string> restore) = p.Protect(string.Empty);
        Assert.Equal(string.Empty, text);
        Assert.Empty(restore);
        Assert.Equal(string.Empty, TermProtector.Restore(string.Empty, restore));
    }

    [Fact]
    public void NoGlossaryIsSafe()
    {
        var p = new TermProtector(new Dictionary<string, string>());
        (string text, IReadOnlyDictionary<string, string> restore) = p.Protect("anything at all");
        Assert.Equal("anything at all", text);
        Assert.Empty(restore);
    }

    [Fact]
    public void StopsAtNineTermsSoSentinelsStaySingleDigit()
    {
        // 哨兵必须是 ZQX1..ZQX9：两位数的 ZQX10 会被 ZQX1 的还原先命中一半。
        Dictionary<string, string> many = Enumerable.Range(1, 20)
            .ToDictionary(i => $"term{i}", i => $"词{i}", StringComparer.OrdinalIgnoreCase);
        var p = new TermProtector(many);

        string source = string.Join(" ", Enumerable.Range(1, 20).Select(i => $"term{i}"));
        (string text, IReadOnlyDictionary<string, string> restore) = p.Protect(source);

        Assert.True(restore.Count <= 9, $"保护了 {restore.Count} 个术语，哨兵会进到两位数");
        foreach (string sentinel in restore.Keys)
        {
            Assert.Matches("^ZQX[1-9]$", sentinel);
        }

        Assert.DoesNotContain("ZQX10", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultGlossaryCoversTheTermsMeasuredToBeWrong()
    {
        // 这两个是实测翻错的（architecture→建筑，container orchestration→集装箱的管弦），
        // 是整张表存在的理由，不能被误删。
        Assert.Equal("架构", TermProtector.DefaultGlossary["architecture"]);
        Assert.Equal("容器编排", TermProtector.DefaultGlossary["container orchestration"]);
    }

    [Fact]
    public void DefaultGlossaryHasNoEmptyEntries()
    {
        foreach ((string term, string replacement) in TermProtector.DefaultGlossary)
        {
            Assert.False(string.IsNullOrWhiteSpace(term));
            Assert.False(string.IsNullOrWhiteSpace(replacement));
        }
    }
}
