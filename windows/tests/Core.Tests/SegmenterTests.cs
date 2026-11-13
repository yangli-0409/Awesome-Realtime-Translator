using AwesomeRealtimeTranslator.Core;
using Xunit;

namespace AwesomeRealtimeTranslator.Core.Tests;

/// <summary>
/// macOS 版 SegmenterTests.swift 的逐条移植。用例顺序、命名、断言全部一一对应，
/// 任何一侧改了断句规则，另一侧的同名用例必须同步改。
/// </summary>
public class SegmenterTests
{
    private static Segmenter MakeSegmenter(double silence = 0.7, int maxLength = 140) =>
        new(new Segmenter.Configuration
        {
            SilenceFlushInterval = silence,
            MaxSentenceLength = maxLength,
        });

    private static string[] Texts(IReadOnlyList<Sentence> sentences) =>
        sentences.Select(s => s.Text).ToArray();

    // MARK: - 规则 1：句末标点

    [Fact]
    public void SplitsOnSentenceTerminator()
    {
        Segmenter s = MakeSegmenter();
        IReadOnlyList<Sentence> output = s.Ingest(
            TranscriptEvent.Finalized("Tell me about yourself. What is your biggest"), 1.0);
        Assert.Equal(["Tell me about yourself."], Texts(output));
        Assert.Equal("What is your biggest", s.PendingText);
    }

    [Fact]
    public void SplitsMultipleSentencesInOneEvent()
    {
        Segmenter s = MakeSegmenter();
        IReadOnlyList<Sentence> output = s.Ingest(
            TranscriptEvent.Finalized("Hello there. How are you? Let's start!"), 1.0);
        Assert.Equal(["Hello there.", "How are you?", "Let's start!"], Texts(output));
        Assert.Equal(string.Empty, s.PendingText);
    }

    [Fact]
    public void ClosingQuoteStaysWithSentence()
    {
        Segmenter s = MakeSegmenter();
        IReadOnlyList<Sentence> output = s.Ingest(
            TranscriptEvent.Finalized("He said \"no.\" Then he left."), 1.0);
        Assert.Equal(["He said \"no.\"", "Then he left."], Texts(output));
    }

    [Fact]
    public void ChinesePunctuation()
    {
        Segmenter s = MakeSegmenter();
        IReadOnlyList<Sentence> output = s.Ingest(
            TranscriptEvent.Finalized("你好。请介绍一下你自己？"), 1.0);
        Assert.Equal(["你好。", "请介绍一下你自己？"], Texts(output));
    }

    // MARK: - 不该切的地方

    [Fact]
    public void DoesNotSplitOnAbbreviation()
    {
        Segmenter s = MakeSegmenter();
        IReadOnlyList<Sentence> output = s.Ingest(
            TranscriptEvent.Finalized("I worked with Dr. Smith on that"), 1.0);
        Assert.Empty(output);
        Assert.Equal("I worked with Dr. Smith on that", s.PendingText);
    }

    [Fact]
    public void DoesNotSplitOnDecimalNumber()
    {
        Segmenter s = MakeSegmenter();
        Assert.Empty(s.Ingest(TranscriptEvent.Finalized("The latency was 1.5 seconds on average"), 1.0));
    }

    [Fact]
    public void DoesNotSplitOnInitial()
    {
        Segmenter s = MakeSegmenter();
        Assert.Empty(s.Ingest(TranscriptEvent.Finalized("My manager was J. Robinson back then"), 1.0));
    }

    [Fact]
    public void DoesNotSplitOnEllipsisMidSentence()
    {
        Segmenter s = MakeSegmenter();
        Assert.Empty(s.Ingest(TranscriptEvent.Finalized("Well... it depends on the traffic"), 1.0));
    }

    [Fact]
    public void EllipsisFollowedByCapitalDoesSplit()
    {
        // "Well... Then he left." 是两句，和上一条是一对
        Segmenter s = MakeSegmenter();
        IReadOnlyList<Sentence> output = s.Ingest(
            TranscriptEvent.Finalized("Well... Then he left."), 1.0);
        Assert.Equal(["Well...", "Then he left."], Texts(output));
    }

    [Fact]
    public void ShortFragmentMergesIntoNextSentence()
    {
        Segmenter s = MakeSegmenter();
        IReadOnlyList<Sentence> output = s.Ingest(
            TranscriptEvent.Finalized("A. That is a good question."), 1.0);
        Assert.Equal(["A. That is a good question."], Texts(output));
    }

