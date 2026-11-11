import Foundation
import Translation

/// 基于 Apple Translation 框架的本机翻译。
///
/// PRD 7.4 #4 的实测结论：
/// `TranslationSession(installedSource:target:)` 这个直接构造器是 **macOS 26.0+** 才有的
/// （SDK 26.5 的 Translation.swiftinterface 里标着 `@available(iOS 26.0, macOS 26.0, *)`）。
/// 在 macOS 15 上框架虽然存在，但唯一拿到 `TranslationSession` 的途径是 SwiftUI 的
/// `.translationTask(configuration)` 修饰器 —— 意味着翻译必须挂在一个活着的 View 上，
/// 非 UI 层拿不到会话。这是我们把部署目标定在 macOS 26 的主要原因之一。
///
/// 为什么是 `final class` 而不是 `actor`：`TranslationSession` 是 non-Sendable 的类，
/// 在 Swift 6 完全并发检查下，从 actor 里 `await session.translate(...)`
/// 等于把一个 non-Sendable 对象跨隔离域传递，编译器直接拒绝。
/// 这里用非隔离类 + 锁保护引用，调用方（翻译串行队列）本身就是单线程串行的。
final class AppleTranslator: Translator, @unchecked Sendable {
    private let source: Locale.Language
    private let target: Locale.Language
    private let lock = NSLock()
    private var session: TranslationSession?

    init(source: Locale.Language, target: Locale.Language) {
        self.source = source
        self.target = target
    }

    func prepare() async throws {
        let availability = LanguageAvailability()
        let status = await availability.status(from: source, to: target)
        let sourceID = source.maximalIdentifier
        let targetID = target.maximalIdentifier

        switch status {
        case .unsupported:
            throw TranslatorError.unsupportedPair(source: sourceID, target: targetID)
        case .installed:
            break
        case .supported:
            // 语言包支持但未安装 —— 走系统下载流程（F5）
            Log.translate.info("语言包未安装，尝试触发下载 \(sourceID) → \(targetID)")
            let probe = TranslationSession(installedSource: source, target: target)
            guard probe.canRequestDownloads else {
                throw TranslatorError.cannotRequestDownload
            }
            do {
                try await probe.prepareTranslation()
            } catch {
                Log.translate.error("语言包下载失败: \(error.localizedDescription)")
                throw TranslatorError.languagePackNotInstalled(source: sourceID, target: targetID)
            }
        @unknown default:
            break
        }

        let session = TranslationSession(installedSource: source, target: target)
        try await session.prepareTranslation()
        lock.withLock { self.session = session }
        Log.translate.info("本机翻译就绪 \(sourceID) → \(targetID)")
    }

    func translate(_ text: String, context: [String]) async throws -> String {
        // Apple 本机翻译是逐句无状态的，context 对它无效；留给 BYOK 实现用。
        _ = context
        guard let session = lock.withLock({ self.session }) else { throw TranslatorError.notPrepared }
        let response = try await session.translate(text)
        return response.targetText
    }

    func invalidate() {
        let session = lock.withLock { () -> TranslationSession? in
            let current = self.session
            self.session = nil
            return current
        }
        session?.cancel()
    }
}

/// 不翻译，原样返回。翻译引擎不可用时降级成「仅原文」模式，而不是整条管道挂掉。
final class PassthroughTranslator: Translator {
    func prepare() async throws {}
    func translate(_ text: String, context: [String]) async throws -> String { "" }
}
