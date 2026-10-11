using BrainstormBuddy.Config;
using BrainstormBuddy.Memory.Embeddings;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Recall;

/// <summary>
/// Hybrid recall для assistant/Ask Memory/Pre-call Brief (спека «MEMORY RETRIEVAL»,
/// «AUTO RECALL», «RECALL LATENCY»). Путь: SearchFts → (при доступных эмбеддингах
/// и AutoRecall) EmbedAsync(query, isQuery:true) + cosine по LoadEmbeddings →
/// IMemoryRanker. Деградация: провайдер недоступен/упал/вернул null → FTS-only.
/// MaxItems и MaxTokens клампятся лимитами из MemoryConfig.
/// </summary>
public sealed class MemoryRecallService : IMemoryRecallService
{
    private const string LogCategory = "Memory";
    private const int FtsCandidateLimit = 50;
    /// <summary>Сколько recall готов ждать embed-запроса, когда модель ещё грузится
    /// (спека §15: FTS не должен ждать E5 load). Модель продолжает греться в
    /// фоне — следующий recall уже получит векторную ветку.</summary>
    private static readonly TimeSpan ColdEmbedWait = TimeSpan.FromMilliseconds(1500);

    private readonly IMemoryStore _store;
    private readonly IMemoryEmbeddingProvider? _embeddings;
    private readonly MemoryConfig _config;
    private readonly IMemoryRanker _ranker;
    private readonly LoggingService? _log;
    private readonly ContextBlockBuilder.ScopeNameResolver? _scopeName;
    // Кэш векторной ветки: items+embeddings перечитываются только при смене
    // store.DataRevision (LoadEmbeddings ~170мс на 50k — без кэша съедал почти
    // весь live-бюджет 300мс). RAM: ~74МБ на 50k эмбеддингов — держим осознанно.
    private readonly object _cacheLock = new();
    private long _cachedRevision = -1;
    private string? _cachedTag;
    private Dictionary<long, MemoryItem>? _cachedItems;
    private List<(long ItemId, float[] Vec)>? _cachedVecs;

    public MemoryRecallService(IMemoryStore store, IMemoryEmbeddingProvider? embeddings,
        MemoryConfig config, LoggingService? log = null, IMemoryRanker? ranker = null,
        ContextBlockBuilder.ScopeNameResolver? scopeName = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddings = embeddings;
        _config = config ?? new MemoryConfig();
        _ranker = ranker ?? new MemoryRanker();
        _log = log;
        _scopeName = scopeName;
    }

