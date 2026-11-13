using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AwesomeRealtimeTranslator.Core.Translation;

/// <summary>
/// 本机翻译，对应 macOS 版的 AppleTranslator。
///
/// <b>这是 Windows 版和 Mac 版差距第二大的地方。</b>macOS 26 自带 Translation 框架，
/// 系统给你一个 <c>TranslationSession</c> 就完事；Windows 没有任何系统翻译 API，
/// 所以只能自己带一个模型跑推理。
///
/// 用的是 OPUS-MT（Helsinki-NLP）的 en→zh，量化成 ONNX 后约 240MB。代价是安装包
/// 从 Mac 版的 0.8MB 涨到几百 MB，换来的是同样的性质：<b>全程本机、不联网</b>。
///
/// 解码策略刻意选了最简单的那种：每一步把<b>完整的已生成序列</b>重新喂给解码器，
/// 不用 KV cache。复杂度是 O(n²)，但一句字幕通常不到 60 个 token，实测开销可接受；
/// 换来的是没有跨步状态、不可能出现 cache 对不齐这类难查的 bug。
/// 贪心而非 beam search 也是同理：字幕要的是稳定和快，不是最后 0.5 个 BLEU。
/// </summary>
public sealed class MarianTranslator : ITranslator
{
    /// <summary>一句译文最多生成多少 token。超了就截断，总比卡住强。</summary>
    private const int MaxNewTokens = 256;

    /// <summary>原文最多喂多少 token，和 Segmenter 的 140 字上限相称。</summary>
    private const int MaxSourceTokens = 192;

    private readonly string _modelDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TermProtector _terms;

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private UnigramTokenizer? _sourceTokenizer;
    private Dictionary<string, int>? _pieceToId;
    private string[]? _idToPiece;
    private int _decoderStartTokenId;
    private int _eosTokenId;
    private int _unknownTokenId;

    /// <param name="modelDirectory">放 vocab.json / source.spm / onnx 的目录</param>
    /// <param name="glossary">
    /// 用户自定义术语，<c>英文=中文</c>。会并进内置术语表，同名时用户的优先。
    /// 对应 macOS 版 AppSettings.glossary——那边喂给识别器做上下文加权，
    /// 这边用来保护翻译，用途不同但来源是同一个设置项。
    /// </param>
    public MarianTranslator(string modelDirectory, IReadOnlyDictionary<string, string>? glossary = null)
    {
        _modelDirectory = modelDirectory;

        var merged = new Dictionary<string, string>(
            TermProtector.DefaultGlossary, StringComparer.OrdinalIgnoreCase);
        if (glossary is not null)
        {
            foreach ((string term, string replacement) in glossary)
            {
                merged[term] = replacement;
            }
        }

        _terms = new TermProtector(merged);
    }

    /// <summary>模型目录里应该有哪些文件。缺一个都跑不起来，提前检查好给出清楚的错误。</summary>
    public static readonly string[] RequiredFiles =
    [
        "vocab.json",
        "source.spm",
        Path.Combine("onnx", "encoder_model_quantized.onnx"),
        Path.Combine("onnx", "decoder_model_quantized.onnx"),
    ];

