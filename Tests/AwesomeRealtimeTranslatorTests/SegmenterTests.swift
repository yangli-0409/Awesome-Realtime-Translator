import XCTest

final class SegmenterTests: XCTestCase {
    private func makeSegmenter(
        silence: TimeInterval = 0.7,
        maxLength: Int = 140
    ) -> Segmenter {
        var config = Segmenter.Configuration()
        config.silenceFlushInterval = silence
        config.maxSentenceLength = maxLength
        return Segmenter(configuration: config)
    }

    // MARK: - 规则 1：句末标点

    func testSplitsOnSentenceTerminator() {
        var s = makeSegmenter()
        let out = s.ingest(.finalized("Tell me about yourself. What is your biggest"), at: 1.0)
        XCTAssertEqual(out.map(\.text), ["Tell me about yourself."])
        XCTAssertEqual(s.pendingText, "What is your biggest")
    }

    func testSplitsMultipleSentencesInOneEvent() {
        var s = makeSegmenter()
        let out = s.ingest(.finalized("Hello there. How are you? Let's start!"), at: 1.0)
        XCTAssertEqual(out.map(\.text), ["Hello there.", "How are you?", "Let's start!"])
        XCTAssertEqual(s.pendingText, "")
    }

    func testClosingQuoteStaysWithSentence() {
        var s = makeSegmenter()
        let out = s.ingest(.finalized("He said \"no.\" Then he left."), at: 1.0)
        XCTAssertEqual(out.map(\.text), ["He said \"no.\"", "Then he left."])
    }

    func testChinesePunctuation() {
        var s = makeSegmenter()
        let out = s.ingest(.finalized("你好。请介绍一下你自己？"), at: 1.0)
        XCTAssertEqual(out.map(\.text), ["你好。", "请介绍一下你自己？"])
    }

    // MARK: - 不该切的地方

    func testDoesNotSplitOnAbbreviation() {
        var s = makeSegmenter()
        let out = s.ingest(.finalized("I worked with Dr. Smith on that"), at: 1.0)
        XCTAssertTrue(out.isEmpty, "Dr. 后面不该切句，实际切出 \(out.map(\.text))")
        XCTAssertEqual(s.pendingText, "I worked with Dr. Smith on that")
    }

    func testDoesNotSplitOnDecimalNumber() {
        var s = makeSegmenter()
        let out = s.ingest(.finalized("The latency was 1.5 seconds on average"), at: 1.0)
        XCTAssertTrue(out.isEmpty)
    }

    func testDoesNotSplitOnInitial() {
        var s = makeSegmenter()
        let out = s.ingest(.finalized("My manager was J. Robinson back then"), at: 1.0)
        XCTAssertTrue(out.isEmpty)
    }

    func testDoesNotSplitOnEllipsisMidSentence() {
        var s = makeSegmenter()
        let out = s.ingest(.finalized("Well... it depends on the traffic"), at: 1.0)
        XCTAssertTrue(out.isEmpty, "实际切出 \(out.map(\.text))")
    }

    func testShortFragmentMergesIntoNextSentence() {
        var s = makeSegmenter()
        // "A." 只有两个字符，不单独成句
        let out = s.ingest(.finalized("A. That is a good question."), at: 1.0)
        XCTAssertEqual(out.map(\.text), ["A. That is a good question."])
    }

    // MARK: - 规则 2：静音定稿

    func testSilenceFlushesBuffer() {
        var s = makeSegmenter(silence: 0.7)
        _ = s.ingest(.finalized("Can you walk me through it"), at: 1.0)
        XCTAssertTrue(s.tick(at: 1.5).isEmpty, "0.5s 还没到阈值，不该定稿")
        let out = s.tick(at: 1.8)
        XCTAssertEqual(out.map(\.text), ["Can you walk me through it"])
        XCTAssertFalse(s.hasPendingContent)
    }

    func testTickDoesNothingWhenBufferEmpty() {
        var s = makeSegmenter()
        XCTAssertTrue(s.tick(at: 100).isEmpty)
    }

    func testVolatileAloneIsNeverFinalizedByTick() {
        var s = makeSegmenter(silence: 0.7)
        _ = s.ingest(.volatile("maybe this"), at: 1.0)
        XCTAssertTrue(s.tick(at: 5.0).isEmpty, "未定稿文本不能被当成句子送去翻译")
        // "this" 是最末 token，可能只是 "thi" 的中间态，所以不显示（见 stableTail）
        XCTAssertEqual(s.pendingText, "maybe")
        XCTAssertTrue(s.hasPendingContent, "裁剪只影响显示，不能让内容看起来消失")
    }

    // MARK: - 规则 3：超长强制切

    func testForcesCutWhenTooLong() {
        var s = makeSegmenter(maxLength: 40)
        let long = "so basically what happened was that the team decided to migrate everything at once"
        let out = s.ingest(.finalized(long), at: 1.0)
        XCTAssertFalse(out.isEmpty)
        XCTAssertLessThanOrEqual(out[0].text.count, 40)
        XCTAssertFalse(out[0].text.hasSuffix(" "))
        // 不能把单词劈开
        XCTAssertTrue(long.hasPrefix(out[0].text), "硬切后前缀应与原文一致")
    }

    // MARK: - volatile 覆盖语义

    func testVolatileIsReplacedNotAppended() {
        var s = makeSegmenter()
        _ = s.ingest(.volatile("what is"), at: 1.0)
        _ = s.ingest(.volatile("what is your"), at: 1.1)
        _ = s.ingest(.volatile("what is your name"), at: 1.2)
        // 覆盖语义正确（不是 "what iswhat is your..."），末词按 stableTail 规则暂不显示
        XCTAssertEqual(s.pendingText, "what is your")
    }

