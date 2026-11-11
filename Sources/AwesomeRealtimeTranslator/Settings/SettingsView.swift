import SwiftUI

struct SettingsView: View {
    let coordinator: AppCoordinator

    private var settings: AppSettings { coordinator.settings }

    var body: some View {
        TabView {
            displayTab
                .tabItem { Label("显示", systemImage: "textformat.size") }
            languageTab
                .tabItem { Label("语言", systemImage: "globe") }
            privacyTab
                .tabItem { Label("隐私", systemImage: "lock") }
        }
        .frame(width: 460, height: 360)
    }

    /// 识别模型每 ~950ms 吐一批结果，这个值决定怎么花掉这段时间 —— 它同时就是显示滞后。
    private var paceHint: String {
        switch settings.revealPace {
        case ..<0.4: "最跟手，但能看到字一跳一跳地出现"
        case ..<0.65: "推荐。基本连续，每批末尾有轻微停顿"
        case ..<0.9: "更顺滑，字比对方说话晚半秒以上"
        default: "最连续，也最滞后；再往上调不会更顺滑"
        }
    }

    // MARK: - 显示（F7）

    private var displayTab: some View {
        Form {
            Picker("显示模式", selection: Binding(
                get: { settings.displayMode },
                set: { settings.displayMode = $0 }
            )) {
                ForEach(DisplayMode.allCases) { Text($0.label).tag($0) }
            }

            Slider(
                value: Binding(get: { settings.fontSize }, set: { settings.fontSize = $0 }),
                in: 12...34,
                step: 1
            ) {
                Text("字号 \(Int(settings.fontSize))")
            }

            Slider(
                value: Binding(get: { settings.opacity }, set: {
                    settings.opacity = $0
                    coordinator.panel.applySettings()
                }),
                in: 0.3...1.0
            ) {
                Text("背景不透明度 \(Int(settings.opacity * 100))%")
            }

            VStack(alignment: .leading, spacing: 2) {
                Slider(
                    value: Binding(get: { settings.revealPace }, set: { settings.revealPace = $0 }),
                    in: 0.3...1.0,
                    step: 0.05
                ) {
                    Text("原文显现节奏 \(settings.revealPace, specifier: "%.2f")s")
                }
                Text(paceHint)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Stepper(
                "显示行数 \(settings.visibleLines)",
                value: Binding(get: { settings.visibleLines }, set: { settings.visibleLines = $0 }),
                in: 1...10
            )

            Toggle("鼠标穿透（点不到字幕窗）", isOn: Binding(
                get: { settings.clickThrough },
                set: { settings.clickThrough = $0; coordinator.panel.applySettings() }
            ))

            Text("快捷键：\(HotKeyCenter.toggleListeningShortcut) 开始/暂停，\(HotKeyCenter.togglePanelShortcut) 显示/隐藏字幕窗")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .formStyle(.grouped)
    }

    // MARK: - 语言（F8）

    private var languageTab: some View {
        Form {
            Picker("面试官说的语言", selection: Binding(
                get: { settings.sourceLanguage },
                set: { settings.sourceLanguage = $0 }
            )) {
                ForEach(SupportedLanguages.sources, id: \.id) { Text($0.label).tag($0.id) }
            }
            .disabled(coordinator.state.isActive)

            Picker("翻译成", selection: Binding(
                get: { settings.targetLanguage },
                set: { settings.targetLanguage = $0 }
            )) {
                ForEach(SupportedLanguages.targets, id: \.id) { Text($0.label).tag($0.id) }
            }
            .disabled(coordinator.state.isActive)

            if coordinator.state.isActive {
                Text("会话进行中不能换语言，先停止会话。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("语言包") {
                Text("识别模型由系统在首次使用时下载；翻译语言包在「系统设置 › 通用 › 语言与地区 › 翻译语言」里管理。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Button("打开翻译语言设置") { Permissions.openTranslationLanguageSettings() }
            }

            Section("当前已验证") {
                Text("英语（美国）→ 简体中文。其余语言对由系统框架支持，但未实测。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    // MARK: - 隐私

    private var privacyTab: some View {
        Form {
            Section {
                Label("默认零网络请求", systemImage: "network.slash")
                Text("应用的沙盒权限里没有声明网络访问，系统层面就发不出任何请求 —— 这不是承诺，是技术约束。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Label("音频永不落盘", systemImage: "waveform.slash")
                Text("音频只在内存里流过识别器，不写文件，也没有录音功能。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("会话记录") {
                Toggle("会话结束后把双语文本保存到本机", isOn: Binding(
                    get: { settings.saveTranscripts },
                    set: { settings.saveTranscripts = $0 }
                ))
                Text("默认关闭。面试记录包含对方的可识别信息，默认不留痕更安全；想复盘再打开。随时可以用菜单里的「复制 / 导出」拿到当前这场的记录。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("屏幕共享") {
                Toggle("屏幕共享时对他人隐藏字幕窗", isOn: Binding(
                    get: { settings.hideFromScreenSharing },
                    set: { settings.hideFromScreenSharing = $0; coordinator.panel.applySettings() }
                ))
                Text("打开后字幕窗对所有录屏和共享 API 不可见，你自己照常看得见。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}
