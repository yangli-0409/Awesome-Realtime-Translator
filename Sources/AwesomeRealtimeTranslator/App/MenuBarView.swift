import AppKit
import SwiftUI

/// 菜单栏菜单（F9）。M1 只要「开始 / 停止 / 退出」，这里顺手把显示模式和语言也放进来，
/// 因为它们是面试前最后一秒会调的东西，埋进设置窗口反而慢。
struct MenuBarView: View {
    let coordinator: AppCoordinator

    private var settings: AppSettings { coordinator.settings }

    var body: some View {
        Text(coordinator.statusLabel)

        Divider()

        Button(primaryActionTitle) {
            coordinator.toggle()
        }
        .keyboardShortcut("t", modifiers: [.option, .command])

        Button("停止并结束会话") {
            Task { await coordinator.stop() }
        }
        .disabled(!coordinator.state.isActive)

        Button(coordinator.panel.isVisible ? "隐藏字幕窗" : "显示字幕窗") {
            coordinator.panel.toggleVisibility()
        }
        .keyboardShortcut("h", modifiers: [.option, .command])

        Divider()

        Picker("显示模式", selection: Binding(
            get: { settings.displayMode },
            set: { settings.displayMode = $0 }
        )) {
            ForEach(DisplayMode.allCases) { mode in
                Text(mode.label).tag(mode)
            }
        }

        Picker("面试官语言", selection: Binding(
            get: { settings.sourceLanguage },
            set: { settings.sourceLanguage = $0 }
        )) {
            ForEach(SupportedLanguages.sources, id: \.id) { item in
                Text(item.label).tag(item.id)
            }
        }
        .disabled(coordinator.state.isActive)

        Picker("翻译成", selection: Binding(
            get: { settings.targetLanguage },
            set: { settings.targetLanguage = $0 }
        )) {
            ForEach(SupportedLanguages.targets, id: \.id) { item in
                Text(item.label).tag(item.id)
            }
        }
        .disabled(coordinator.state.isActive)

        Toggle("鼠标穿透字幕窗", isOn: Binding(
            get: { settings.clickThrough },
            set: { settings.clickThrough = $0; coordinator.panel.applySettings() }
        ))

        Toggle("屏幕共享时对他人隐藏", isOn: Binding(
            get: { settings.hideFromScreenSharing },
            set: { settings.hideFromScreenSharing = $0; coordinator.panel.applySettings() }
        ))

        Divider()

        Button("复制本场记录") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(coordinator.store.plainText, forType: .string)
        }
        .disabled(coordinator.store.lines.isEmpty)

        Button("导出 Markdown…") {
            SessionArchive.exportWithDialog(
                markdown: coordinator.store.markdown,
                suggestedName: "interview-transcript.md"
            )
        }
        .disabled(coordinator.store.lines.isEmpty)

        Divider()

        // 和字幕窗的齿轮指向同一个窗口，避免出现两个内容相同的设置窗
        Button("设置…") { SettingsWindow.show(coordinator: coordinator) }
            .keyboardShortcut(",", modifiers: .command)

        Button("权限与首次设置…") {
            OnboardingWindow.show()
        }

        Button("退出") {
            Task {
                await coordinator.stop()
                NSApp.terminate(nil)
            }
        }
        .keyboardShortcut("q", modifiers: .command)
    }

    private var primaryActionTitle: String {
        switch coordinator.state {
        case .running: "暂停 (\(HotKeyCenter.toggleListeningShortcut))"
        case .paused: "继续 (\(HotKeyCenter.toggleListeningShortcut))"
        case .preparing: "正在准备…"
        case .idle, .failed: "开始听 (\(HotKeyCenter.toggleListeningShortcut))"
        }
    }
}
