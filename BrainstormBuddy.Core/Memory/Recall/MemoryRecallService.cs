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
    // Кандидаты FTS до перескоринга покрытием: OR-запрос по длинному естественному
    // вопросу легко давал 50 строк шума, и тематические факты не доживали до
    // ранкера. 200 строк на локальном индексе — микросекунды, покрытие режет.
    private const int FtsCandidateLimit = 200;
    /// <summary>Кандидаты для режима «Все» (§6.4): 2000 хватает за глаза,
    /// но не бесконечно — защита от патологической БД.</summary>
    private const int AllItemsCandidateLimit = 2000;
    /// <summary>Сколько recall готов ждать embed-запроса, когда модель ещё грузится
    /// (спека §15: FTS не должен ждать E5 load). Модель продолжает греться в
    /// фоне — следующий recall уже получит векторную ветку.</summary>
    private static readonly TimeSpan ColdEmbedWait = TimeSpan.FromMilliseconds(1500);
    // Порог только для смысловой ветки без FTS: E5 на нерелевантной паре
    // даёт 0.75–0.82, реальный перефраз — обычно ≥0.80 при отрыве от медианы.
    private const double SemanticOnlyMinCosine = 0.80;
    // Та же калибровка, что в MeetingSearchService: на реальном E5 отсутствующий
    // запрос даёт отрыв от медианы ~0.039, реальный перефраз — 0.053–0.066.
    // 0.02 пропускал нейтральные факты как «совпадения».
    private const double SemanticMedianMargin = 0.045;
    // Лексический матч слабее половины веса запроса — тема покрыта частично
    // (часть слов вопроса в памяти отсутствует). Держим короткий список
    // смежного вместо 50 случайных фактов; «Все» (RecallMaxItems<=0) не режем.
    private const double WeakLexicalCoverage = 0.5;
    private const int WeakLexicalMaxResults = 8;
    // Потолок явного memory_search: тот же размер, что отдаёт FTS-кандидатов.
    // Схема инструмента разрешает limit ≤ 50, поэтому больше не нужно.
    internal const int ExplicitSearchMaxItems = 50;
    // Когда лексические хиты уже есть, векторный кандидат без лексики должен
    // быть уверенным перефразом: иначе E5-«близость» почти ко всему
    // добавляет в блок чужие факты (проверено на реальной памяти владельца).
    private const double MixedParaphraseCosine = 0.85;
    private const double MixedParaphraseMargin = 0.06;

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

    /// <summary>Запрос требует полного списка («все», «полный перечень»):
    /// App расширяет бюджет и снимает лимит фактов. Делегат к внутреннему
    /// нормализатору запроса, чтобы правило жило в одном месте.</summary>
    public static bool QueryWantsFullList(string? query) => QueryText.WantsFullList(query);

    public async Task<List<MemoryRecallHit>> RecallAsync(
        MemoryRecallQuery q, CancellationToken ct, Action<MemoryRecallTrace>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(q);
        // §6.4 (аудит 2026-10-15): «Все» = RecallMaxItems <= 0 — все ACTIVE факты
        // выбранного scope'а без скрытого лимита 20. Потолок снимаем целиком.
        // Явная просьба «все/полный список» — тот же режим: лимит по числу
        // фактов снят, объём ответа ограничивает только бюджет токенов (блок
        // честно помечается усечённым), иначе просьбу выполнить невозможно.
        var allItems = _config.RecallMaxItems <= 0 || QueryWantsFullList(q.Text);
        // [secret]/[screen] — метка канала, не часть вопроса: иначе маркер
        // попадает в FTS/эмбеддинг и перетягивает служебные факты.
        var query = q with
        {
            Text = QueryText.StripServiceMarkers(q.Text),
            MaxItems = allItems ? int.MaxValue
                                : Math.Clamp(q.MaxItems, 0,
                                    q.ExplicitSearch ? ExplicitSearchMaxItems : _config.RecallMaxItems),
            MaxTokens = Math.Clamp(q.MaxTokens, 0, MaxContextBudgetTokens),
        };
        if (query.MaxItems == 0) return new List<MemoryRecallHit>();

        // Пустой текст: FTS/MATCH пропускаем (пустой MATCH вернул бы шум по всему
        // индексу); ниже — fallback на свежие Active-факты, чтобы PreCallBrief/Ask
        // Memory с пустым запросом не приходили с нулём.
        var fts = string.IsNullOrWhiteSpace(query.Text)
            ? new List<(MemoryItem Item, double FtsScore)>()
            : _store.SearchFts(query.Text, allItems ? AllItemsCandidateLimit : FtsCandidateLimit)
                    .Where(x => IsVisibleForScope(query, x.Item)).ToList();
        List<(MemoryItem Item, double VecScore)>? vec = null;

        var embeddingsFailed = false;

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
                embeddingsFailed = true;
                _log?.Warn($"Memory recall: embedding path failed, FTS-only fallback ({ex.Message})",
                    LogCategory);
            }
        }

        var ftsCandidates = fts.Count;
        var vectorCandidates = vec?.Count ?? 0;
        var embeddingsAvailable = _embeddings?.IsAvailable == true;

        // FTS не нашёл ничего, а E5 всегда возвращает «ближайшее»: без
        // абсолютного/относительного порога отсутствующий вопрос («квантовый
        // флюгегехаймен») тащит случайные факты с cos 0.75–0.82. Если же
        // лексический матч есть, его дополняем векторами без отсечения.
        if (fts.Count == 0 && vec is { Count: > 0 } && !string.IsNullOrWhiteSpace(query.Text))
        {
            var scores = vec.Select(x => x.VecScore).OrderBy(v => v).ToList();
            var mid = scores.Count / 2;
            var median = scores.Count == 0 ? 0.0
                : scores.Count % 2 == 0
                    ? (scores[mid - 1] + scores[mid]) / 2.0
                    : scores[mid];
            var useMedian = scores.Count >= 3;
            vec = vec.Where(x => x.VecScore >= SemanticOnlyMinCosine
                && (!useMedian || x.VecScore - median >= SemanticMedianMargin)).ToList();
        }

        // Смешанный случай: лексические кандидаты есть, но E5 «близок почти ко
        // всему» (медиана нерелевантных пар ~0.75), поэтому векторная ветка
        // добавляла десятки случайных фактов к любому вопросу. Векторный факт
        // остаётся, только если он связан лексически (Fts>0) или это уверенный
        // перефраз: абсолютный порог и отрыв от медианы. Итог: вопрос о теме,
        // которой в памяти нет, возвращает несколько слабых лексических фактов,
        // а не 50 случайных свежих.
        if (vec is { Count: > 0 } && fts.Count > 0 && !string.IsNullOrWhiteSpace(query.Text))
        {
            var ftsIds = fts.Select(x => x.Item.Id).ToHashSet();
            var all = vec.Select(x => x.VecScore).OrderBy(v => v).ToList();
            var mid = all.Count / 2;
            var median = all.Count % 2 == 0
                ? (all[mid - 1] + all[mid]) / 2.0
                : all[mid];
            var useMedian = all.Count >= 3;
            var kept = vec.Where(x => ftsIds.Contains(x.Item.Id) ||
                (x.VecScore >= MixedParaphraseCosine
                 && (!useMedian || x.VecScore - median >= MixedParaphraseMargin)))
                .ToList();
            if (kept.Count != vec.Count)
                _log?.Debug($"Memory recall: vec noise filtered {vec.Count} → {kept.Count}" +
                            $" (median {median:F3}, lexical {ftsIds.Count})", LogCategory);
            vec = kept;
        }

        if (string.IsNullOrWhiteSpace(query.Text) && vec is not { Count: > 0 })
            fts = BlankFallbackItems(query, allItems)
                    .Select(it => (Item: it, FtsScore: 0.0)).ToList();

        // Слабое покрытие темы: вопрос содержит слова, которых в памяти нет
        // («мобильное приложение»), поэтому «отвечает» давало 50 правдоподобных
        // фактов. Оставляем короткий список смежного — модель обязана сказать
        // «точных данных нет», а не выдавать соседние факты за ответ.
        if (!allItems && fts.Count > WeakLexicalMaxResults)
        {
            var bestFts = fts.Max(x => x.FtsScore);
            if (bestFts < WeakLexicalCoverage)
            {
                fts = fts.OrderByDescending(x => x.FtsScore)
                         .ThenBy(x => x.Item.Id)
                         .Take(WeakLexicalMaxResults)
                         .ToList();
                _log?.Debug($"Memory recall: weak coverage {bestFts:F2} — " +
                            $"capped to {fts.Count} candidates", LogCategory);
            }
        }

        var hits = _ranker.Rank(query, fts, vec ?? new List<(MemoryItem, double)>());
        _log?.Debug($"Memory recall: '{query.Text}' → {hits.Count} hits " +
                    $"(fts={fts.Count}, vec={vec?.Count ?? 0})", LogCategory);
        trace?.Invoke(new MemoryRecallTrace(ftsCandidates, vectorCandidates, hits.Count,
            embeddingsAvailable, embeddingsFailed));
        return hits;
    }

    /// <summary>
    /// Кандидаты для blank-запроса: Global всегда + конкретный scope из запроса
    /// (scope без ScopeId, кроме Global, ничего не добавляет — «любой проект»
    /// в fallback не нужен, чужие факты не показываем). Дедуп по Id на случай
    /// scope=Global.
    /// </summary>
    private List<MemoryItem> BlankFallbackItems(MemoryRecallQuery query, bool allItems = false)
    {
        // «Все» (§6.4): MaxItems=int.MaxValue — ListItems отдаёт все Active-факты
        // scope'а; limit в SQL трактует <=0/MAX как «без ограничения».
        var limit = allItems ? int.MaxValue : Math.Max(query.MaxItems, FtsCandidateLimit);
        var result = new List<MemoryItem>(Math.Min(limit, 4096));
        var seen = new HashSet<long>();
        void Collect(MemoryScopeType scope, long? scopeId)
        {
            foreach (var it in _store.ListItems(scope, scopeId, MemoryItemStatus.Active,
                                               allItems ? 0 : limit))
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
        => ContextBlockBuilder.Build(hits, Math.Clamp(maxTokens, 0, MaxContextBudgetTokens), _scopeName);

    /// <summary>Верхняя граница блока: обычный recall-лимит ИЛИ полный бюджет
    /// автоматического ответа + дополнительного поиска (настройки «Бюджет
    /// памяти на автоответ»/«Бюджет дополнительного поиска»), если он больше —
    /// режим «покажи все» обязан получать больше 2000 токенов.</summary>
    private int MaxContextBudgetTokens =>
        Math.Max(_config.RecallMaxTokens, _config.AutoRecallBudgetTokens + _config.ExtraSearchBudgetTokens);
}
