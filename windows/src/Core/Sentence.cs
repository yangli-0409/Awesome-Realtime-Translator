namespace AwesomeRealtimeTranslator.Core;

/// <summary>
/// 一条已定稿的句子。定稿后文本永不改变 —— 这是译文不闪烁的前提。
/// </summary>
/// <param name="Id">句子标识，翻译回填时用它定位</param>
/// <param name="Text">定稿文本</param>
/// <param name="StartedAt">相对会话开始的秒数</param>
/// <param name="EndedAt">相对会话开始的秒数</param>
public readonly record struct Sentence(Guid Id, string Text, double StartedAt, double EndedAt)
{
    public Sentence(string text, double startedAt, double endedAt)
        : this(Guid.NewGuid(), text, startedAt, endedAt)
    {
    }
}
