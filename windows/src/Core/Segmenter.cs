using System.Text;

namespace AwesomeRealtimeTranslator.Core;

/// <summary>
/// 断句器：把识别器的 volatile/final 事件流切成不可变的句子。
///
/// 纯逻辑、无依赖、无时钟 —— 时间一律由调用方传入，所以可以完整单测（见 SegmenterTests）。
///
/// 三条定稿规则：
/// <list type="number">
/// <item>句末标点</item>
/// <item>静音超过 <see cref="Configuration.SilenceFlushInterval"/></item>
/// <item>单句超过 <see cref="Configuration.MaxSentenceLength"/></item>
/// </list>
///
/// 这是 macOS 版 Segmenter.swift 的逐条移植，规则、阈值、边界情况全部一致，
/// 单测用例也一一对应。两边任何一侧改了规则，另一侧必须同步。
/// </summary>
public sealed class Segmenter
{
    public sealed class Configuration
    {
        /// <summary>识别器停止吐字多久之后，把缓冲区当成一句话定稿</summary>
        public double SilenceFlushInterval { get; set; } = 0.7;

        /// <summary>单句字符上限，超过就强制切（防止面试官一口气说 3 分钟导致整段不出译文）</summary>
        public int MaxSentenceLength { get; set; } = 140;

        /// <summary>短于这个长度的残片不单独成句，并入下一句</summary>
        public int MinSentenceLength { get; set; } = 2;

        public HashSet<char> Terminators { get; set; } =
            ['.', '!', '?', '。', '！', '？', '…', ';', '；'];

        /// <summary>这些缩写后面的句点不算句末</summary>
        public HashSet<string> Abbreviations { get; set; } = new(StringComparer.Ordinal)
        {
            "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st",
            "e.g", "i.e", "etc", "vs", "approx", "inc", "ltd", "corp",
            "fig", "dept", "univ", "ph.d",
            // 刻意不收 "no" / "co"：口语里 "No." "Co." 远不如 "no." 结句常见，
            // 收了会让 "He said no. Then..." 永远切不开。
        };
    }

    private readonly Configuration _config;

    /// <summary>已定稿但还没凑成一句的文本</summary>
    private string _buffer = string.Empty;

    /// <summary>当前这句话的起始时间</summary>
    private double? _bufferStartedAt;

    /// <summary>最后一次收到任何事件的时间，用于静音判定</summary>
    private double _lastActivityAt;

    public Segmenter(Configuration? configuration = null) => _config = configuration ?? new Configuration();

    /// <summary>当前未定稿尾巴，仅供原文区显示</summary>
    public string VolatileTail { get; private set; } = string.Empty;

    /// <summary>
    /// 原文区应该显示的「当前这句」：已定稿的头 + 未定稿的尾。
    ///
    /// 尾巴会<b>砍掉最后一个没说完的词</b>。理由：模型的 volatile 结果是逐字追加的，
    /// 实测会出现 <c>I am also c</c> → <c>I am also curious</c> 这种中间态，屏幕上就是
    /// 一个被截断的词闪一下。只有最末一个 token 可能不完整（前面的词模型已经看全了），
    /// 砍掉它就能根治，代价是原文比识别结果晚一个词（约 300ms）。
    /// </summary>
    public string PendingText => Join(_buffer, StableTail(VolatileTail));

    public bool HasPendingContent =>
        _buffer.Trim().Length > 0 || VolatileTail.Trim().Length > 0;

    /// <summary>
    /// 去掉未完成的尾词。文本以空白或标点结尾时说明最后一个词已完整，原样返回。
    /// </summary>
    public static string StableTail(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        char last = text[^1];

        // 中日韩没有词边界，逐字出字本身就是正常阅读单位，不做裁剪
        if (char.IsWhiteSpace(last) || IsCjk(last) || TailSafePunctuation.Contains(last))
        {
            return text;
        }

        int idx = text.LastIndexOfAny([' ', '\n']);
        return idx < 0 ? string.Empty : text[..idx];
    }

    private const string TailSafePunctuation = "，。！？、；：,.!?;:'\")）》」-";

    // MARK: - 输入

    /// <summary>喂一个识别事件，返回这一步定稿出来的句子（可能为空）。</summary>
    public IReadOnlyList<Sentence> Ingest(TranscriptEvent evt, double time)
    {
        _lastActivityAt = time;

        switch (evt.Kind)
        {
            case TranscriptEventKind.Volatile:
                VolatileTail = evt.Text;
                if (_bufferStartedAt is null && evt.Text.Trim().Length > 0)
                {
                    _bufferStartedAt = time;
                }

                return [];

            case TranscriptEventKind.Finalized:
                // 一旦有定稿，之前的 volatile 作废（它的内容已包含在定稿里）
                VolatileTail = string.Empty;
                if (evt.Text.Trim().Length == 0)
                {
                    return [];
                }

                _bufferStartedAt ??= time;
                _buffer = Join(_buffer, evt.Text);
                return Cut(time);

            default:
                return [];
        }
    }

    /// <summary>定时调用（建议 100ms 一次）做静音判定。</summary>
    public IReadOnlyList<Sentence> Tick(double time)
    {
        if (_buffer.Trim().Length == 0)
        {
            return [];
        }

        return time - _lastActivityAt >= _config.SilenceFlushInterval ? Flush(time) : [];
    }

