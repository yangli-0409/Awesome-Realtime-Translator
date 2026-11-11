import AppKit
import SwiftUI

/// 悬浮窗里的字幕内容。
///
/// 两条反闪烁规则：
/// - 原文区：历史句子 + 当前未定稿尾句（允许变动，只有最后一行会动）。
/// - 译文区：只渲染已定稿句子的译文，译文一旦出现就不再改写。
struct CaptionView: View {
    let store: CaptionStore
    let settings: AppSettings
    var onToggle: () -> Void

    private var isRunning: Bool { store.isRunning }

    private var visible: [CaptionLine] { store.tail(settings.visibleLines) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().opacity(0.25)
            content
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background {
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(.black.opacity(settings.opacity))
        }
        .overlay {
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(.white.opacity(0.12), lineWidth: 1)
        }
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    // MARK: - 顶部拖动条

    private var header: some View {
        HStack(spacing: 8) {
            Circle()
                .fill(isRunning ? Color.green : Color.secondary)
                .frame(width: 7, height: 7)
            Text(isRunning ? "正在听" : "已暂停")
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(.white.opacity(0.65))
            Spacer(minLength: 0)
            if settings.clickThrough {
                Text("鼠标穿透")
                    .font(.system(size: 10))
                    .foregroundStyle(.white.opacity(0.4))
            }
            Button(action: onToggle) {
                Image(systemName: isRunning ? "pause.fill" : "play.fill")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(.white.opacity(0.7))
            }
            .buttonStyle(.plain)
            .help(isRunning ? "暂停" : "开始")

            Button(action: Self.openSettings) {
                Image(systemName: "gearshape.fill")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(.white.opacity(0.45))
            }
            .buttonStyle(.plain)
            .help("设置")

            // 退出入口必须在这里。菜单栏图标会被刘海或者一堆菜单栏项挤掉，
            // 而这是个 LSUIElement 应用：没有 Dock 图标，字幕窗又不接受键盘焦点，
            // 所以 ⌘Q 也到不了。没有这个按钮，用户只能去终端 pkill。
            Button {
                NSApp.terminate(nil)
            } label: {
                Image(systemName: "power")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(.white.opacity(0.45))
            }
            .buttonStyle(.plain)
            .help("退出面试字幕")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .contentShape(Rectangle())
    }

    // MARK: - 字幕正文

    private var content: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 10) {
                    if let message = store.statusMessage {
                        Text(message)
                            .font(.system(size: settings.fontSize - 3))
                            .foregroundStyle(.yellow.opacity(0.9))
                            .textSelection(.enabled)
                    }

                    ForEach(visible) { line in
                        lineView(line)
                            .id(line.id)
                    }

                    if settings.displayMode.showsSource, !store.pendingSource.isEmpty {
                        StreamingText(text: store.pendingSource, pace: settings.revealPace)
                            .font(.system(size: settings.fontSize, weight: .regular))
                            .foregroundStyle(.white.opacity(0.55))
                            .id(Self.pendingAnchor)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
            }
            // 逐字显现时文本高度是连续长高的，而 onChange 只在「批」到达时才触发，
            // 中间的增长没人管会把内容顶到可视区以下。底部锚定让它自己跟住。
            .defaultScrollAnchor(.bottom)
            .scrollIndicators(.never)
            .onChange(of: store.lines.count) { _, _ in scrollToBottom(proxy) }
            .onChange(of: store.pendingSource) { _, _ in scrollToBottom(proxy) }
        }
    }

    private static let pendingAnchor = "pending-tail"

    /// 打开设置。见 `SettingsWindow` 里为什么不能走 `showSettingsWindow:`。
    private static func openSettings() {
        SettingsWindow.show(coordinator: .shared)
    }

    /// 刻意**不加动画**。
    ///
    /// 流式原文每秒要变好几次，每次变化都触发一次滚动；如果带 easeOut 动画，
    /// 上一次动画还没跑完就被下一次打断，看起来就是持续抖动 —— 用户反馈的
    /// 「像卡顿」主要来自这里。无动画的瞬时定位反而稳定。
    private func scrollToBottom(_ proxy: ScrollViewProxy) {
        if !store.pendingSource.isEmpty {
            proxy.scrollTo(Self.pendingAnchor, anchor: .bottom)
        } else if let last = visible.last {
            proxy.scrollTo(last.id, anchor: .bottom)
        }
    }

    @ViewBuilder
    private func lineView(_ line: CaptionLine) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            if settings.displayMode.showsSource {
                Text(line.source)
                    .font(.system(size: settings.fontSize, weight: .regular))
                    .foregroundStyle(settings.displayMode == .bilingual ? .white.opacity(0.72) : .white)
                    .textSelection(.enabled)
            }
            if settings.displayMode.showsTranslation {
                // 译文行**始终占位**，哪怕译文还没回来。
                //
                // 原来的写法是「译文到了才插入这一行」，插入瞬间整个 block 变高，
                // 上面所有历史行一起往上顶一次 —— 这就是用户说的「布局跳动」。
                // 现在未完成时渲染一个同字号的省略号占位，译文到了只是把文字换掉，
                // 单行译文的高度完全不变，不会推动其它行。
                // （译文超过一行时仍会增高，这是文本流排版的下限，无法在不预测
                //   换行结果的前提下消除。）
                translationSlot(line)
                    .font(.system(size: settings.fontSize + 1, weight: .medium))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// 译文槽位：三种状态共用同一个字号，所以单行时高度一致。
    @ViewBuilder
    private func translationSlot(_ line: CaptionLine) -> some View {
        if let translation = line.translation, !translation.isEmpty {
            Text(translation)
                .foregroundStyle(.white)
                .textSelection(.enabled)
        } else if line.translationFailed {
            Text("（本句翻译失败）")
                .foregroundStyle(.orange.opacity(0.8))
        } else {
            Text("…")
                .foregroundStyle(.white.opacity(0.35))
        }
    }
}
