using System.Security.Cryptography;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace BrainstormBuddy.Memory.Embeddings;

/// <summary>Управление жизненным циклом локальной embedding-модели из UI
/// (спека memory2 §19-§20): статус, ручная выгрузка, smoke-проверка,
/// применение настроек без рестарта (§39).</summary>
public interface ILocalEmbeddingControl
{
    /// <summary>Каталог, из которого грузится модель.</summary>
    string ModelDir { get; }
    LazyModelStatus GetStatus();
    /// <summary>«Выгрузить модель сейчас»: false — не загружена или идёт inference.</summary>
    Task<bool> UnloadNowAsync();
    /// <summary>«Проверить модель»: load + smoke-embed → (ok, latency, dim).</summary>
    Task<(bool Ok, double ElapsedMs, int Dimension)> CheckAsync(CancellationToken ct);
    /// <summary>Применить LocalEmbeddingRuntimeMode/UnloadAfterIdleMinutes из конфига.</summary>
    void ApplyConfig(MemoryConfig cfg);
}

/// <summary>
/// Локальные эмбеддинги intfloat/multilingual-e5-small на ONNX Runtime
/// (спека memory2). Реальная SentencePiece-токенизация (XLM-R BPE,
/// sentencepiece.bpe.model через Microsoft.ML.Tokenizers), префиксы
/// «query: »/«passage: », masked mean pooling по attention_mask, L2-норма —
/// ровно contract из model card (§8).
///
/// Lifecycle — LazyModelLoader: single-flight load, ref-counted lease,
/// idle-таймер для Balanced, немедленная выгрузка после задания для
/// MemorySaver, AlwaysLoaded — до Dispose (§10-§18). Настройки применяются
/// на лету через ApplyConfig (§39).
///
/// Файлы в каталоге модели:
///   model.onnx               — официальный FP32 export (~470 МБ)
///   sentencepiece.bpe.model  — XLM-R SentencePiece-BPE
/// Файлов нет → IsAvailable=false, EmbedAsync=null — FTS продолжает работать
/// (§41). Наружу не бросаем.
///
/// Соответствие ID пространств: HF tokenizer.json сдвигает словарь sp на +1
/// (added_tokens: &lt;s&gt;=0,&lt;pad&gt;=1,&lt;/s&gt;=2,&lt;unk&gt;=3), обёртка
/// [0] … [2] — вручную. Проверено golden-тестом против HF tokenizers (§32).
/// </summary>
public sealed class LocalE5EmbeddingProvider : IMemoryEmbeddingProvider, ILocalEmbeddingControl, IDisposable
{
    private const string LogCat = "Memory";
    private const int MaxSeqLen = 512;          // model card: 512 tokens, с учётом <s>/</s>
    private const int BatchChunkSize = 32;      // верхняя граница батча — держим паддинг умеренным

    /// <summary>ID добавленных токенов в пространстве HF tokenizer.json.</summary>
    internal const int HfBosId = 0;    // <s>
    internal const int HfEosId = 2;    // </s>

    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly string _spPath;
    private readonly LoggingService? _logger;
    private readonly MemoryConfig _cfg;
    private readonly LazyModelLoader<LoadedModel> _loader;
    private readonly object _tagLock = new();
    private string? _tag;
    private string _tagFileSig = "";

    public LocalE5EmbeddingProvider(string modelDir, LoggingService? logger = null,
        MemoryConfig? cfg = null, TimeProvider? clock = null,
        Func<CancellationToken, Task<LoadedModel>>? modelFactory = null)
    {
        _modelDir = modelDir ?? "";
        _logger = logger;
        _cfg = cfg ?? new MemoryConfig();
        _modelPath = Path.Combine(_modelDir, "model.onnx");
        _spPath = Path.Combine(_modelDir, "sentencepiece.bpe.model");

        _loader = new LazyModelLoader<LoadedModel>(
            modelFactory ?? LoadModel,
            "e5-embed",
            ParseMode(_cfg.LocalEmbeddingRuntimeMode),
            IdleTimeout(_cfg.LocalEmbeddingUnloadAfterIdleMinutes),
            logger, clock);
    }

