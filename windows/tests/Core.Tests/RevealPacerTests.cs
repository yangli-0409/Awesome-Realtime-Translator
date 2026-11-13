using AwesomeRealtimeTranslator.Core;
using Xunit;

namespace AwesomeRealtimeTranslator.Core.Tests;

/// <summary>
/// 显现节奏的数学。这段逻辑在 macOS 版里埋在 SwiftUI 的 View 里没法单测，
/// 移植时把它抽成纯函数，就是为了能在这里验证——「顺滑感」整个来自这里。
/// </summary>
public class RevealPacerTests
{
    private const double Frame = 1.0 / 60.0;

    [Theory]
    [InlineData(0.1, RevealPacer.MinPace)]
    [InlineData(0.5, 0.5)]
    [InlineData(5.0, RevealPacer.MaxPace)]
    public void PaceIsClampedToUsefulRange(double input, double expected) =>
        Assert.Equal(expected, new RevealPacer(input).Pace);

    [Fact]
    public void StartsAtZero() => Assert.Equal(0, new RevealPacer(0.5).Revealed);

    [Fact]
    public void NeverExceedsTotalLength()
    {
        var pacer = new RevealPacer(0.3);
        for (int i = 0; i < 1000; i++)
        {
            pacer.Advance(10, Frame);
        }

        Assert.Equal(10, pacer.Revealed);
    }

    [Fact]
    public void PaceIsATimeConstantNotADeadline()
    {
        // Pace 是指数衰减的时间常数：跑满 Pace 秒排掉约 63%（1 - 1/e），不是全部。
        // 这条用例把这个性质钉死，免得以后有人看到「排空」两个字就去改成匀速——
        // 匀速正是 macOS 版第一版的做法，会产生「铺完 → 干等 → 下一批」。
        var pacer = new RevealPacer(0.5);
        for (double t = 0; t < 0.5; t += Frame)
        {
            pacer.Advance(100, Frame);
        }

        Assert.InRange(pacer.Revealed, 55, 72);
    }

    [Fact]
    public void FullDrainFinishesInBoundedTime()
    {
        // 对方停止说话之后，最后一截由 MinRate 兜底收尾，不能无限拖下去。
        var pacer = new RevealPacer(0.5);
        double elapsed = 0;
        while (pacer.Revealed < 40 && elapsed < 5.0)
        {
            pacer.Advance(40, Frame);
            elapsed += Frame;
        }

        Assert.Equal(40, pacer.Revealed);
        Assert.InRange(elapsed, 0.25, 2.5);
    }

    [Fact]
    public void BiggerBacklogDrainsFaster()
    {
        // 速率控制的核心性质：积压越多放得越快，所以不同批量能在相近时间内排空。
        var small = new RevealPacer(0.5);
        var large = new RevealPacer(0.5);

        small.Advance(20, Frame);
        large.Advance(200, Frame);

        Assert.True(large.Revealed > small.Revealed,
            $"积压大的那个应该放得更快，实际 {large.Revealed} vs {small.Revealed}");
    }

    [Fact]
    public void RateIsCappedSoHugeBacklogDoesNotDumpInstantly()
    {
        // 上限 MaxRate 存在的理由：一次收到超长文本时不能瞬间铺完，
        // 否则又变回「一跳一跳」。
        var pacer = new RevealPacer(0.3);
        pacer.Advance(100000, Frame);
        Assert.True(pacer.Revealed <= (int)(RevealPacer.MaxRate * Frame) + 1,
            $"单帧放出 {pacer.Revealed} 个字符，超过了速率上限");
    }

    [Fact]
    public void RateHasFloorSoTinyBacklogStillMoves()
    {
        // 下限 MinRate 存在的理由：只剩一两个字符时也得继续走完，不能卡住。
        var pacer = new RevealPacer(1.0);
        double elapsed = 0;
        while (pacer.Revealed < 2 && elapsed < 1.0)
        {
            pacer.Advance(2, Frame);
            elapsed += Frame;
        }

        Assert.Equal(2, pacer.Revealed);
    }

    [Fact]
    public void ShrinkingTextRewindsProgress()
    {
        // 一句话定稿后原文尾巴清空，或者模型改写了 volatile 结果，文本会变短。
        // 进度必须跟着回退，否则 Revealed 会越界，UI 上就是 substring 抛异常。
        var pacer = new RevealPacer(0.3);
        for (int i = 0; i < 100; i++)
        {
            pacer.Advance(50, Frame);
        }

        Assert.Equal(50, pacer.Revealed);

        int revealed = pacer.Advance(5, Frame);
        Assert.True(revealed <= 5, $"文本缩短后 Revealed={revealed}，越界了");
    }

    [Fact]
    public void ContinuesFromWhereItStoppedWhenNewBatchArrives()
    {
        // 这是第一版的 bug 所在：批次切换时<b>不能</b>补齐到全文，
        // 否则每批末尾都会跳一下。新批到达只是把 total 变大，进度留在原处。
        var pacer = new RevealPacer(0.5);
        for (int i = 0; i < 10; i++)
        {
            pacer.Advance(30, Frame);
        }

        int before = pacer.Revealed;
        Assert.True(before is > 0 and < 30, $"前提不成立：before={before}");

        // 新的一批到了，文本变长
        int after = pacer.Advance(60, Frame);
        Assert.True(after >= before, "进度不能倒退");
        Assert.True(after < 60, "不能瞬间补齐到全文——那正是要消除的「每批末尾跳一下」");
    }

    [Fact]
    public void ResetClearsProgress()
    {
        var pacer = new RevealPacer(0.5);
        pacer.Advance(100, 1.0);
        Assert.True(pacer.Revealed > 0);
        pacer.Reset();
        Assert.Equal(0, pacer.Revealed);
    }

    [Fact]
    public void SteadyStateNeverStalls()
    {
        // 稳态模拟：每 950ms 到一批 ~15 个字符（实测的识别节奏）。
        // 断言：没有任何一个 100ms 的窗口是完全不动的——「不卡顿」就是这个意思。
        var pacer = new RevealPacer(0.5);
        int total = 0;
        double clock = 0;
        double nextBatch = 0;
        int lastRevealed = 0;
        double stalledFor = 0;
        double worstStall = 0;

        while (clock < 10.0)
        {
            if (clock >= nextBatch)
            {
                total += 15;
                nextBatch += 0.95;
            }

            pacer.Advance(total, Frame);
            clock += Frame;

            if (pacer.Revealed == lastRevealed)
            {
                stalledFor += Frame;
                worstStall = Math.Max(worstStall, stalledFor);
            }
            else
            {
                stalledFor = 0;
                lastRevealed = pacer.Revealed;
            }
        }

        Assert.True(worstStall < 0.2,
            $"最长停顿 {worstStall:F3}s，稳态下不该出现可感知的停顿");
    }
}
