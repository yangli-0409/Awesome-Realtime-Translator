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

    /// 备忘录是否展开。刻意**不持久化**：下次打开字幕窗该是看字幕，不是看备忘录。
    @State private var showMemo = false

    private var isRunning: Bool { store.isRunning }

    private var visible: [CaptionLine] { store.tail(settings.visibleLines) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().opacity(0.25)
            // 备忘录**覆盖**字幕区而不是挤在它旁边：窗口只有 180 像素高，
            // 两者并排谁都看不清。而且看备忘录的时候你本来就不在听当前这句。
            if showMemo {
                memoList
            } else {
                pinnedQuestionBar
                content
            }
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

            Button {
                showMemo.toggle()
            } label: {
                Image(systemName: "note.text")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(showMemo ? Color.green.opacity(0.9) : .white.opacity(0.45))
            }
            .buttonStyle(.plain)
            .help(showMemo ? "回到字幕" : "问题备忘录")

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
            .help("退出字幕宝")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .contentShape(Rectangle())
    }

    // MARK: - 钉住的当前问题

    /// 固定在正文上方，不参与滚动。
    ///
    /// 面试里最常见的翻车是答到一半忘了问题的后半截，而字幕是往上滚的 ——
    /// 你一开口它就滚走了。所以这一条必须在滚动区**之外**。
    @ViewBuilder
    private var pinnedQuestionBar: some View {
        let pinned = store.pinnedQuestion
        if settings.pinQuestion, !pinned.isEmpty {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "questionmark.circle.fill")
                    .font(.system(size: settings.fontSize - 5, weight: .semibold))
                    .foregroundStyle(Color.green.opacity(0.75))
                    .padding(.top, 2)

                VStack(alignment: .leading, spacing: 2) {
                    if settings.displayMode.showsSource {
                        Text(pinned.map(\.source).joined(separator: " "))
                            .font(.system(size: settings.fontSize - 2, weight: .regular))
                            .foregroundStyle(.white.opacity(0.6))
                    }
                    if settings.displayMode.showsTranslation {
                        // 译文可能还没回来，这里不占位：钉住条本来就是额外多出来的一块，
                        // 让它随译文长高一次，比先撑开一行空白更不碍事。
                        let translated = pinned.compactMap(\.translation).joined(separator: " ")
                        if !translated.isEmpty {
                            Text(translated)
                                .font(.system(size: settings.fontSize - 1, weight: .semibold))
                                .foregroundStyle(.white.opacity(0.95))
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 14)
            .padding(.vertical, 7)
            .background(Color.green.opacity(0.1))
            .overlay { WindowDragHandle() }
            .overlay(alignment: .bottom) { Divider().opacity(0.25) }
        }
    }

    // MARK: - 问题备忘录

    /// 本场问过的每一轮问题。只读、不滚动跟随 —— 你是主动翻它的，
    /// 新问题进来时把你正在看的位置顶走会很烦。
    private var memoList: some View {
        ScrollView(.vertical) {
            let memo = store.questionMemo
            VStack(alignment: .leading, spacing: 12) {
                if memo.isEmpty {
                    Text("这一场还没检测到提问")
                        .font(.system(size: settings.fontSize - 2))
                        .foregroundStyle(.white.opacity(0.4))
                } else {
                    ForEach(Array(memo.enumerated()), id: \.offset) { index, group in
                        memoEntry(index: index, group: group)
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
        }
        .scrollIndicators(.visible)
    }

    @ViewBuilder
    private func memoEntry(index: Int, group: [CaptionLine]) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text("\(index + 1)")
                .font(.system(size: settings.fontSize - 4, weight: .bold, design: .rounded))
                .foregroundStyle(Color.green.opacity(0.7))
                .frame(minWidth: 14, alignment: .trailing)
                .padding(.top, 2)

            VStack(alignment: .leading, spacing: 2) {
                if settings.displayMode.showsSource {
                    Text(group.map(\.source).joined(separator: " "))
                        .font(.system(size: settings.fontSize - 2))
                        .foregroundStyle(.white.opacity(0.6))
                        .textSelection(.enabled)
                }
                let translated = group.compactMap(\.translation).joined(separator: " ")
                if settings.displayMode.showsTranslation, !translated.isEmpty {
                    Text(translated)
                        .font(.system(size: settings.fontSize - 1, weight: .medium))
                        .foregroundStyle(.white.opacity(0.95))
                        .textSelection(.enabled)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
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
                .frame(maxWidth: .infinity, minHeight: 80, alignment: .leading)
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                // 字幕区整块可拖。放在 ScrollView **内部**：外面是 NSScrollView
                // 自己，盖不住。minHeight 是为了字幕还很短的时候，下面那片空白
                // 也能拖。
                .overlay { WindowDragHandle() }
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

    /// 字幕行**不可选中**。
    ///
    /// 选中和拖窗是同一个手势，只能二选一。选在拖窗这边，是因为字幕区每来一句
    /// 就自动滚到底 —— 你在这里选中的文字下一秒就被滚走了，这个「选中复制」
    /// 本来就不好用。要抄某个问题去备忘录（📝）里抄，那里是静态的。
    @ViewBuilder
    private func lineView(_ line: CaptionLine) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            if settings.displayMode.showsSource {
                Text(line.source)
                    .font(.system(size: settings.fontSize, weight: .regular))
                    .foregroundStyle(settings.displayMode == .bilingual ? .white.opacity(0.72) : .white)
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
        } else if line.translationFailed {
            Text("（本句翻译失败）")
                .foregroundStyle(.orange.opacity(0.8))
        } else {
            Text("…")
                .foregroundStyle(.white.opacity(0.35))
        }
    }
}
