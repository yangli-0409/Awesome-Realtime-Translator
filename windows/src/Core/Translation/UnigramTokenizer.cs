using System.Globalization;
using System.Text;

namespace AwesomeRealtimeTranslator.Core.Translation;

/// <summary>
/// SentencePiece Unigram 分词：在片段表上做 Viterbi，找总分最高的一种切法。
///
/// 纯逻辑、无依赖，所以能用一张小词表完整单测——这是不引第三方分词库、
/// 自己实现的主要理由（另一个理由见 <see cref="SentencePieceModel"/>）。
/// </summary>
public sealed class UnigramTokenizer
{
    /// <summary>SentencePiece 用它代表词边界的空格。</summary>
    public const char SpaceMarker = '▁';

    private readonly Dictionary<string, float> _scores;
    private readonly int _maxPieceLength;
    private readonly float _unknownPenalty;

    public UnigramTokenizer(IReadOnlyList<SentencePieceEntry> pieces)
    {
        _scores = new Dictionary<string, float>(pieces.Count, StringComparer.Ordinal);
        float min = 0;

        foreach (SentencePieceEntry entry in pieces)
        {
            // CONTROL（<s> </s> <pad>）不参与分词，否则正文里出现 "</s>" 字样会被当成控制符。
            if (entry.Type == 2)
            {
                continue;
            }

            _scores[entry.Piece] = entry.Score;
            if (entry.Piece.Length > _maxPieceLength)
            {
                _maxPieceLength = entry.Piece.Length;
            }

            if (entry.Score < min)
            {
                min = entry.Score;
            }
        }

        // 未知字符的代价要比任何已知片段都差，但不能是 -∞，
        // 否则整句话会因为一个生僻字而无法切分。
        _unknownPenalty = min - 10f;

        // 片段表里偶有超长条目，限制一下搜索宽度，避免每个位置都试几十次。
        _maxPieceLength = Math.Min(Math.Max(_maxPieceLength, 1), 32);
    }

    /// <summary>
    /// 归一化：NFKC + 压缩连续空白 + 空格换成 ▁ + 句首补一个 ▁。
    /// 和 SentencePiece 默认的 <c>add_dummy_prefix=true</c> 行为一致。
    /// </summary>
    public static string Normalize(string text)
    {
        string nfkc = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length + 1);
        sb.Append(SpaceMarker);

        bool lastWasSpace = true;
        foreach (char c in nfkc)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(SpaceMarker);
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        // 整串都是空白时只剩一个 ▁，没有意义
        return sb.Length == 1 ? string.Empty : sb.ToString();
    }

    /// <summary>把一段文本切成片段。输入会先经过 <see cref="Normalize"/>。</summary>
    public IReadOnlyList<string> Encode(string text)
    {
        string normalized = Normalize(text);
        return normalized.Length == 0 ? [] : Segment(normalized);
    }

    /// <summary>对已归一化的文本做 Viterbi 切分。</summary>
    public IReadOnlyList<string> Segment(string text)
    {
        int n = text.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        best.AsSpan().Fill(float.NegativeInfinity);
        best[0] = 0;

        for (int i = 0; i < n; i++)
        {
            if (float.IsNegativeInfinity(best[i]))
            {
                continue;
            }

            int limit = Math.Min(_maxPieceLength, n - i);
            bool matchedAny = false;

            for (int len = limit; len >= 1; len--)
            {
                string candidate = text.Substring(i, len);
                if (!_scores.TryGetValue(candidate, out float score))
                {
                    continue;
                }

                matchedAny = true;
                float total = best[i] + score;
                if (total > best[i + len])
                {
                    best[i + len] = total;
                    from[i + len] = i;
                }
            }

            // 一个已知片段都匹配不上（生僻字、表情符号）：按单字符走 <unk> 分支，
            // 保证路径不断，整句话仍然能切出来。
            if (!matchedAny)
            {
                int len = char.IsHighSurrogate(text[i]) && i + 1 < n ? 2 : 1;
                float total = best[i] + _unknownPenalty;
                if (total > best[i + len])
                {
                    best[i + len] = total;
                    from[i + len] = i;
                }
            }
        }

        if (float.IsNegativeInfinity(best[n]))
        {
            return [];
        }

        var pieces = new List<string>();
        int pos = n;
        while (pos > 0)
        {
            int start = from[pos];
            pieces.Add(text[start..pos]);
            pos = start;
        }

        pieces.Reverse();
        return pieces;
    }

    /// <summary>把片段拼回文本：▁ 还原成空格，首尾空白去掉。</summary>
    public static string Decode(IEnumerable<string> pieces)
    {
        var sb = new StringBuilder();
        foreach (string piece in pieces)
        {
            sb.Append(piece);
        }

        return sb.Replace(SpaceMarker, ' ').ToString().Trim();
    }
}