    /// <summary>Файлы модели на месте? Inference запускается лениво.</summary>
    public bool IsAvailable => File.Exists(_modelPath) && File.Exists(_spPath);

    /// <summary>Человеческое имя провайдера (без версии).</summary>
    public string Name => $"local:{(_cfg.LocalEmbeddingModel ?? "multilingual-e5-small").Trim()}";

    /// <summary>
    /// Vector-space тег: local:&lt;model&gt;@&lt;sha256(model.onnx)[:16]&gt;.
    /// Лениво хэшируем файл (~1с на 470МБ) с инвалидацией по (size,mtime):
    /// заменили модель → тег сменился → embed-джоба переиндексирует (§35).
    /// </summary>
    public string EmbeddingTag
    {
        get
        {
            lock (_tagLock)
            {
                var sig = FileSig(_modelPath);
                if (_tag == null || sig != _tagFileSig)
                {
                    _tag = $"{Name}@{Fingerprint(_modelPath)}";
                    _tagFileSig = sig;
                }
                return _tag;
            }
        }
    }

    public string ModelDir => _modelDir;
    public LazyModelStatus GetStatus() => _loader.GetStatus();
    public Task<bool> UnloadNowAsync() => _loader.TryUnloadAsync();
    public void ApplyConfig(MemoryConfig cfg) => _loader.ApplySettings(
        ParseMode(cfg.LocalEmbeddingRuntimeMode),
        IdleTimeout(cfg.LocalEmbeddingUnloadAfterIdleMinutes));

