import Foundation

/// 翻译引擎。
///
/// `context` 是最近几句已定稿原文，给需要上下文的引擎（LLM / BYOK，F12）用；
/// Apple 的本机翻译是逐句无状态的，会忽略它。
protocol Translator: AnyObject, Sendable {
    /// 语言包/模型准备，可能触发下载。
    func prepare() async throws
    func translate(_ text: String, context: [String]) async throws -> String
}

enum TranslatorError: LocalizedError {
    case unsupportedPair(source: String, target: String)
    case languagePackNotInstalled(source: String, target: String)
    case cannotRequestDownload
    case notPrepared

    var errorDescription: String? {
        switch self {
        case .unsupportedPair(let source, let target):
            "系统本机翻译不支持 \(source) → \(target) 这个语言对。"
        case .languagePackNotInstalled(let source, let target):
            "\(source) → \(target) 的翻译语言包还没安装。"
        case .cannotRequestDownload:
            "现在无法发起语言包下载，请到「系统设置 › 通用 › 语言与地区 › 翻译语言」手动下载。"
        case .notPrepared:
            "翻译引擎还没准备好。"
        }
    }

    var recoverySuggestion: String? {
        switch self {
        case .languagePackNotInstalled, .cannotRequestDownload:
            "打开「系统设置 › 通用 › 语言与地区 › 翻译语言」下载后重试。"
        default: nil
        }
    }
}
