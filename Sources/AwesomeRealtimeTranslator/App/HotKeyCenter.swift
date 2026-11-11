import AppKit
import Carbon.HIToolbox

/// 全局快捷键（F9）。
///
/// 用 Carbon 的 `RegisterEventHotKey` 而不是 `NSEvent.addGlobalMonitorForEvents`：
/// 后者需要「辅助功能」权限，对一个只做字幕的应用来说要得太多，而且会被安全软件盯上。
/// Carbon 这套 API 虽然老，但不需要任何额外权限，沙盒内可用，至今是官方支持的路径。
@MainActor
final class HotKeyCenter {
    static let shared = HotKeyCenter()

    enum Action: UInt32 {
        case toggleListening = 1
        case togglePanel = 2
    }

    private var handlers: [UInt32: () -> Void] = [:]
    private var hotKeyRefs: [EventHotKeyRef?] = []
    private var eventHandler: EventHandlerRef?

    private init() {}

    func registerDefaults(toggleListening: @escaping () -> Void, togglePanel: @escaping () -> Void) {
        installEventHandlerIfNeeded()
        // ⌥⌘T 开始/暂停，⌥⌘H 显示/隐藏字幕窗
        register(.toggleListening, keyCode: UInt32(kVK_ANSI_T), handler: toggleListening)
        register(.togglePanel, keyCode: UInt32(kVK_ANSI_H), handler: togglePanel)
    }

    static let toggleListeningShortcut = "⌥⌘T"
    static let togglePanelShortcut = "⌥⌘H"

    private func register(_ action: Action, keyCode: UInt32, handler: @escaping () -> Void) {
        handlers[action.rawValue] = handler
        var hotKeyID = EventHotKeyID(signature: OSType(0x4152_5431), id: action.rawValue) // 'ART1'
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(
            keyCode,
            UInt32(optionKey | cmdKey),
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &ref
        )
        if status == noErr {
            hotKeyRefs.append(ref)
        } else {
            Log.ui.error("注册全局快捷键失败 status=\(status)")
        }
        _ = hotKeyID
    }

    fileprivate func fire(_ id: UInt32) {
        handlers[id]?()
    }

    private func installEventHandlerIfNeeded() {
        guard eventHandler == nil else { return }
        var spec = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )
        InstallEventHandler(
            GetApplicationEventTarget(),
            { _, event, _ -> OSStatus in
                var hotKeyID = EventHotKeyID()
                let status = GetEventParameter(
                    event,
                    EventParamName(kEventParamDirectObject),
                    EventParamType(typeEventHotKeyID),
                    nil,
                    MemoryLayout<EventHotKeyID>.size,
                    nil,
                    &hotKeyID
                )
                guard status == noErr else { return status }
                let id = hotKeyID.id
                DispatchQueue.main.async {
                    MainActor.assumeIsolated { HotKeyCenter.shared.fire(id) }
                }
                return noErr
            },
            1,
            &spec,
            nil,
            &eventHandler
        )
    }

    func unregisterAll() {
        for ref in hotKeyRefs { if let ref { UnregisterEventHotKey(ref) } }
        hotKeyRefs.removeAll()
        handlers.removeAll()
    }
}