    func testFinalizedSupersedesVolatile() {
        var s = makeSegmenter()
        _ = s.ingest(.volatile("what is your na"), at: 1.0)
        let out = s.ingest(.finalized("What is your name?"), at: 1.3)
        XCTAssertEqual(out.map(\.text), ["What is your name?"])
        XCTAssertEqual(s.pendingText, "", "定稿后 volatile 必须清空，否则原文区会重复")
    }

    func testVolatileThenFinalizedPartial() {
        var s = makeSegmenter()
        _ = s.ingest(.finalized("First part"), at: 1.0)
        _ = s.ingest(.volatile(" and the rest"), at: 1.2)
        XCTAssertEqual(s.pendingText, "First part and the")
    }

    // MARK: - 时间戳与冲刷

    func testSentenceCarriesTimeRange() {
        var s = makeSegmenter()
        _ = s.ingest(.volatile("tell me"), at: 2.0)
        let out = s.ingest(.finalized("Tell me about it."), at: 4.5)
        XCTAssertEqual(out.count, 1)
        XCTAssertEqual(out[0].startedAt, 2.0, accuracy: 0.001)
        XCTAssertEqual(out[0].endedAt, 4.5, accuracy: 0.001)
    }

    func testFlushOnStopEmitsRemainder() {
        var s = makeSegmenter()
        _ = s.ingest(.finalized("one last thing"), at: 1.0)
        let out = s.flush(at: 2.0)
        XCTAssertEqual(out.map(\.text), ["one last thing"])
        XCTAssertTrue(s.flush(at: 3.0).isEmpty, "重复 flush 不能产生重复句子")
    }

    func testFailureEventIsIgnoredBySegmentation() {
        var s = makeSegmenter()
        _ = s.ingest(.finalized("hello"), at: 1.0)
        let out = s.ingest(.failed("boom"), at: 1.1)
        XCTAssertTrue(out.isEmpty)
        XCTAssertEqual(s.pendingText, "hello")
    }

    func testEmptyAndWhitespaceEventsAreIgnored() {
        var s = makeSegmenter()
        XCTAssertTrue(s.ingest(.finalized("   "), at: 1.0).isEmpty)
        XCTAssertTrue(s.ingest(.finalized(""), at: 1.1).isEmpty)
        XCTAssertFalse(s.hasPendingContent)
    }

    // MARK: - 回归：定稿句子不可变

    func testFinalizedSentencesAreStableAcrossLaterEvents() {
        var s = makeSegmenter()
        let first = s.ingest(.finalized("Question one."), at: 1.0)
        _ = s.ingest(.volatile("question tw"), at: 1.5)
        let second = s.ingest(.finalized("Question two."), at: 2.0)
        XCTAssertEqual(first.map(\.text), ["Question one."])
        XCTAssertEqual(second.map(\.text), ["Question two."])
        XCTAssertNotEqual(first[0].id, second[0].id)
    }

    // MARK: - 端到端：一串带时间戳的事件 → 定稿句子（PRD §9.6 验收）

    func testEventStreamScenario() {
        var s = makeSegmenter(silence: 0.7)
        let script: [(TranscriptEvent, TimeInterval)] = [
            (.volatile("so"), 0.3),
            (.volatile("so tell me"), 0.6),
            (.volatile("so tell me about"), 0.9),
            (.finalized("So tell me about your experience with Kubernetes."), 1.4),
            (.volatile("how"), 2.0),
            (.volatile("how many nodes"), 2.4),
            (.finalized("How many nodes did you run"), 3.0),
        ]
        var produced: [String] = []
        for (event, time) in script {
            produced += s.ingest(event, at: time).map(\.text)
            produced += s.tick(at: time).map(\.text)
        }
        // 3.0s 之后面试官停了 1 秒
        produced += s.tick(at: 4.0).map(\.text)

        XCTAssertEqual(produced, [
            "So tell me about your experience with Kubernetes.",
            "How many nodes did you run",
        ])
        XCTAssertFalse(s.hasPendingContent)
    }

    // MARK: - 未完成尾词的裁剪

    func testPendingTextDropsIncompleteTrailingWord() {
        var s = makeSegmenter()
        _ = s.ingest(.volatile("I am also c"), at: 0.1)
        XCTAssertEqual(s.pendingText, "I am also")
        _ = s.ingest(.volatile("I am also curious"), at: 0.4)
        XCTAssertEqual(s.pendingText, "I am also")
        _ = s.ingest(.volatile("I am also curious how"), at: 0.7)
        XCTAssertEqual(s.pendingText, "I am also curious")
    }

    func testPendingTextKeepsEverythingAfterSpaceOrPunctuation() {
        XCTAssertEqual(Segmenter.stableTail("what was the "), "what was the ")
        XCTAssertEqual(Segmenter.stableTail("and how did you find it?"), "and how did you find it?")
        XCTAssertEqual(Segmenter.stableTail("the root cause,"), "the root cause,")
    }

    func testPendingTextHidesLoneIncompleteFirstWord() {
        // 一句话刚开头、还没有空格时，没有任何完整的词可显示
        XCTAssertEqual(Segmenter.stableTail("Wha"), "")
    }

    func testPendingTextDoesNotTrimChinese() {
        // 中文没有词边界，逐字出字本身就是正常阅读单位
        XCTAssertEqual(Segmenter.stableTail("你能讲一下你"), "你能讲一下你")
    }

    func testFinalizedHeadIsNeverTrimmed() {
        var s = makeSegmenter()
        // 定稿部分是不可变的，哪怕后面跟着未完成的尾词也不能被裁掉
        _ = s.ingest(.finalized("What was the root cause"), at: 0.5)
        _ = s.ingest(.volatile("and ho"), at: 0.8)
        XCTAssertEqual(s.pendingText, "What was the root cause and")
    }
}
