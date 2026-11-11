import Foundation

/// 一条已定稿的句子。定稿后文本永不改变 —— 这是译文不闪烁的前提。
struct Sentence: Identifiable, Hashable, Sendable {
    let id: UUID
    let text: String
    /// 相对会话开始的秒数
    let startedAt: TimeInterval
    let endedAt: TimeInterval

    init(id: UUID = UUID(), text: String, startedAt: TimeInterval, endedAt: TimeInterval) {
        self.id = id
        self.text = text
        self.startedAt = startedAt
        self.endedAt = endedAt
    }
}

/// 断句器：把识别器的 volatile/final 事件流切成不可变的句子。
///
/// 纯逻辑、无依赖、无时钟 —— 时间一律由调用方传入，所以可以完整单测（见 SegmenterTests）。
///
/// 三条定稿规则（PRD 7.3）：
/// 1. 句末标点
/// 2. 静音超过 `silenceFlushInterval`
/// 3. 单句超过 `maxSentenceLength`
struct Segmenter {
    struct Configuration: Sendable {
        /// 识别器停止吐字多久之后，把缓冲区当成一句话定稿
        var silenceFlushInterval: TimeInterval = 0.7
        /// 单句字符上限，超过就强制切（防止面试官一口气说 3 分钟导致整段不出译文）
        var maxSentenceLength: Int = 140
        /// 短于这个长度的残片不单独成句，并入下一句
        var minSentenceLength: Int = 2
        var terminators: Set<Character> = [".", "!", "?", "。", "！", "？", "…", ";", "；"]
        /// 这些缩写后面的句点不算句末
        var abbreviations: Set<String> = [
            "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st",
            "e.g", "i.e", "etc", "vs", "approx", "inc", "ltd", "corp",
            "fig", "dept", "univ", "ph.d",
            // 刻意不收 "no" / "co"：口语里 "No." "Co." 远不如 "no." 结句常见，
            // 收了会让 "He said no. Then..." 永远切不开。
        ]

        init() {}
    }

    private let config: Configuration

    /// 已定稿但还没凑成一句的文本
    private var buffer: String = ""
    /// 当前这句话的起始时间
    private var bufferStartedAt: TimeInterval?
    /// 最后一次收到任何事件的时间，用于静音判定
    private var lastActivityAt: TimeInterval = 0
    /// 当前未定稿尾巴，仅供原文区显示
    private(set) var volatileTail: String = ""

    init(configuration: Configuration = .init()) {
        config = configuration
    }

    /// 原文区应该显示的「当前这句」：已定稿的头 + 未定稿的尾。
    ///
    /// 尾巴会**砍掉最后一个没说完的词**。理由：模型的 volatile 结果是逐字追加的，
    /// 实测会出现 `I am also c` → `I am also curious` 这种中间态，屏幕上就是
    /// 一个被截断的词闪一下。只有最末一个 token 可能不完整（前面的词模型已经看全了），
    /// 砍掉它就能根治，代价是原文比识别结果晚一个词（约 300ms）。
    /// 这笔交易划算：`.fastResults` 已经把批次间隔从 3.7s 压到 0.95s。
    var pendingText: String {
        Self.join(buffer, Self.stableTail(volatileTail))
    }

    /// 去掉未完成的尾词。文本以空白或标点结尾时说明最后一个词已完整，原样返回。
    static func stableTail(_ text: String) -> String {
        guard let last = text.last else { return text }
        // 中日韩没有词边界，逐字出字本身就是正常阅读单位，不做裁剪
        if last.isWhitespace || isCJK(last) || "，。！？、；：,.!?;:'\")）》」-".contains(last) {
            return text
        }
        guard let idx = text.lastIndex(where: { $0 == " " || $0 == "\n" }) else { return "" }
        return String(text[..<idx])
    }

    var hasPendingContent: Bool {
        !buffer.trimmed.isEmpty || !volatileTail.trimmed.isEmpty
    }

    // MARK: - 输入

    mutating func ingest(_ event: TranscriptEvent, at time: TimeInterval) -> [Sentence] {
        lastActivityAt = time
        switch event {
        case .volatile(let text):
            volatileTail = text
            if bufferStartedAt == nil, !text.trimmed.isEmpty { bufferStartedAt = time }
            return []

        case .finalized(let text):
            // 一旦有定稿，之前的 volatile 作废（它的内容已包含在定稿里）
            volatileTail = ""
            guard !text.trimmed.isEmpty else { return [] }
            if bufferStartedAt == nil { bufferStartedAt = time }
            buffer = Self.join(buffer, text)
            return cut(at: time)

        case .failed:
            return []
        }
    }

    /// 定时调用（建议 100ms 一次）做静音判定。
    mutating func tick(at time: TimeInterval) -> [Sentence] {
        guard !buffer.trimmed.isEmpty else { return [] }
        guard time - lastActivityAt >= config.silenceFlushInterval else { return [] }
        return flush(at: time)
    }

    /// 会话结束时冲刷剩余内容。
    mutating func flush(at time: TimeInterval) -> [Sentence] {
        let text = buffer.trimmed
        buffer = ""
        volatileTail = ""
        let start = bufferStartedAt ?? time
        bufferStartedAt = nil
        guard !text.isEmpty else { return [] }
        return [Sentence(text: text, startedAt: start, endedAt: time)]
    }

