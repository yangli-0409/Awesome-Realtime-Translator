import AppKit
import SwiftUI

/// 自己管的设置窗口。
///
/// 为什么不用 SwiftUI 的 `Settings` 场景 + `SettingsLink` / `showSettingsWindow:`：
/// 那条路要走 **responder chain**，而 `sendAction(to: nil)` 是从 key window 开始找目标的。
/// 这个应用没有 key window —— 字幕面板 `canBecomeKey = false`（不能抢 Zoom 的键盘输入），
/// 又是 LSUIElement，没有 Dock 图标也没有主菜单。于是责任链是空的，
/// `sendAction` 直接返回 false，点设置毫无反应。实测就是这样。
///
/// 自己建 `NSWindow` 绕开整条责任链，行为完全确定。和 `OnboardingWindow` 同一个套路。
@MainActor
enum SettingsWindow {
    private static var window: NSWindow?

    static func show(coordinator: AppCoordinator) {
        if window == nil {
            let hosting = NSHostingController(rootView: SettingsView(coordinator: coordinator))
            let newWindow = NSWindow(contentViewController: hosting)
            newWindow.title = "设置"
            newWindow.styleMask = [.titled, .closable]
            newWindow.isReleasedWhenClosed = false
            newWindow.center()
            window = newWindow
        }
        // 不提 .regular 激活策略：那会长出 Dock 图标，而 Dock 会被屏幕共享录进去。
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
