import AppKit
import SwiftUI

/// 权限引导窗口（F2）。
@MainActor
enum OnboardingWindow {
    private static var window: NSWindow?

    static func show() {
        if window == nil {
            let hosting = NSHostingController(rootView: OnboardingView())
            let newWindow = NSWindow(contentViewController: hosting)
            newWindow.title = "开始之前"
            newWindow.styleMask = [.titled, .closable]
            newWindow.isReleasedWhenClosed = false
            newWindow.setContentSize(NSSize(width: 520, height: 430))
            newWindow.center()
            window = newWindow
        }
        // 这里必须把激活策略提成 .regular，不能只 activate。
        //
        // 原因（实测 tccd 日志得出）：LSUIElement 应用不是前台应用，TCC 对它的
        // 屏幕录制授权请求会降级成**通知**而不是模态对话框，日志里是
        // `Notifying for access kTCCServiceScreenCapture ... promptType:1, DB Action:None`。
        // 通知被专注模式或通知设置吞掉的话，用户就什么都看不到；而 `DB Action:None`
        // 意味着不写 TCC 数据库，于是应用也不会出现在系统设置的列表里。
        // 「屏幕与系统音频录制」面板没有「+」按钮，用户无法手动添加 —— 彻底的死局。
        //
        // 提成 .regular 之后应用有 Dock 图标、是正常前台应用，授权请求才会弹模态框。
        // 授权完成后 restoreAccessoryPolicy() 再切回菜单栏应用。
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    /// 引导结束后切回「只在菜单栏」。
    static func restoreAccessoryPolicy() {
        window?.orderOut(nil)
        NSApp.setActivationPolicy(.accessory)
    }
}

struct OnboardingView: View {
    @State private var screenGranted = Permissions.hasScreenRecordingAccess
    @State private var requestedScreenAccess = false

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 6) {
                Text("面试实时字幕")
                    .font(.title2.bold())
                Text("抓取电脑正在播放的声音，在悬浮窗里显示原文和中文翻译。全程本机处理，不联网，不加入会议，对方看不到任何东西。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider()

            permissionRow(
                title: "屏幕与系统音频录制",
                detail: screenGranted
                    ? "已授权。可以采集 Zoom / Chrome / Safari 播放的声音。"
                    : "必需。macOS 把「采集系统声音」归在这一项权限下，只用来读音频，不会截图、不录屏。",
                granted: screenGranted
            ) {
                if requestedScreenAccess {
                    Permissions.openScreenRecordingSettings()
                } else {
                    requestedScreenAccess = true
                    Task {
                        await Permissions.forceScreenCapturePrompt()
                        screenGranted = Permissions.hasScreenRecordingAccess
                    }
                }
            } actionLabel: {
                requestedScreenAccess ? "打开系统设置" : "请求授权"
            }

            permissionRow(
                title: "语音识别",
                detail: Permissions.speechRecognitionState == .notRequired
                    ? "本机识别无需单独授权（macOS 26 的 SpeechAnalyzer 不走网络、不走 Siri 授权流程）。"
                    : "需要授权才能转写文字。",
                granted: Permissions.speechRecognitionState != .denied
            ) {
                Permissions.openSpeechRecognitionSettings()
            } actionLabel: {
                "打开系统设置"
            }

            if !screenGranted, requestedScreenAccess {
                GroupBox {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("授权之后需要重启本应用")
                            .font(.callout.bold())
                        Text("macOS 在进程启动时缓存屏幕录制权限的判定结果，刚授权的进程读到的还是旧值。这是系统行为，不是 bug。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                        Button("立即重启应用") { Permissions.relaunchApp() }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(4)
                }
            }

            Spacer(minLength: 0)

            Text("合规提示：不同国家和地区对会议录音 / 转写的告知义务不同。本应用不录音、不保存音频，但文字转写是否需要告知对方，请你自行确认。")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack {
                Button("重新检测") { screenGranted = Permissions.hasScreenRecordingAccess }
                Spacer()
                Button("开始使用") {
                    OnboardingWindow.restoreAccessoryPolicy()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!screenGranted)
            }
        }
        .padding(24)
        .frame(width: 520)
    }

    @ViewBuilder
    private func permissionRow(
        title: String,
        detail: String,
        granted: Bool,
        action: @escaping () -> Void,
        actionLabel: () -> String
    ) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: granted ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .foregroundStyle(granted ? .green : .orange)
                .font(.title3)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.headline)
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            if !granted {
                Button(actionLabel(), action: action)
            }
        }
    }
}
