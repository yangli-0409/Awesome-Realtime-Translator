namespace AwesomeRealtimeTranslator.Core;

/// <summary>
/// 识别器吐出的事件。
///
/// 语义约定（整条管道依赖它）：
/// <list type="bullet">
/// <item>Volatile 是<b>可被覆盖</b>的尾部文本，随时会被改写，只能显示在原文区末尾，绝不能翻译。</item>
/// <item>Finalized 是<b>不可再改</b>的片段，是断句和翻译的唯一输入。</item>
/// <item>一次 Finalized 之后，之前的 Volatile 立即作废。</item>
/// </list>
/// </summary>
public enum TranscriptEventKind
{
    Volatile,
    Finalized,
    Failed,
}

/// <param name="Kind">事件类型</param>
/// <param name="Text">文本；Failed 时是错误描述</param>
public readonly record struct TranscriptEvent(TranscriptEventKind Kind, string Text)
{
    public static TranscriptEvent Volatile(string text) => new(TranscriptEventKind.Volatile, text);

    public static TranscriptEvent Finalized(string text) => new(TranscriptEventKind.Finalized, text);

    public static TranscriptEvent Failed(string message) => new(TranscriptEventKind.Failed, message);
}