    /// <summary>«Проверить модель»: реальный load + embed + замер (спека §20).</summary>
    public async Task<(bool Ok, double ElapsedMs, int Dimension)> CheckAsync(CancellationToken ct)
    {
        if (!IsAvailable) return (false, 0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var vec = await EmbedAsync("проверка модели", isQuery: true, ct).ConfigureAwait(false);
        sw.Stop();
        return vec == null ? (false, sw.Elapsed.TotalMilliseconds, 0)
                           : (true, sw.Elapsed.TotalMilliseconds, vec.Length);
    }

    public async Task<float[]?> EmbedAsync(string text, bool isQuery, CancellationToken ct)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var lease = await _loader.AcquireAsync(ct).ConfigureAwait(false);
            // ORT синхронный и греет CPU — уводим в пул.
            var result = await Task.Run(
                () => lease.Model.Embed(new[] { text }, isQuery)[0], ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger?.Debug("E5 embed cancelled", LogCat);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"E5 embed failed: {ex.Message}", LogCat);
            return null;
        }
    }

    /// <summary>
    /// Батч-эмбеддинг: один lease (одна «операция» для MemorySaver) на весь
    /// набор + настоящий batched ONNX-прогон чанками по BatchChunkSize —
    /// паддинг до max-длины в чанке, masked pooling по attention_mask.
    /// </summary>
    public async Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string> texts, bool isQuery,
        CancellationToken ct)
    {
        var result = new float[]?[texts.Count];
        if (!IsAvailable || texts.Count == 0) return result;
        try
        {
            using var lease = await _loader.AcquireAsync(ct).ConfigureAwait(false);
            for (int off = 0; off < texts.Count; off += BatchChunkSize)
            {
                var chunkLen = Math.Min(BatchChunkSize, texts.Count - off);
                var chunk = new string[chunkLen];
                for (int i = 0; i < chunkLen; i++) chunk[i] = texts[off + i];
                var vecs = await Task.Run(() => lease.Model.Embed(chunk, isQuery), ct)
                                     .ConfigureAwait(false);
                Array.Copy(vecs, 0, result, off, chunkLen);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger?.Debug("E5 batch embed cancelled", LogCat);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"E5 batch embed failed: {ex.Message}", LogCat);
            return result;
        }
    }

    private async Task<LoadedModel> LoadModel(CancellationToken ct)
    {
        _logger?.Info($"E5: loading {_modelPath}", LogCat);
        // InferenceSession из файла — тяжёлый синхронный шаг (~секунды на 470МБ):
        // уводим в пул, чтобы Acquire не блокировал вызывающий поток.
        return await Task.Run(() =>
        {
            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            var session = new InferenceSession(_modelPath, opts);
            SentencePieceTokenizer tokenizer;
            using (var s = File.OpenRead(_spPath))
            {
                // Врапперы BOS/EOS добавляем вручную в HF-пространстве ID —
                // флаги addBos/addEos эмитят sp-id (1/2), нам нужны 0/2.
                tokenizer = SentencePieceTokenizer.Create(
                    s, addBeginningOfSentence: false, addEndOfSentence: false);
            }
            // Фактическая размерность — из метаданных выхода (model card: 384).
            var dim = 384;
            if (session.OutputMetadata.TryGetValue("last_hidden_state", out var meta) &&
                meta.Dimensions.Length == 3 && meta.Dimensions[2] > 0)
                dim = meta.Dimensions[2];
            return new LoadedModel(session, tokenizer, dim);
        }, ct).ConfigureAwait(false);
    }

    internal static LocalEmbeddingRuntimeMode ParseMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "alwaysloaded" or "always_loaded" or "always" => LocalEmbeddingRuntimeMode.AlwaysLoaded,
            "memorysaver" or "memory_saver" or "saver" => LocalEmbeddingRuntimeMode.MemorySaver,
            _ => LocalEmbeddingRuntimeMode.Balanced,   // дефолт спеки §12
        };

    internal static TimeSpan IdleTimeout(int minutes)
        => TimeSpan.FromMinutes(Math.Clamp(minutes <= 0 ? 10 : minutes, 1, 24 * 60));

    private static string FileSig(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch { return "error"; }
    }

    private static string Fingerprint(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var hash = SHA256.HashData(s);
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
        catch { return "nohash"; }
    }

    public void Dispose() => _loader.Dispose();

    // =================== LoadedModel: tokenizer + ORT session ===================

    /// <summary>
    /// Загруженная связка: tokenizer + InferenceSession. Вся «горячая» работа —
    /// чистый CPU-инференс, потокобезопасна (tokenizer EncodeToIds stateless,
    /// session.Run документированно thread-safe).
    /// </summary>
    public sealed class LoadedModel : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly SentencePieceTokenizer _tokenizer;

        internal LoadedModel(InferenceSession session, SentencePieceTokenizer tokenizer, int dim)
        {
            _session = session;
            _tokenizer = tokenizer;
            Dimension = dim;
        }

        /// <summary>Фактическая размерность эмбеддинга (из метаданных модели).</summary>
        public int Dimension { get; }

        /// <summary>
        /// Tokenize → ids пространства HF: [0]=&lt;s&gt;, sp-ids+1, [2]=&lt;/s&gt;.
        /// SentencePiece pieces 0/1/2 (unk/bos/eos) маппятся в HF 3/0/2.
        /// Обрезка до MaxSeqLen с сохранением &lt;/s&gt; в конце.
        /// Static — golden tokenizer test создаёт sp-токенайзер без ONNX-сессии.
        /// </summary>
        internal static long[] TokenizeIds(SentencePieceTokenizer tokenizer, string text, bool isQuery)
        {
            var prefixed = (isQuery ? "query: " : "passage: ") + text;
            var spIds = tokenizer.EncodeToIds(prefixed);
            int content = Math.Min(spIds.Count, MaxSeqLen - 2);
            var ids = new long[content + 2];
            ids[0] = HfBosId;
            for (int i = 0; i < content; i++)
                ids[i + 1] = MapId(spIds[i]);
            ids[content + 1] = HfEosId;
            return ids;
        }

        internal long[] TokenizeIds(string text, bool isQuery)
            => TokenizeIds(_tokenizer, text, isQuery);

        /// <summary>sp piece id → HF token id (см. шапку класса-провайдера).</summary>
        internal static long MapId(int spId) => spId switch
        {
            0 => 3,   // <unk>
            1 => 0,   // <s>
            2 => 2,   // </s>
            _ => spId + 1,
        };

        /// <summary>Эмбеддинг батча: паддинг → один Run → masked mean → L2.</summary>
        public float[]?[] Embed(IReadOnlyList<string> texts, bool isQuery)
        {
            var allIds = new long[texts.Count][];
            int maxLen = 0;
            for (int i = 0; i < texts.Count; i++)
            {
                allIds[i] = TokenizeIds(texts[i] ?? "", isQuery);
                if (allIds[i].Length > maxLen) maxLen = allIds[i].Length;
            }

            var inputIds = new DenseTensor<long>(new[] { texts.Count, maxLen });
            var attention = new DenseTensor<long>(new[] { texts.Count, maxLen });
            var tokenTypes = new DenseTensor<long>(new[] { texts.Count, maxLen });
            for (int b = 0; b < texts.Count; b++)
            {
                for (int t = 0; t < allIds[b].Length; t++)
                {
                    inputIds[b, t] = allIds[b][t];
                    attention[b, t] = 1;
                }
                for (int t = allIds[b].Length; t < maxLen; t++)
                {
                    inputIds[b, t] = 1;      // <pad>
                    attention[b, t] = 0;
                    tokenTypes[b, t] = 0;
                }
            }

            var inputs = new List<NamedOnnxValue>(3);
            foreach (var (name, meta) in _session.InputMetadata)
            {
                var tensor = name switch
                {
                    "attention_mask" => (Tensor<long>)attention,
                    "token_type_ids" => tokenTypes,
                    _ => inputIds,             // input_ids / прочие id-входы
                };
                inputs.Add(meta.ElementType == typeof(int)
                    ? NamedOnnxValue.CreateFromTensor(name, ToInt32(tensor))
                    : NamedOnnxValue.CreateFromTensor(name, tensor));
            }

            using var results = _session.Run(inputs);
            Tensor<float>? hidden = null;
            foreach (var o in results)
            {
                var t = o.AsTensor<float>();
                if (t is { Rank: 3 } && (hidden == null || o.Name == "last_hidden_state"))
                {
                    hidden = t;
                    if (o.Name == "last_hidden_state") break;
                }
            }
            if (hidden == null) return new float[]?[texts.Count];

            int dim = hidden.Dimensions[2];
            var output = new float[]?[texts.Count];
            for (int b = 0; b < texts.Count; b++)
            {
                // Masked average pooling по attention_mask (§8) — НЕ CLS.
                int len = allIds[b].Length;
                var vec = new float[dim];
                for (int t = 0; t < len && t < hidden.Dimensions[1]; t++)
                    for (int d = 0; d < dim; d++)
                        vec[d] += hidden[b, t, d];
                for (int d = 0; d < dim; d++) vec[d] /= Math.Max(1, len);
                VectorMath.NormalizeInPlace(vec);   // E5-инвариант: L2-норма
                output[b] = vec;
            }
            return output;
        }

        private static DenseTensor<int> ToInt32(Tensor<long> src)
        {
            var dst = new DenseTensor<int>(src.Dimensions.ToArray());
            if (src.Rank == 2)
                for (int b = 0; b < src.Dimensions[0]; b++)
                    for (int t = 0; t < src.Dimensions[1]; t++)
                        dst[b, t] = (int)src[b, t];
            else
                for (int i = 0; i < src.Dimensions[0]; i++)
                    dst[i] = (int)src[i];
            return dst;
        }

        public void Dispose() => _session.Dispose();
    }
}
