import SwiftUI

/// 把批量到达的文本匀速放出来，填平批次之间的空档。
///
/// 为什么需要它：`SpeechAnalyzer` 开了 `.fastResults` 之后仍然是**批量**吐结果 ——
/// 实测每 ~950ms 吐一批，一批 2~5 个词，批内十几条结果的时间戳在同一毫秒。
/// 直接渲染就是「每秒跳一次」。
///
/// 第一版用「400ms 内铺完一批」，结果变成「铺完 → 干等 550ms → 下一批」，
/// 节奏感还在，只是从一次大跳变成一次小跳加一段停顿。
///
/// 这一版改成**速率控制**：每帧按「把当前积压在 `drainHorizon` 秒内排空」算速度，
/// 积压多就放得快，积压少就放得慢。稳态下积压恒大于零，所以字一直在动，没有死等。
/// 代价是显示比识别结果稳定落后约 `drainHorizon` 秒 —— 用延迟换连续，
/// 这是这个问题唯一的交易方式：解码窗口是模型定的，软件只能决定怎么花掉这段时间。
///
/// 纯视觉补偿，只用在**未定稿的原文尾巴**上。已定稿的句子和译文必须立刻、
/// 完整地出现 —— 让人等动画去看一句已经确定的话，是拿体验换花哨。
struct StreamingText: View {
    let text: String
    /// 积压排空的时间常数（秒）。来自设置里的「原文显现节奏」，合理区间 0.3~1.0。
    /// 调大更顺滑也更滞后，调小更跟手但会露出批次之间的停顿。
    let pace: Double
    private static let frame: Double = 1.0 / 60.0
    /// 速度下限保证收尾（否则指数衰减永远差最后几个字），上限防止一次大修正时暴走
    private static let minRate: Double = 8
    private static let maxRate: Double = 120

    /// 小数形式的进度。整数化之后才是真正显示的字符数。
    @State private var progress: Double = 0
    @State private var revealed = 0

    var body: some View {
        // 渲染的是 text 的前缀，所以模型回头改了前面的词也会自动跟上
        Text(String(text.prefix(revealed)))
            .textSelection(.enabled)
            .task(id: text) { await pace() }
    }

    private func pace() async {
        let total = Double(text.count)
        // 文本被换掉或清空（句子定稿）时先夹住，避免 prefix 越界或残留上一句的长度
        if progress > total { progress = total }
        revealed = Int(progress)

        while progress < total {
            let horizon = min(1.0, max(0.3, pace))
            let rate = min(Self.maxRate, max(Self.minRate, (total - progress) / horizon))
            progress = min(total, progress + rate * Self.frame)
            revealed = Int(progress)
            do {
                try await Task.sleep(for: .milliseconds(16))
            } catch {
                // 新的一批到了，这个任务被取消。**不要**在这里补齐到全文：
                // 那会让每批结尾跳一下，正是这一版要消除的东西。
                // 进度留在原处，新任务接着往下放。
                return
            }
        }
    }
}