    // MARK: - 规则 2：静音定稿

    [Fact]
    public void SilenceFlushesBuffer()
    {
        Segmenter s = MakeSegmenter(silence: 0.7);
        s.Ingest(TranscriptEvent.Finalized("Can you walk me through it"), 1.0);
        Assert.Empty(s.Tick(1.5));
        Assert.Equal(["Can you walk me through it"], Texts(s.Tick(1.8)));
        Assert.False(s.HasPendingContent);
    }

    [Fact]
    public void TickDoesNothingWhenBufferEmpty()
    {
        Segmenter s = MakeSegmenter();
        Assert.Empty(s.Tick(100));
    }

    [Fact]
    public void VolatileAloneIsNeverFinalizedByTick()
    {
        Segmenter s = MakeSegmenter(silence: 0.7);
        s.Ingest(TranscriptEvent.Volatile("maybe this"), 1.0);
        Assert.Empty(s.Tick(5.0));
        // "this" 是最末 token，可能只是 "thi" 的中间态，所以不显示（见 StableTail）
        Assert.Equal("maybe", s.PendingText);
        Assert.True(s.HasPendingContent);
    }

    // MARK: - 规则 3：超长强制切

    [Fact]
    public void ForcesCutWhenTooLong()
    {
        Segmenter s = MakeSegmenter(maxLength: 40);
        const string Long =
            "so basically what happened was that the team decided to migrate everything at once";
        IReadOnlyList<Sentence> output = s.Ingest(TranscriptEvent.Finalized(Long), 1.0);
        Assert.NotEmpty(output);
        Assert.True(output[0].Text.Length <= 40, $"硬切结果过长：{output[0].Text.Length}");
        Assert.False(output[0].Text.EndsWith(' '));
        Assert.StartsWith(output[0].Text, Long, StringComparison.Ordinal);
    }

    // MARK: - volatile 覆盖语义

