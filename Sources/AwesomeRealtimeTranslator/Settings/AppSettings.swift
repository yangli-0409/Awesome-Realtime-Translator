import Foundation
import Observation

enum DisplayMode: String, CaseIterable, Identifiable, Sendable {
    case sourceOnly
    case translationOnly
    case bilingual

    var id: String { rawValue }

    var label: String {
        switch self {
        case .sourceOnly: "仅原文"
        case .translationOnly: "仅译文"
        case .bilingual: "双语对照"
        }
    }

    var showsSource: Bool { self != .translationOnly }
    var showsTranslation: Bool { self != .sourceOnly }
}

/// 用户设置。UserDefaults 持久化，改一处就落盘。
@MainActor
@Observable
final class AppSettings {
    static let shared = AppSettings()

    private let defaults: UserDefaults

    // MARK: - 语言（F8）

    /// 面试官说的语言
    var sourceLanguage: String { didSet { persist(sourceLanguage, "sourceLanguage") } }
    /// 你要读的语言
    var targetLanguage: String { didSet { persist(targetLanguage, "targetLanguage") } }

    // MARK: - 显示（F7）

    var displayMode: DisplayMode { didSet { persist(displayMode.rawValue, "displayMode") } }
    var fontSize: Double { didSet { persist(fontSize, "fontSize") } }
    var opacity: Double { didSet { persist(opacity, "opacity") } }
    var visibleLines: Int { didSet { persist(visibleLines, "visibleLines") } }
    var clickThrough: Bool { didSet { persist(clickThrough, "clickThrough") } }

    /// 原文显现节奏：把一批到达的字摊开播放的时长（秒），也等于显示比识别结果滞后多少。
    ///
    /// 识别模型每 ~950ms 吐一批结果，这个值决定怎么花掉这段时间：
    /// - 0.3：几乎不补偿，跟手但能看到「每秒跳一次」
    /// - 0.5：默认。基本连续，每批末尾约 450ms 轻微停顿
    /// - 0.95：刚好填平批次间隔，最连续，也最滞后
    ///
    /// 上限取 1.0 —— 超过批次间隔之后积压只会越积越多，更滞后但不会更顺滑，
    /// 所以再往上调没有任何收益，不如直接锁住。
    var revealPace: Double { didSet { persist(revealPace, "revealPace") } }

    /// 屏幕共享时隐藏字幕窗（PRD §10 未决问题之一）。
    /// 实现方式是 `NSWindow.sharingType = .none`，窗口对所有录屏/共享 API 不可见，
    /// 但你自己仍然看得见 —— 比「检测到共享就隐藏」更可靠，所以默认开启。
    var hideFromScreenSharing: Bool { didSet { persist(hideFromScreenSharing, "hideFromScreenSharing") } }

    // MARK: - 术语表（F11 预埋）

    var glossary: [String] { didSet { persist(glossary, "glossary") } }

    // MARK: - 会话记录（PRD §10 未决问题之一）

    /// 默认**不**保存。面试记录含第三方可识别信息，默认留痕对隐私不友好；
    /// 想复盘的人自己打开，是更安全的默认值。
    var saveTranscripts: Bool { didSet { persist(saveTranscripts, "saveTranscripts") } }

    private init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        sourceLanguage = defaults.string(forKey: "sourceLanguage") ?? "en-US"
        targetLanguage = defaults.string(forKey: "targetLanguage") ?? "zh-Hans"
        displayMode = DisplayMode(rawValue: defaults.string(forKey: "displayMode") ?? "") ?? .bilingual
        fontSize = defaults.object(forKey: "fontSize") as? Double ?? 17
        opacity = defaults.object(forKey: "opacity") as? Double ?? 0.82
        visibleLines = defaults.object(forKey: "visibleLines") as? Int ?? 3
        clickThrough = defaults.object(forKey: "clickThrough") as? Bool ?? false
        revealPace = min(1.0, max(0.3, defaults.object(forKey: "revealPace") as? Double ?? 0.5))
        hideFromScreenSharing = defaults.object(forKey: "hideFromScreenSharing") as? Bool ?? true
        glossary = defaults.stringArray(forKey: "glossary") ?? []
        saveTranscripts = defaults.object(forKey: "saveTranscripts") as? Bool ?? false
    }

    var sourceLocale: Locale { Locale(identifier: sourceLanguage) }
    var sourceLanguageCode: Locale.Language { Locale.Language(identifier: sourceLanguage) }
    var targetLanguageCode: Locale.Language { Locale.Language(identifier: targetLanguage) }

    private func persist(_ value: Any, _ key: String) {
        defaults.set(value, forKey: key)
    }
}

/// 可选语言对。MVP 只验证了 英→中；其余是框架支持、未实测。
enum SupportedLanguages {
    static let sources: [(id: String, label: String)] = [
        ("en-US", "英语（美国）"),
        ("en-GB", "英语（英国）"),
        ("fr-FR", "法语（法国）"),
        ("fr-CA", "法语（加拿大）"),
        ("de-DE", "德语"),
        ("es-ES", "西班牙语"),
        ("ja-JP", "日语"),
        ("zh-CN", "中文（普通话）"),
    ]

    static let targets: [(id: String, label: String)] = [
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁体中文"),
        ("en-US", "英语"),
        ("fr-FR", "法语"),
    ]
}
