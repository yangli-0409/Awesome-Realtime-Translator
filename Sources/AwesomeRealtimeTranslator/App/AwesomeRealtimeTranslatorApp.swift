import AppKit
import SwiftUI

@main
struct AwesomeRealtimeTranslatorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    private var coordinator = AppCoordinator.shared

    var body: some Scene {
        MenuBarExtra {
            MenuBarView(coordinator: coordinator)
        } label: {
            Image(systemName: menuBarSymbol)
        }

        // 没有 Settings 场景：它只能通过 responder chain 打开，而这个应用没有 key window，
        // 那条路必然失败（详见 SettingsWindow）。设置窗由 SettingsWindow 自己管。
    }

    private var menuBarSymbol: String {
        switch coordinator.state {
        case .running: "captions.bubble.fill"
        case .paused, .preparing: "captions.bubble"
        case .failed: "exclamationmark.bubble"
        case .idle: "captions.bubble"
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        MainActor.assumeIsolated {
            let coordinator = AppCoordinator.shared
            HotKeyCenter.shared.registerDefaults(
                toggleListening: { coordinator.toggle() },
                togglePanel: { coordinator.panel.toggleVisibility() }
            )
            Log.ui.info("应用启动 bundle=\(Bundle.main.bundleURL.path) 屏幕录制=\(Permissions.hasScreenRecordingAccess)")

            // 冒烟测试用：`open <app> --args --autostart` 启动后直接开始会话，
            // 不需要人去点菜单栏。日常使用不会带这个参数。
            if CommandLine.arguments.contains("--autostart") {
                Log.ui.info("--autostart：自动开始会话")
                Task { await coordinator.start() }
                return
            }

            // 权限齐了就把字幕窗摆出来，哪怕还没开始听。
            //
            // 实测踩的坑：LSUIElement 应用没有 Dock 图标，菜单栏图标又可能被刘海
            // 或者一堆菜单栏项挤掉。于是「授权完成 → 重启 → 什么都没发生」，
            // 用户的感受是「这个 app 打不开」—— 进程其实活着，只是零可见性。
            // 悬浮窗自带状态点和 ▶ 按钮，摆出来既是「我开着」的证据，也是入口。
            if Permissions.hasScreenRecordingAccess {
                coordinator.store.statusMessage = "点左上角 ▶ 或按 ⌥⌘T 开始听"
                coordinator.panel.show()
                return
            }

            // 权限没给齐就先把引导页摆在用户面前，而不是等他点「开始」才报错
            OnboardingWindow.show()
            // 主动请求一次：系统的「屏幕与系统音频录制」列表只收录请求过的应用，
            // 用户没法手动添加。不在启动时登记，用户去设置里会找不到这个应用。
            // 等激活策略和窗口生效再请求，否则 TCC 仍可能把我们当后台应用
            Task {
                try? await Task.sleep(for: .milliseconds(600))
                await Permissions.forceScreenCapturePrompt()
            }
        }
    }

    /// 在 Finder / Dock 里再次打开已经在运行的应用时，把字幕窗重新摆出来。
    /// 没有这一步，用户双击应用图标会「毫无反应」—— 系统只是把事件转给了现有进程。
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        MainActor.assumeIsolated {
            AppCoordinator.shared.panel.show()
        }
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated {
            HotKeyCenter.shared.unregisterAll()
        }
    }
}