    public static string? FindMissingFile(string directory)
    {
        foreach (string file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(directory, file)))
            {
                return file;
            }
        }

        return null;
    }

    public Task PrepareAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (FindMissingFile(_modelDirectory) is { } missing)
        {
            throw new TranslatorException(
                $"翻译模型不完整，缺少 {missing}。",
                "正常安装包里自带模型。自己从源码构建的话，运行 tools/fetch-models.ps1 下载。");
        }

        progress?.Report("正在加载翻译模型…");

        return Task.Run(
            () =>
            {
                try
                {
                    LoadVocabulary();

                    var options = new SessionOptions
                    {
                        // 翻译和识别会抢 CPU。识别是实时的、不能等，翻译晚半秒没人看得出来，
                        // 所以这里刻意压低线程数，把核让给 Whisper。
                        InterOpNumThreads = 1,
                        IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4),
                    };

                    _encoder = new InferenceSession(
                        Path.Combine(_modelDirectory, "onnx", "encoder_model_quantized.onnx"), options);
                    _decoder = new InferenceSession(
                        Path.Combine(_modelDirectory, "onnx", "decoder_model_quantized.onnx"), options);
                }
                catch (Exception ex) when (ex is not TranslatorException)
                {
                    throw new TranslatorException($"翻译模型加载失败：{ex.Message}", null, ex);
                }
            },
            cancellationToken);
    }

    private void LoadVocabulary()
    {
        string vocabPath = Path.Combine(_modelDirectory, "vocab.json");
        using FileStream stream = File.OpenRead(vocabPath);
        Dictionary<string, int> vocab =
            JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
            ?? throw new TranslatorException("vocab.json 解析失败。");

        _pieceToId = vocab;

        int max = 0;
        foreach (int id in vocab.Values)
        {
            if (id > max)
            {
                max = id;
            }
        }

        var reverse = new string[max + 1];
        foreach ((string piece, int id) in vocab)
        {
            reverse[id] = piece;
        }

        _idToPiece = reverse;

        // Marian 的约定：解码从 <pad> 开始，</s> 结束。
        _decoderStartTokenId = vocab.GetValueOrDefault("<pad>", 65000);
        _eosTokenId = vocab.GetValueOrDefault("</s>", 0);
        _unknownTokenId = vocab.GetValueOrDefault("<unk>", 1);

        _sourceTokenizer = new UnigramTokenizer(
            SentencePieceModel.Load(Path.Combine(_modelDirectory, "source.spm")));
    }

    public async Task<string> TranslateAsync(
        string text,
        IReadOnlyList<string> context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // OPUS-MT 是逐句无状态的，context 对它没有意义——接口留着是给以后
        // 可能接入的、需要上下文的引擎用的（和 macOS 版同样的处理）。
        _ = context;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => TranslateCore(text, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string TranslateCore(string text, CancellationToken cancellationToken)
    {
        if (_encoder is null || _decoder is null || _sourceTokenizer is null ||
            _pieceToId is null || _idToPiece is null)
        {
            throw new TranslatorException("翻译引擎还没准备好。");
        }

        // 先把术语换成哨兵，翻完再换回来。见 TermProtector 里的实测依据。
        (string protectedText, IReadOnlyDictionary<string, string> restore) = _terms.Protect(text);

        long[] sourceIds = EncodeSource(protectedText);
        if (sourceIds.Length == 0)
        {
            return string.Empty;
        }

        int sourceLength = sourceIds.Length;
        var inputIds = new DenseTensor<long>(sourceIds, [1, sourceLength]);
        var attentionMask = new DenseTensor<long>(new long[sourceLength], [1, sourceLength]);
        for (int i = 0; i < sourceLength; i++)
        {
            attentionMask[0, i] = 1;
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoded = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        ]);

        Tensor<float> hidden = encoded.First(v => v.Name == "last_hidden_state").AsTensor<float>();

        // 编码器输出要在整个解码循环里反复用，而 encoded 在 using 结束时会被释放，
        // 所以先拷一份独立的张量出来。
        var hiddenCopy = new DenseTensor<float>(hidden.ToArray(), hidden.Dimensions.ToArray());

        var generated = new List<long> { _decoderStartTokenId };
        var output = new List<int>();

        for (int step = 0; step < MaxNewTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 显式建数组：集合表达式在这里会挑错重载（被当成 dimensions 而不是 buffer）
            long[] sequence = generated.ToArray();
            var decoderInput = new DenseTensor<long>(sequence, [1, sequence.Length]);

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoded = _decoder.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", decoderInput),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hiddenCopy),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMask),
            ]);

            Tensor<float> logits = decoded.First(v => v.Name == "logits").AsTensor<float>();
            int next = ArgMaxLastPosition(logits);

            if (next == _eosTokenId)
            {
                break;
            }

            generated.Add(next);
            output.Add(next);
        }

        // 顺序要紧：先把哨兵换回中文，再去中文之间的空格。
        // 反过来的话，「来计算 ZQX1 的缩放」还原出来就是「来计算 容器编排 的缩放」，
        // 中文词两边会挂着空格。
        return CollapseCjkSpaces(TermProtector.Restore(DecodeTarget(output), restore));
    }

    private long[] EncodeSource(string text)
    {
        IReadOnlyList<string> pieces = _sourceTokenizer!.Encode(text);
        int count = Math.Min(pieces.Count, MaxSourceTokens);
        var ids = new long[count + 1];

        for (int i = 0; i < count; i++)
        {
            ids[i] = _pieceToId!.TryGetValue(pieces[i], out int id) ? id : _unknownTokenId;
        }

        ids[count] = _eosTokenId;
        return ids;
    }

    private string DecodeTarget(IReadOnlyList<int> ids)
    {
        var pieces = new List<string>(ids.Count);
        foreach (int id in ids)
        {
            if (id < 0 || id >= _idToPiece!.Length)
            {
                continue;
            }

            string? piece = _idToPiece[id];
            if (piece is null || piece is "<pad>" or "</s>" or "<s>")
            {
                continue;
            }

            pieces.Add(piece);
        }

        // 这里<b>不</b>去空格：哨兵还原之后才去，否则还原进来的中文术语
        // 两边会留下空格。去空格统一在 TranslateCore 的最后一步做。
        return UnigramTokenizer.Decode(pieces);
    }

    /// <summary>去掉中日韩字符之间多出来的空格，其它地方的空格保留。</summary>
    internal static string CollapseCjkSpaces(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' && i > 0 && i + 1 < text.Length &&
                IsCjk(text[i - 1]) && IsCjk(text[i + 1]))
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsCjk(char c) =>
        c is >= (char)0x3000 and <= (char)0x9FFF ||
        c is >= (char)0xF900 and <= (char)0xFAFF ||
        c is >= (char)0xFF00 and <= (char)0xFFEF;

    private static int ArgMaxLastPosition(Tensor<float> logits)
    {
        // logits 形状 [batch, sequence, vocab]，只看最后一个位置
        int sequence = logits.Dimensions[1];
        int vocab = logits.Dimensions[2];
        int best = 0;
        float bestValue = float.NegativeInfinity;
        int last = sequence - 1;

        for (int v = 0; v < vocab; v++)
        {
            float value = logits[0, last, v];
            if (value > bestValue)
            {
                bestValue = value;
                best = v;
            }
        }

        return best;
    }

    public ValueTask DisposeAsync()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
        _encoder = null;
        _decoder = null;
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
