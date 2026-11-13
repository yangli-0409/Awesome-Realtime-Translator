namespace AwesomeRealtimeTranslator.Core;

/// <summary>
/// 原文逐字显现的速率控制。<b>这是纯障眼法</b> —— 字不会来得更早；
/// 解码窗口是模型定的，软件能决定的只是「怎么花掉这段时间」。
///
/// macOS 版的第一版是「每批在固定 400ms 内铺完」，错的：批次间隔约 950ms，
/// 于是实际节奏变成「铺 400ms → 干等 550ms → 下一批」，只是把一次大跳
/// 换成一次小跳加一段停顿。
///
/// 现在这版是<b>速率控制</b>：每帧按「把当前积压在 <see cref="Pace"/> 秒内排空」
/// 算速度，积压多就放得快，积压少就放得慢。稳态下新的一批总在旧的一批放完之前
/// 到达，积压恒大于零，所以字一直在动。
///
/// 把它做成纯函数（不碰计时器、不碰 UI）的唯一理由就是能单测 —— 这段数学
/// 是整个「顺滑感」的来源，不验证不敢上。
/// </summary>
public sealed class RevealPacer
{
    /// <summary>每秒最少放出多少字符。太低会在积压很小时显得停滞。</summary>
    public const double MinRate = 8;

    /// <summary>每秒最多放出多少字符。太高等于没有补偿，又变回「一跳一跳」。</summary>
    public const double MaxRate = 120;

    public const double MinPace = 0.3;
    public const double MaxPace = 1.0;

    private double _progress;

    /// <summary>
    /// 积压排空的<b>时间常数</b>（秒），不是截止期限。来自设置里的「原文显现节奏」。
    ///
    /// 速率取 <c>积压 / Pace</c>，所以进度是指数逼近而不是匀速到达：跑满 Pace 秒
    /// 只排掉约 63%（1 - 1/e），剩下的尾巴靠 <see cref="MinRate"/> 兜底收尾。
    /// 稳态下这不重要——新的一批总在旧的排完之前到达，积压恒大于零；
    /// 只有对方停止说话时，最后一截才会以这个较慢的速度收尾。
    ///
    /// 构造时就夹在 [0.3, 1.0]：上限取 1.0 是因为超过批次间隔（~950ms）之后
    /// 积压只会越积越多，更滞后但不会更顺滑，再往上调没有任何收益。
    /// </summary>
    public double Pace { get; }

    public RevealPacer(double pace) => Pace = Math.Clamp(pace, MinPace, MaxPace);

    /// <summary>当前已显现的字符数。</summary>
    public int Revealed => (int)_progress;

    /// <summary>
    /// 推进一帧。
    /// </summary>
    /// <param name="totalLength">当前完整文本的长度</param>
    /// <param name="deltaSeconds">距离上一帧的时间</param>
    /// <returns>这一帧之后应该显示的字符数</returns>
    public int Advance(int totalLength, double deltaSeconds)
    {
        double total = totalLength;

        // 文本变短了（上一句定稿、尾巴清空，或者模型改写了 volatile 结果）：
        // 进度必须跟着回退，否则 Revealed 会越界。
        if (_progress > total)
        {
            _progress = total;
        }

        if (_progress >= total)
        {
            return Revealed;
        }

        double rate = Math.Clamp((total - _progress) / Pace, MinRate, MaxRate);
        _progress = Math.Min(total, _progress + (rate * deltaSeconds));
        return Revealed;
    }

    /// <summary>
    /// 会话重置时清零。
    ///
    /// 刻意<b>没有</b>「新文本到达时补齐到全文」这种方法：macOS 版第一版在任务
    /// 被取消时做了 <c>revealed = total</c> 的补齐，那正是「每批末尾跳一下」的来源。
    /// 新批到达时进度就该留在原处，新的一帧接着往下放。
    /// </summary>
    public void Reset() => _progress = 0;
}