    [Fact]
    public void VolatileIsReplacedNotAppended()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Volatile("what is"), 1.0);
        s.Ingest(TranscriptEvent.Volatile("what is your"), 1.1);
        s.Ingest(TranscriptEvent.Volatile("what is your name"), 1.2);
        Assert.Equal("what is your", s.PendingText);
    }

    [Fact]
    public void FinalizedSupersedesVolatile()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Volatile("what is your na"), 1.0);
        IReadOnlyList<Sentence> output = s.Ingest(TranscriptEvent.Finalized("What is your name?"), 1.3);
        Assert.Equal(["What is your name?"], Texts(output));
        Assert.Equal(string.Empty, s.PendingText);
    }

    [Fact]
    public void VolatileThenFinalizedPartial()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Finalized("First part"), 1.0);
        s.Ingest(TranscriptEvent.Volatile(" and the rest"), 1.2);
        Assert.Equal("First part and the", s.PendingText);
    }

    // MARK: - 时间戳与冲刷

    [Fact]
    public void SentenceCarriesTimeRange()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Volatile("tell me"), 2.0);
        IReadOnlyList<Sentence> output = s.Ingest(TranscriptEvent.Finalized("Tell me about it."), 4.5);
        Sentence only = Assert.Single(output);
        Assert.Equal(2.0, only.StartedAt, 3);
        Assert.Equal(4.5, only.EndedAt, 3);
    }

    [Fact]
    public void FlushOnStopEmitsRemainder()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Finalized("one last thing"), 1.0);
        Assert.Equal(["one last thing"], Texts(s.Flush(2.0)));
        Assert.Empty(s.Flush(3.0));
    }

    [Fact]
    public void FailureEventIsIgnoredBySegmentation()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Finalized("hello"), 1.0);
        Assert.Empty(s.Ingest(TranscriptEvent.Failed("boom"), 1.1));
        Assert.Equal("hello", s.PendingText);
    }

    [Fact]
    public void EmptyAndWhitespaceEventsAreIgnored()
    {
        Segmenter s = MakeSegmenter();
        Assert.Empty(s.Ingest(TranscriptEvent.Finalized("   "), 1.0));
        Assert.Empty(s.Ingest(TranscriptEvent.Finalized(string.Empty), 1.1));
        Assert.False(s.HasPendingContent);
    }

    // MARK: - 回归：定稿句子不可变

    [Fact]
    public void FinalizedSentencesAreStableAcrossLaterEvents()
    {
        Segmenter s = MakeSegmenter();
        IReadOnlyList<Sentence> first = s.Ingest(TranscriptEvent.Finalized("Question one."), 1.0);
        s.Ingest(TranscriptEvent.Volatile("question tw"), 1.5);
        IReadOnlyList<Sentence> second = s.Ingest(TranscriptEvent.Finalized("Question two."), 2.0);
        Assert.Equal(["Question one."], Texts(first));
        Assert.Equal(["Question two."], Texts(second));
        Assert.NotEqual(first[0].Id, second[0].Id);
    }

    // MARK: - 端到端：一串带时间戳的事件 → 定稿句子

    [Fact]
    public void EventStreamScenario()
    {
        Segmenter s = MakeSegmenter(silence: 0.7);
        (TranscriptEvent Event, double Time)[] script =
        [
            (TranscriptEvent.Volatile("so"), 0.3),
            (TranscriptEvent.Volatile("so tell me"), 0.6),
            (TranscriptEvent.Volatile("so tell me about"), 0.9),
            (TranscriptEvent.Finalized("So tell me about your experience with Kubernetes."), 1.4),
            (TranscriptEvent.Volatile("how"), 2.0),
            (TranscriptEvent.Volatile("how many nodes"), 2.4),
            (TranscriptEvent.Finalized("How many nodes did you run"), 3.0),
        ];

        var produced = new List<string>();
        foreach ((TranscriptEvent evt, double time) in script)
        {
            produced.AddRange(Texts(s.Ingest(evt, time)));
            produced.AddRange(Texts(s.Tick(time)));
        }

        // 3.0s 之后面试官停了 1 秒
        produced.AddRange(Texts(s.Tick(4.0)));

        Assert.Equal(
            [
                "So tell me about your experience with Kubernetes.",
                "How many nodes did you run",
            ],
            produced);
        Assert.False(s.HasPendingContent);
    }

    // MARK: - 未完成尾词的裁剪

    [Fact]
    public void PendingTextDropsIncompleteTrailingWord()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Volatile("I am also c"), 0.1);
        Assert.Equal("I am also", s.PendingText);
        s.Ingest(TranscriptEvent.Volatile("I am also curious"), 0.4);
        Assert.Equal("I am also", s.PendingText);
        s.Ingest(TranscriptEvent.Volatile("I am also curious how"), 0.7);
        Assert.Equal("I am also curious", s.PendingText);
    }

    [Fact]
    public void PendingTextKeepsEverythingAfterSpaceOrPunctuation()
    {
        Assert.Equal("what was the ", Segmenter.StableTail("what was the "));
        Assert.Equal("and how did you find it?", Segmenter.StableTail("and how did you find it?"));
        Assert.Equal("the root cause,", Segmenter.StableTail("the root cause,"));
    }

    [Fact]
    public void PendingTextHidesLoneIncompleteFirstWord()
    {
        // 一句话刚开头、还没有空格时，没有任何完整的词可显示
        Assert.Equal(string.Empty, Segmenter.StableTail("Wha"));
    }

    [Fact]
    public void PendingTextDoesNotTrimChinese()
    {
        // 中文没有词边界，逐字出字本身就是正常阅读单位
        Assert.Equal("你能讲一下你", Segmenter.StableTail("你能讲一下你"));
    }

    [Fact]
    public void FinalizedHeadIsNeverTrimmed()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Finalized("What was the root cause"), 0.5);
        s.Ingest(TranscriptEvent.Volatile("and ho"), 0.8);
        Assert.Equal("What was the root cause and", s.PendingText);
    }

    [Fact]
    public void StableTailOnEmptyStringIsEmpty() =>
        Assert.Equal(string.Empty, Segmenter.StableTail(string.Empty));

    // MARK: - 中英混排不插空格（Join 的回归）

    [Fact]
    public void JoinDoesNotInsertSpaceBetweenCjk()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Finalized("你好"), 1.0);
        s.Ingest(TranscriptEvent.Volatile("世界。"), 1.1);
        Assert.Equal("你好世界。", s.PendingText);
    }

    [Fact]
    public void JoinDoesNotInsertSpaceBeforePunctuation()
    {
        Segmenter s = MakeSegmenter();
        s.Ingest(TranscriptEvent.Finalized("hello"), 1.0);
        s.Ingest(TranscriptEvent.Volatile(", world "), 1.1);
        Assert.Equal("hello, world ", s.PendingText);
    }
}
