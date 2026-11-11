import AppKit
import CoreGraphics
import ScreenCaptureKit
import Speech

enum PermissionState: Equatable {
    case granted
    case denied
    case undetermined
    /// 不需要这项权限（当前实现用不到）
    case notRequired
}

enum Permissions {
    // MARK: - 屏幕与系统音频录制

    /// ScreenCaptureKit 采集系统音频走的是「屏幕与系统音频录制」这一项 TCC 权限。
    static var hasScreenRecordingAccess: Bool {
        CGPreflightScreenCaptureAccess()
    }

    /// 首次调用会弹系统授权框。注意：**授权后必须重启本进程才生效**（PRD 7.4 #5），
    /// 因为 TCC 的判定结果在进程启动时缓存。
    @discardableResult
    static func requestScreenRecordingAccess() -> Bool {
        CGRequestScreenCaptureAccess()
    }

    /// 真正能把应用登记进「屏幕与系统音频录制」列表的做法。
    ///
    /// 踩过的坑：光调 `CGRequestScreenCaptureAccess()` 不一定弹框，也不一定在系统设置里
    /// 留下条目。而那个列表**只列出请求过的应用，用户无法手动添加** —— 于是会出现
    /// 「设置里找不到这个应用」的死局。
    /// 可靠的触发点是真的去问一次 ScreenCaptureKit 要可共享内容：
    /// 未决定时系统弹授权框，被拒时抛错，两种情况都会在 TCC 里留下记录。
    @discardableResult
    static func forceScreenCapturePrompt() async -> Bool {
        Log.ui.info("请求屏幕录制权限：preflight=\(hasScreenRecordingAccess)")
        let granted = CGRequestScreenCaptureAccess()
        Log.ui.info("CGRequestScreenCaptureAccess 返回 \(granted)")
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            Log.ui.info("ScreenCaptureKit 可用，显示器数量 \(content.displays.count)")
            return true
        } catch {
            Log.ui.error("ScreenCaptureKit 被拒或不可用: \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    static func openScreenRecordingSettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
    }

    // MARK: - 语音识别

    /// macOS 26 的 `SpeechAnalyzer` / `SpeechTranscriber` 全程本机，实测不触发
    /// SFSpeechRecognizer 的 TCC 授权流程，所以当前实现返回 `.notRequired`。
    /// 一旦换成 `SFSpeechRecognizer` 实现（Intel / macOS 15 回退路径），改成读真实状态即可。
    static var speechRecognitionState: PermissionState {
        guard SpeechTranscriber.isAvailable else { return legacySpeechState }
        return .notRequired
    }

    static var legacySpeechState: PermissionState {
        switch SFSpeechRecognizer.authorizationStatus() {
        case .authorized: .granted
        case .denied, .restricted: .denied
        case .notDetermined: .undetermined
        @unknown default: .undetermined
        }
    }

    static func requestLegacySpeechRecognition() async -> PermissionState {
        await withCheckedContinuation { continuation in
            SFSpeechRecognizer.requestAuthorization { status in
                let state: PermissionState = switch status {
                case .authorized: .granted
                case .denied, .restricted: .denied
                case .notDetermined: .undetermined
                @unknown default: .undetermined
                }
                continuation.resume(returning: state)
            }
        }
    }

    static func openSpeechRecognitionSettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_SpeechRecognition")
    }

    // MARK: - 翻译语言包

    static func openTranslationLanguageSettings() {
        // 「系统设置 › 通用 › 语言与地区」里有「翻译语言」入口
        open("x-apple.systempreferences:com.apple.Localization-Settings.extension")
    }

    // MARK: - 重启

    /// 屏幕录制权限授予后需要重启进程。
    static func relaunchApp() {
        let url = Bundle.main.bundleURL
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: url, configuration: configuration) { _, _ in
            DispatchQueue.main.async { NSApp.terminate(nil) }
        }
    }

    private static func open(_ urlString: String) {
        guard let url = URL(string: urlString) else { return }
        NSWorkspace.shared.open(url)
    }
}
