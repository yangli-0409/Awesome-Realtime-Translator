import AppKit
import SwiftUI

/// 不抢焦点的悬浮面板（F6）。
///
/// 关键属性：
/// - `.nonactivatingPanel` + `canBecomeKey = false`：点它不会把焦点从 Zoom 抢走。
/// - `level = .screenSaver`：盖在全屏的会议窗口之上。
/// - `collectionBehavior` 含 `.canJoinAllSpaces` 和 `.fullScreenAuxiliary`：
///   切桌面、进全屏都跟着走。
/// - `sharingType = .none`：窗口对所有录屏 / 屏幕共享 API 不可见。
///   这同时解决了 PRD §10 的「屏幕共享时隐藏字幕窗」—— 不需要检测共享状态，
///   字幕从源头上就不会出现在别人看到的画面里（也不会被自己采集进去）。
final class CaptionPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class CaptionPanelController {
    private var panel: CaptionPanel?
    private let store: CaptionStore
    private let settings: AppSettings
    private let onToggle: () -> Void

    init(store: CaptionStore, settings: AppSettings, onToggle: @escaping () -> Void) {
        self.store = store
        self.settings = settings
        self.onToggle = onToggle
    }

    var isVisible: Bool { panel?.isVisible ?? false }

    func show() {
        if panel == nil { panel = makePanel() }
        applySettings()
        panel?.orderFrontRegardless()
    }

    func hide() {
        panel?.orderOut(nil)
    }

    func toggleVisibility() {
        isVisible ? hide() : show()
    }

    /// 设置项变化后调用（鼠标穿透、屏幕共享可见性、透明度）。
    func applySettings() {
        guard let panel else { return }
        panel.ignoresMouseEvents = settings.clickThrough
        panel.sharingType = settings.hideFromScreenSharing ? .none : .readOnly
        panel.alphaValue = 1 // 透明度通过视图背景实现，窗口本身保持不透明以免文字变淡
    }

    private func makePanel() -> CaptionPanel {
        let panel = CaptionPanel(
            contentRect: Self.defaultFrame(),
            styleMask: [.nonactivatingPanel, .titled, .fullSizeContentView, .resizable, .utilityWindow],
            backing: .buffered,
            defer: false
        )
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.standardWindowButton(.closeButton)?.isHidden = true
        panel.standardWindowButton(.miniaturizeButton)?.isHidden = true
        panel.standardWindowButton(.zoomButton)?.isHidden = true
        panel.isMovableByWindowBackground = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.minSize = NSSize(width: 360, height: 90)
        panel.animationBehavior = .utilityWindow

        let root = CaptionView(store: store, settings: settings, onToggle: onToggle)
        let hosting = NSHostingView(rootView: root)
        hosting.translatesAutoresizingMaskIntoConstraints = false
        panel.contentView = hosting

        panel.setFrameAutosaveName("CaptionPanelFrame")
        if panel.frame.width < panel.minSize.width { panel.setFrame(Self.defaultFrame(), display: false) }
        return panel
    }

    /// 默认位置：屏幕顶部居中，靠近摄像头，减少视线偏移（PRD §3 核心场景 2）。
    private static func defaultFrame() -> NSRect {
        let width: CGFloat = 780
        let height: CGFloat = 180
        guard let screen = NSScreen.main else {
            return NSRect(x: 100, y: 100, width: width, height: height)
        }
        let visible = screen.visibleFrame
        return NSRect(
            x: visible.midX - width / 2,
            y: visible.maxY - height - 12,
            width: width,
            height: height
        )
    }
}