    // MARK: - 切句

    private mutating func cut(at time: TimeInterval) -> [Sentence] {
        var out: [Sentence] = []

        while true {
            if let idx = sentenceEnd(in: buffer) {
                let head = String(buffer[..<idx]).trimmed
                let rest = String(buffer[idx...])
                // 太短的残片（"Yes." "OK."）并入下一句，除非剩余为空
                if head.count < config.minSentenceLength, !rest.trimmed.isEmpty {
                    break
                }
                buffer = Self.dropLeadingSpace(rest)
                if !head.isEmpty {
                    out.append(Sentence(text: head, startedAt: bufferStartedAt ?? time, endedAt: time))
                    bufferStartedAt = buffer.trimmed.isEmpty ? nil : time
                }
                if buffer.trimmed.isEmpty { buffer = ""; break }
                continue
            }

            // 没有句末标点，但太长了：在最后一个词边界硬切
            if buffer.count > config.maxSentenceLength {
                let cutIdx = hardCutIndex(in: buffer, limit: config.maxSentenceLength)
                let head = String(buffer[..<cutIdx]).trimmed
                buffer = Self.dropLeadingSpace(String(buffer[cutIdx...]))
                if !head.isEmpty {
                    out.append(Sentence(text: head, startedAt: bufferStartedAt ?? time, endedAt: time))
                    bufferStartedAt = buffer.trimmed.isEmpty ? nil : time
                }
                continue
            }
            break
        }
        return out
    }

    /// 返回第一个句末标点之后的位置；没有则 nil。
    private func sentenceEnd(in text: String) -> String.Index? {
        var i = text.startIndex
        while i < text.endIndex {
            let ch = text[i]
            if config.terminators.contains(ch) {
                let next = text.index(after: i)
                if isRealSentenceEnd(text, at: i, next: next) {
                    // 把紧跟的引号/括号一并归到前一句
                    var end = next
                    while end < text.endIndex, "\"'”’)）》」".contains(text[end]) {
                        end = text.index(after: end)
                    }
                    return end
                }
            }
            i = text.index(after: i)
        }
        return nil
    }

    private func isRealSentenceEnd(_ text: String, at i: String.Index, next: String.Index) -> Bool {
        let ch = text[i]
        guard ch == "." else { return true } // ? ! 。等不会出现在缩写/小数里

        // 小数点：3.5
        if next < text.endIndex, text[next].isNumber,
           i > text.startIndex, text[text.index(before: i)].isNumber {
            return false
        }
        // 省略号 "..."：前面的点一律不算，最后一个点要看后面接的是不是新句子
        if next < text.endIndex, text[next] == "." { return false }
        if i > text.startIndex, text[text.index(before: i)] == "." {
            // "Well... it depends" 是一句；"Well... Then he left." 是两句
            let rest = text[next...].drop(while: { $0 == " " || $0 == "\u{00A0}" })
            guard let following = rest.first else { return true }
            return following.isUppercase
        }

        // 缩写：取句点前的最后一个「词」
        let head = text[text.startIndex..<i]
        let word = head.split(whereSeparator: { $0 == " " || $0 == "\n" }).last.map(String.init) ?? ""
        let normalized = word.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "(（\"'"))
        if config.abbreviations.contains(normalized) { return false }
        // 单个大写字母 + 点，典型的人名缩写 "J. Smith"
        if normalized.count == 1, word.first?.isUppercase == true { return false }

        return true
    }

    private func hardCutIndex(in text: String, limit: Int) -> String.Index {
        let hardLimit = text.index(text.startIndex, offsetBy: min(limit, text.count))
        // 往前找最近的空白，避免把单词劈开
        var i = hardLimit
        let floor = text.index(text.startIndex, offsetBy: min(limit / 2, text.count))
        while i > floor {
            let prev = text.index(before: i)
            if text[prev] == " " || text[prev] == "," || text[prev] == "，" {
                return i
            }
            i = prev
        }
        return hardLimit
    }

    private static func dropLeadingSpace(_ text: String) -> String {
        String(text.drop(while: { $0.isWhitespace }))
    }

    private static func join(_ a: String, _ b: String) -> String {
        let left = a
        let right = b
        if left.isEmpty { return right }
        if right.isEmpty { return left }
        let needsSpace = !(left.last?.isWhitespace ?? true) && !(right.first?.isWhitespace ?? true)
            && !"，。！？、；：,.!?;:".contains(right.first!)
        // 中日韩之间不加空格
        let leftCJK = left.last.map(Self.isCJK) ?? false
        let rightCJK = right.first.map(Self.isCJK) ?? false
        return left + (needsSpace && !(leftCJK && rightCJK) ? " " : "") + right
    }

    private static func isCJK(_ c: Character) -> Bool {
        guard let s = c.unicodeScalars.first else { return false }
        return (0x3000...0x9FFF).contains(s.value) || (0xF900...0xFAFF).contains(s.value)
    }
}

extension StringProtocol {
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }
}
