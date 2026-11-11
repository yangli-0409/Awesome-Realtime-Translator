import AppKit
import Foundation

/// 会话记录落盘（F10 的存储层）。
/// 只写文本，永不写音频 —— 「音频不落盘」是硬约束，不是开关。
enum SessionArchive {
    static var directory: URL? {
        guard let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else {
            return nil
        }
        return base.appendingPathComponent("Sessions", isDirectory: true)
    }

    @discardableResult
    static func save(markdown: String, startedAt: Date) -> URL? {
        guard let directory else { return nil }
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let formatter = DateFormatter()
            formatter.dateFormat = "yyyy-MM-dd-HHmm"
            let url = directory.appendingPathComponent("interview-\(formatter.string(from: startedAt)).md")
            try markdown.write(to: url, atomically: true, encoding: .utf8)
            Log.session.info("会话记录已保存")
            return url
        } catch {
            Log.session.error("保存会话记录失败: \(error.localizedDescription)")
            return nil
        }
    }

    /// 导出到用户选的位置（走 NSSavePanel，沙盒下由 user-selected 权限覆盖）。
    @MainActor
    static func exportWithDialog(markdown: String, suggestedName: String) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = suggestedName
        panel.canCreateDirectories = true
        panel.allowedContentTypes = [.init(filenameExtension: "md") ?? .plainText]
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            try? markdown.write(to: url, atomically: true, encoding: .utf8)
        }
    }
}
