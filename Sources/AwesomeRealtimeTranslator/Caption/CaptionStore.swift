import Foundation
import Observation

struct CaptionLine: Identifiable, Sendable {
    let id: UUID
    let source: String
    var translation: String?
    var translationFailed: Bool = false
    let startedAt: TimeInterval
    let wallClock: Date

    var isTranslating: Bool { translation == nil && !translationFailed }
}

/// 字幕状态。单一数据源，悬浮窗和历史记录都从这里读。
@MainActor
@Observable
final class CaptionStore {
    /// 已定稿的句子，按时间升序
    private(set) var lines: [CaptionLine] = []
    /// 未定稿的尾句，只显示在原文区末尾，永不翻译
    var pendingSource: String = ""
    /// 给用户看的一句话状态（权限、下载、降级提示）
    var statusMessage: String?
    var sessionStartedAt: Date?
    /// 悬浮窗要显示运行状态，放在同一个 Observable 里，省掉一层状态同步
    var isRunning: Bool = false

    private let maxLines = 2000

    func startSession() {
        lines.removeAll()
        pendingSource = ""
        sessionStartedAt = Date()
    }

    func append(_ sentence: Sentence) {
        lines.append(CaptionLine(
            id: sentence.id,
            source: sentence.text,
            translation: nil,
            startedAt: sentence.startedAt,
            wallClock: Date()
        ))
        if lines.count > maxLines { lines.removeFirst(lines.count - maxLines) }
    }

    func setTranslation(_ text: String, for id: UUID) {
        guard let index = lines.firstIndex(where: { $0.id == id }) else { return }
        lines[index].translation = text
        lines[index].translationFailed = false
    }

    func markTranslationFailed(for id: UUID) {
        guard let index = lines.firstIndex(where: { $0.id == id }) else { return }
        lines[index].translationFailed = true
    }

    /// 最近 n 条，供悬浮窗显示
    func tail(_ n: Int) -> [CaptionLine] {
        Array(lines.suffix(max(1, n)))
    }

    /// 最近几句原文，给需要上下文的翻译引擎用
    func recentContext(_ n: Int) -> [String] {
        Array(lines.suffix(n).map(\.source))
    }

    // MARK: - 导出（F10 的数据层）

    var plainText: String {
        lines.map { line in
            let stamp = Self.stamp(line.startedAt)
            let translation = line.translation.map { "\n\($0)" } ?? ""
            return "[\(stamp)] \(line.source)\(translation)"
        }.joined(separator: "\n\n")
    }

    var markdown: String {
        let header: String
        if let sessionStartedAt {
            let formatter = DateFormatter()
            formatter.dateFormat = "yyyy-MM-dd HH:mm"
            header = "# 面试记录 \(formatter.string(from: sessionStartedAt))\n\n"
        } else {
            header = "# 面试记录\n\n"
        }
        let body = lines.map { line in
            var block = "**[\(Self.stamp(line.startedAt))]** \(line.source)"
            if let translation = line.translation, !translation.isEmpty {
                block += "\n\n> \(translation)"
            }
            return block
        }.joined(separator: "\n\n")
        return header + body + "\n"
    }

    private static func stamp(_ seconds: TimeInterval) -> String {
        let total = Int(seconds.rounded())
        return String(format: "%02d:%02d", total / 60, total % 60)
    }
}
