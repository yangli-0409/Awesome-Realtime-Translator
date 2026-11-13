using System.Buffers.Binary;

namespace AwesomeRealtimeTranslator.Core.Translation;

/// <summary>SentencePiece 词表里的一个片段。</summary>
/// <param name="Piece">片段文本，词首带 ▁</param>
/// <param name="Score">对数概率，Viterbi 分词用</param>
/// <param name="Type">0=NORMAL 1=UNKNOWN 2=CONTROL 3=USER_DEFINED 4=BYTE 5=UNUSED</param>
public readonly record struct SentencePieceEntry(string Piece, float Score, int Type);

/// <summary>
/// <c>.spm</c> 文件的最小 protobuf 解析器。
///
/// 为什么自己解析而不用现成库：<c>Microsoft.ML.Tokenizers</c> 里的
/// <c>SentencePieceTokenizer</c> 没有公开构造入口（只能通过 <c>LlamaTokenizer.Create</c>
/// 间接拿到），而且它按 spm 内部的 id 编号；Marian/OPUS-MT 的 id 来自单独的
/// <c>vocab.json</c>，两套编号对不上，直接用会得到错误的 token id。
///
/// 需要的只是「片段 + 分数」这一张表，protobuf 里就是嵌套两层的
/// length-delimited 字段，解析器不到一百行，而且<b>可以完整单测</b>——
/// 这比引一个用不对的库可靠。
///
/// 对应的 .proto（sentencepiece_model.proto 的子集）：
/// <code>
/// message ModelProto {
///   repeated SentencePiece pieces = 1;
/// }
/// message SentencePiece {
///   optional string piece = 1;
///   optional float  score = 2;
///   optional Type   type  = 3;
/// }
/// </code>
/// </summary>
public static class SentencePieceModel
{
    public static IReadOnlyList<SentencePieceEntry> Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Parse(ReadAll(stream));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static IReadOnlyList<SentencePieceEntry> Parse(ReadOnlySpan<byte> data)
    {
        var pieces = new List<SentencePieceEntry>();
        int offset = 0;

        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out int field, out int wire))
            {
                break;
            }

            if (field == 1 && wire == 2)
            {
                if (!TryReadLengthDelimited(data, ref offset, out ReadOnlySpan<byte> body))
                {
                    break;
                }

                if (TryParsePiece(body, out SentencePieceEntry entry))
                {
                    pieces.Add(entry);
                }
            }
            else if (!TrySkip(data, ref offset, wire))
            {
                break;
            }
        }

        return pieces;
    }

    private static bool TryParsePiece(ReadOnlySpan<byte> body, out SentencePieceEntry entry)
    {
        string? piece = null;
        float score = 0;
        int type = 0;
        int offset = 0;

        while (offset < body.Length)
        {
            if (!TryReadTag(body, ref offset, out int field, out int wire))
            {
                break;
            }

            switch (field, wire)
            {
                case (1, 2):
                    if (!TryReadLengthDelimited(body, ref offset, out ReadOnlySpan<byte> text))
                    {
                        entry = default;
                        return false;
                    }

                    piece = System.Text.Encoding.UTF8.GetString(text);
                    break;

                case (2, 5):
                    if (offset + 4 > body.Length)
                    {
                        entry = default;
                        return false;
                    }

                    score = BinaryPrimitives.ReadSingleLittleEndian(body[offset..(offset + 4)]);
                    offset += 4;
                    break;

                case (3, 0):
                    if (!TryReadVarint(body, ref offset, out ulong raw))
                    {
                        entry = default;
                        return false;
                    }

                    type = (int)raw;
                    break;

                default:
                    if (!TrySkip(body, ref offset, wire))
                    {
                        entry = default;
                        return false;
                    }

                    break;
            }
        }

        if (piece is null)
        {
            entry = default;
            return false;
        }

        entry = new SentencePieceEntry(piece, score, type);
        return true;
    }

    private static bool TryReadTag(ReadOnlySpan<byte> data, ref int offset, out int field, out int wire)
    {
        field = 0;
        wire = 0;
        if (!TryReadVarint(data, ref offset, out ulong tag))
        {
            return false;
        }

        field = (int)(tag >> 3);
        wire = (int)(tag & 0x7);
        return true;
    }

    private static bool TryReadLengthDelimited(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> body)
    {
        body = default;
        if (!TryReadVarint(data, ref offset, out ulong length))
        {
            return false;
        }

        int len = (int)length;
        if (len < 0 || offset + len > data.Length)
        {
            return false;
        }

        body = data.Slice(offset, len);
        offset += len;
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int offset, int wire)
    {
        switch (wire)
        {
            case 0:
                return TryReadVarint(data, ref offset, out _);
            case 1:
                offset += 8;
                return offset <= data.Length;
            case 2:
                return TryReadLengthDelimited(data, ref offset, out _);
            case 5:
                offset += 4;
                return offset <= data.Length;
            default:
                return false;
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;

        while (offset < data.Length)
        {
            byte b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
            if (shift > 63)
            {
                return false;
            }
        }

        return false;
    }
}