    public async Task<List<MemoryRecallHit>> RecallAsync(MemoryRecallQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        var query = q with
        {
            MaxItems = Math.Clamp(q.MaxItems, 0, _config.RecallMaxItems),
            MaxTokens = Math.Clamp(q.MaxTokens, 0, _config.RecallMaxTokens),
        };
        if (query.MaxItems == 0) return new List<MemoryRecallHit>();

        // Пустой текст: FTS/MATCH пропускаем (пустой MATCH вернул бы шум по всему
        // индексу); ниже — fallback на свежие Active-факты, чтобы PreCallBrief/Ask
        // Memory с пустым запросом не приходили с нулём.
        var fts = string.IsNullOrWhiteSpace(query.Text)
            ? new List<(MemoryItem Item, double FtsScore)>()
            : _store.SearchFts(query.Text, FtsCandidateLimit)
                    .Where(x => IsVisibleForScope(query, x.Item)).ToList();
        List<(MemoryItem Item, double VecScore)>? vec = null;

        // Векторная ветка — только когда auto-recall включён и провайдер реально доступен.
        // Remote embed может быть медленным: ct наружу, любой сбой → мягкий FTS-fallback.
        if (_config.AutoRecall && _embeddings?.IsAvailable == true)
        {
            try
            {
                var embedTask = _embeddings.EmbedAsync(query.Text, isQuery: true, ct);
                // Холодная модель грузится секунды: ждём ограниченное окно
                // (§15 — FTS не блокируем), модель догревается в фоне и
                // следующий recall уже получит векторную ветку.
                var qv = await embedTask
                              .WaitAsync(ColdEmbedWait, CancellationToken.None)
                              .ConfigureAwait(false);
                if (qv is { Length: > 0 })
                {
                    var (byId, vecs) = GetVectorIndex();
                    vec = new List<(MemoryItem, double)>();
                    foreach (var (itemId, v) in vecs)
                    {
                        if (!byId.TryGetValue(itemId, out var item)) continue;
                        if (!IsVisibleForScope(query, item)) continue;
                        var cos = VectorMath.CosineSimilarity(qv, v);
                        if (cos > 0) vec.Add((item, cos));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TimeoutException)
            {
                _log?.Info("Memory recall: embedding not ready in cold window — " +
                           "FTS-only this call, model keeps warming", LogCategory);
            }
            catch (Exception ex)
            {
                _log?.Warn($"Memory recall: embedding path failed, FTS-only fallback ({ex.Message})",
                    LogCategory);
            }
        }

        // Blank-запрос без векторного сигнала → топ свежих Active по recency/importance
        // (всплывают в ранкере: FTS=0, vec=0, score от recency/importance/pinned).
        // Scope-фильтр обязателен: ListItems(status) без scope выдал бы Active-факты
        // чужих проектов/персон — утёк бы чужой контекст в PreCallBrief/Ask Memory.
        if (string.IsNullOrWhiteSpace(query.Text) && vec is not { Count: > 0 })
            fts = BlankFallbackItems(query)
                    .Select(it => (Item: it, FtsScore: 0.0)).ToList();

        var hits = _ranker.Rank(query, fts, vec ?? new List<(MemoryItem, double)>());
        _log?.Debug($"Memory recall: '{query.Text}' → {hits.Count} hits " +
                    $"(fts={fts.Count}, vec={vec?.Count ?? 0})", LogCategory);
        return hits;
    }

    /// <summary>
    /// Кандидаты для blank-запроса: Global всегда + конкретный scope из запроса
    /// (scope без ScopeId, кроме Global, ничего не добавляет — «любой проект»
    /// в fallback не нужен, чужие факты не показываем). Дедуп по Id на случай
    /// scope=Global.
    /// </summary>
    private List<MemoryItem> BlankFallbackItems(MemoryRecallQuery query)
    {
        var limit = Math.Max(query.MaxItems, FtsCandidateLimit);
        var result = new List<MemoryItem>(limit);
        var seen = new HashSet<long>();
        void Collect(MemoryScopeType scope, long? scopeId)
        {
            foreach (var it in _store.ListItems(scope, scopeId, MemoryItemStatus.Active, limit))
                if (seen.Add(it.Id)) result.Add(it);
        }
        Collect(MemoryScopeType.Global, null);
        if (query.Scope is { } scope && scope != MemoryScopeType.Global && query.ScopeId is { } sid)
            Collect(scope, sid);
        return result;
    }

    /// <summary>
    /// Жёсткая изоляция scope (спека §30): чужой проект/персона/встреча
    /// недостижимы — это фильтр кандидатов, а не мягкий бонус ранкера
    /// (раньше vec-ветка ранжировала чужие факты по косинусу и те всплывали
    /// поверх правильного проекта). Global виден всегда; Scope=null в запросе —
    /// без ограничений. Зеркалит ветку ScopeScore==0.0 в MemoryRanker.
    /// </summary>
    private static bool IsVisibleForScope(MemoryRecallQuery q, MemoryItem it)
    {
        if (it.ScopeType == MemoryScopeType.Global || q.Scope is null) return true;
        return it.ScopeType == q.Scope.Value
            && (q.ScopeId is null || it.ScopeId == q.ScopeId.Value);
    }

    /// <summary>
    /// items(limit 2000) + embeddings из стора, закэшированные по DataRevision.
    /// Любая мутация item/embedding бампит ревизию → следующий recall перечитает.
    /// </summary>
    private (Dictionary<long, MemoryItem> ById, List<(long ItemId, float[] Vec)> Vecs) GetVectorIndex()
    {
        lock (_cacheLock)
        {
            // Фильтр по vector-space провайдера (спека §35): эмбеддинги от другой
            // модели/провайдера не смешиваем — иначе скоры шумят в другом базисе.
            // Смена тега инвалидирует кэш даже без бампа DataRevision.
            var tag = _embeddings?.EmbeddingTag;
            if (_cachedItems == null || _cachedVecs == null ||
                _cachedRevision != _store.DataRevision || _cachedTag != tag)
            {
                // Ревизию берём ДО чтений: запись посреди загрузки даст лишний
                // rebuild (безопасно), а не «новая ревизия на старых данных».
                var rev = _store.DataRevision;
                _cachedItems = _store.ListItems(limit: 2000).ToDictionary(i => i.Id);
                _cachedVecs = _store.LoadEmbeddings()
                                    .Where(e => string.IsNullOrEmpty(tag) || e.Model == tag)
                                    .Select(e => (e.ItemId, e.Vec))
                                    .ToList();
                _cachedRevision = rev;
                _cachedTag = tag;
                _log?.Debug($"Memory recall: vector index rebuilt, {_cachedVecs.Count} embeddings " +
                            $"(rev {_cachedRevision}, tag={tag ?? "-"})", LogCategory);
            }
            return (_cachedItems, _cachedVecs);
        }
    }

    public string BuildContextBlock(List<MemoryRecallHit> hits, int maxTokens)
        => ContextBlockBuilder.Build(hits, Math.Clamp(maxTokens, 0, _config.RecallMaxTokens), _scopeName);
}