    /// <summary>会话结束时冲刷剩余内容。</summary>
    public IReadOnlyList<Sentence> Flush(double time)
    {
        string text = _buffer.Trim();
        _buffer = string.Empty;
        VolatileTail = string.Empty;
        double start = _bufferStartedAt ?? time;
        _bufferStartedAt = null;
        return text.Length == 0 ? [] : [new Sentence(text, start, time)];
    }

    // MARK: - 切句

    private List<Sentence> Cut(double time)
    {
        var output = new List<Sentence>();

        while (true)
        {
            int idx = SentenceEnd(_buffer);
            if (idx >= 0)
            {
                string head = _buffer[..idx].Trim();
                string rest = _buffer[idx..];

                // 太短的残片（"Yes." "OK."）并入下一句，除非剩余为空
                if (head.Length < _config.MinSentenceLength && rest.Trim().Length > 0)
                {
                    break;
                }

                _buffer = DropLeadingSpace(rest);
                if (head.Length > 0)
                {
                    output.Add(new Sentence(head, _bufferStartedAt ?? time, time));
                    _bufferStartedAt = _buffer.Trim().Length == 0 ? null : time;
                }

                if (_buffer.Trim().Length == 0)
                {
                    _buffer = string.Empty;
                    break;
                }

                continue;
            }

            // 没有句末标点，但太长了：在最后一个词边界硬切
            if (_buffer.Length > _config.MaxSentenceLength)
            {
                int cutIdx = HardCutIndex(_buffer, _config.MaxSentenceLength);
                string head = _buffer[..cutIdx].Trim();
                _buffer = DropLeadingSpace(_buffer[cutIdx..]);
                if (head.Length > 0)
                {
                    output.Add(new Sentence(head, _bufferStartedAt ?? time, time));
                    _bufferStartedAt = _buffer.Trim().Length == 0 ? null : time;
                }

                continue;
            }

            break;
        }

        return output;
    }

    /// <summary>返回第一个句末标点之后的位置；没有则 -1。</summary>
    private int SentenceEnd(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!_config.Terminators.Contains(text[i]))
            {
                continue;
            }

            if (!IsRealSentenceEnd(text, i))
            {
                continue;
            }

            // 把紧跟的引号/括号一并归到前一句
            int end = i + 1;
            while (end < text.Length && ClosingMarks.Contains(text[end]))
            {
                end++;
            }

            return end;
        }

        return -1;
    }

    private const string ClosingMarks = "\"'”’)）》」";

    private bool IsRealSentenceEnd(string text, int i)
    {
        if (text[i] != '.')
        {
            return true; // ? ! 。等不会出现在缩写/小数里
        }

        int next = i + 1;

        // 小数点：3.5
        if (next < text.Length && char.IsDigit(text[next]) &&
            i > 0 && char.IsDigit(text[i - 1]))
        {
            return false;
        }

        // 省略号 "..."：前面的点一律不算，最后一个点要看后面接的是不是新句子
        if (next < text.Length && text[next] == '.')
        {
            return false;
        }

        if (i > 0 && text[i - 1] == '.')
        {
            // "Well... it depends" 是一句；"Well... Then he left." 是两句
            int j = next;
            while (j < text.Length && (text[j] == ' ' || text[j] == '\u00A0'))
            {
                j++;
            }

            return j >= text.Length || char.IsUpper(text[j]);
        }

        // 缩写：取句点前的最后一个「词」
        string head = text[..i];
        string word = LastWord(head);
        string normalized = word.ToLowerInvariant().Trim('(', '（', '"', '\'');
        if (_config.Abbreviations.Contains(normalized))
        {
            return false;
        }

        // 单个大写字母 + 点，典型的人名缩写 "J. Smith"
        if (normalized.Length == 1 && word.Length > 0 && char.IsUpper(word[0]))
        {
            return false;
        }

        return true;
    }

    private static string LastWord(string head)
    {
        int end = head.Length;
        int start = end;
        while (start > 0 && head[start - 1] != ' ' && head[start - 1] != '\n')
        {
            start--;
        }

        return head[start..end];
    }

    private static int HardCutIndex(string text, int limit)
    {
        int hardLimit = Math.Min(limit, text.Length);

        // 往前找最近的空白，避免把单词劈开
        int floor = Math.Min(limit / 2, text.Length);
        int i = hardLimit;
        while (i > floor)
        {
            char prev = text[i - 1];
            if (prev is ' ' or ',' or '，')
            {
                return i;
            }

            i--;
        }

        return hardLimit;
    }

    private static string DropLeadingSpace(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return text[i..];
    }

    internal static string Join(string left, string right)
    {
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0)
        {
            return left;
        }

        bool needsSpace = !char.IsWhiteSpace(left[^1])
            && !char.IsWhiteSpace(right[0])
            && !NoSpaceBefore.Contains(right[0]);

        // 中日韩之间不加空格
        bool leftCjk = IsCjk(left[^1]);
        bool rightCjk = IsCjk(right[0]);

        if (!needsSpace || (leftCjk && rightCjk))
        {
            return left + right;
        }

        return string.Concat(left, " ", right);
    }

    private const string NoSpaceBefore = "，。！？、；：,.!?;:";

    private static bool IsCjk(char c) =>
        c is >= (char)0x3000 and <= (char)0x9FFF ||
        c is >= (char)0xF900 and <= (char)0xFAFF;
}
