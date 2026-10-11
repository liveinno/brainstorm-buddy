using System.Text;
using System.Text.Json;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Дедупликация и supersession кандидатов из extraction против уже хранящихся
/// Active items. Порядок: exact-дедуп (normalize+scope+type) → embedding-дедуп
/// (cos ≥ DedupeSimilarity) → LLM-merge (только при большой пачке и наличии LLM)
/// → остальное Insert. Жёсткое правило: Superseded-экземпляры никогда не
/// воскресают — ни recall, ни apply не возвращают их в Active.
/// </summary>
public sealed class MemoryConsolidator : IMemoryConsolidator
{
    /// <summary>Пачка кандидатов крупнее этого порога включает LLM-шаг поиска дублей.</summary>
    internal const int LlmMergeThreshold = 50;
    private const int LlmBatchSize = 25;         // кандидатов в одном merge-промпте
    private const int LlmExistingContext = 200;  // сколько Active показывать LLM как контекст
    private const string LogCategory = "Memory";

    private readonly IApiClient? _llm;
    private readonly IMemoryEmbeddingProvider? _emb;
    private readonly MemoryConfig _cfg;
    private readonly LoggingService? _log;

    public MemoryConsolidator(IApiClient? llm, IMemoryEmbeddingProvider? emb,
        MemoryConfig cfg, LoggingService? log)
    {
        _llm = llm;
        _emb = emb;
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _log = log;
    }

    public async Task<ConsolidationPlan> PlanAsync(List<MemoryItem> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        var insert = new List<MemoryItem>();
        var supersede = new List<(long ExistingId, MemoryItem NewItem)>();
        var confirmIds = new List<long>();
        var confirmed = new HashSet<long>();
        var supersededIds = new HashSet<long>();

        if (candidates.Count == 0)
            return new ConsolidationPlan(insert, supersede, confirmIds);

        // Все Active — пул для сравнения (Superseded/Invalidated/Pending не участвуют:
        // superseded-факт никогда не воскресает и не «подтверждается» обратно).
        var actives = store.ListItems(status: MemoryItemStatus.Active, limit: int.MaxValue);

        // ---- (a) Exact-dedup: normalized content + scope + type уже есть → Confirm. ----
        var exactIndex = new Dictionary<string, MemoryItem>(actives.Count);
        foreach (var a in actives)
            exactIndex.TryAdd(ExactKey(a), a);

        // Ключи, уже занятые кандидатами этой пачки — дубль внутри extraction'а дропаем.
        var claimed = new HashSet<string>();
        var rest = new List<MemoryItem>(candidates.Count);
        foreach (var c in candidates)
        {
            var key = ExactKey(c);
            if (exactIndex.TryGetValue(key, out var existing))
            {
                if (confirmed.Add(existing.Id))
                    confirmIds.Add(existing.Id);
                continue;
            }
            if (!claimed.Add(key))
            {
                // Точный дубль внутри одной пачки кандидатов — второй молча пропускаем.
                _log?.Debug($"Consolidate: intra-batch duplicate skipped '{Truncate(c.Content)}'", LogCategory);
                continue;
            }
            rest.Add(c);
        }

        // ---- (b) Embeddings-дедуп: cos >= DedupeSimilarity при том же scope. ----
        if (_emb is { IsAvailable: true })
        {
            var storeVecs = LoadActiveEmbeddings(store, actives, _emb.EmbeddingTag);
            var stillRest = new List<MemoryItem>(rest.Count);
            foreach (var c in rest)
            {
                var vec = await EmbedSafeAsync(c.Content, ct);
                if (vec != null)
                {
                    MemoryItem? best = null;
                    var bestCos = _cfg.DedupeSimilarity;
                    foreach (var a in actives)
                    {
                        if (!SameScope(a, c)) continue;
                        if (!storeVecs.TryGetValue(a.Id, out var av) || av.Length != vec.Length) continue;
                        var cos = Cosine(vec, av);
                        if (cos >= bestCos) { bestCos = cos; best = a; }
                    }
                    if (best != null)
                    {
                        // Идентичны (existing покрывает новый) → подтверждение;
                        // иначе новый несёт обновление → supersede.
                        if (IsCoveredBy(TextNormalizer.Normalize(c.Content),
                                        TextNormalizer.Normalize(best.Content)))
                        {
                            if (confirmed.Add(best.Id))
                                confirmIds.Add(best.Id);
                        }
                        else if (!supersededIds.Contains(best.Id))
                        {
                            supersede.Add((best.Id, c));
                            supersededIds.Add(best.Id);
                        }
                        continue;
                    }
                }
                stillRest.Add(c);
            }
            rest = stillRest;
        }

        // ---- (c) LLM-merge: только при большой пачке и наличии LLM. ----
        if (rest.Count > LlmMergeThreshold && _llm != null)
            await LlmMergeAsync(rest, actives, insert, supersede, supersededIds,
                confirmIds, confirmed, ct);
        else
            insert.AddRange(rest);

        _log?.Info($"Consolidate: {candidates.Count} candidates → insert={insert.Count}, " +
                   $"supersede={supersede.Count}, confirm={confirmIds.Count}", LogCategory);
        return new ConsolidationPlan(insert, supersede, confirmIds);
    }

    /// <summary>
    /// Применить план к store. Insert → InsertItem; Supersede → InsertItem(new) +
    /// SetStatus(existing, Superseded, newId); Confirm → touch LastConfirmedAtUtc.
    /// Повторный проход (job retry) идемпотентен: Confirm/Supersede по уже
    /// не-Active элементу — no-op, superseded не воскресает.
    /// </summary>
    public Task ApplyAsync(ConsolidationPlan plan, IMemoryStore store)
    {
        foreach (var it in plan.Insert)
            store.InsertItem(it);

        foreach (var (existingId, newItem) in plan.Supersede)
        {
            var newId = store.InsertItem(newItem);
            // Insert сначала — чтобы superseded_by указывал на реальную запись.
            var existing = store.GetItem(existingId);
            if (existing != null && existing.Status == MemoryItemStatus.Active)
                store.SetStatus(existingId, MemoryItemStatus.Superseded, newId);
        }

        foreach (var id in plan.ConfirmIds.Distinct())
        {
            var it = store.GetItem(id);
            if (it == null || it.Status != MemoryItemStatus.Active) continue;
            it.LastConfirmedAtUtc = DateTime.UtcNow;
            store.UpdateItem(it);
        }
        return Task.CompletedTask;
    }

    // ---------------- internals ----------------

    private static string ExactKey(MemoryItem it) =>
        $"{TextNormalizer.Normalize(it.Content)}|{(int)it.ScopeType}|{it.ScopeId}|{(int)it.Type}";

    private static bool SameScope(MemoryItem a, MemoryItem b) =>
        a.ScopeType == b.ScopeType && a.ScopeId == b.ScopeId;

    /// <summary>Тот же факт заново: совпадение или новый — подстрока существующего.</summary>
    private static bool IsCoveredBy(string normNew, string normExisting) =>
        normNew.Length > 0 && normExisting.Contains(normNew, StringComparison.Ordinal);

    private static Dictionary<long, float[]> LoadActiveEmbeddings(IMemoryStore store,
        List<MemoryItem> actives, string? tag)
    {
        var map = new Dictionary<long, bool>(actives.Count);
        foreach (var a in actives) map[a.Id] = true;
        var vecs = new Dictionary<long, float[]>();
        // Только vector-space текущего провайдера (§35): чужие вектора в дедупе
        // дали бы ложные срабатывания.
        foreach (var (itemId, vec, model) in store.LoadEmbeddings())
            if (map.ContainsKey(itemId) && (tag == null || model == tag))
                vecs.TryAdd(itemId, vec);
        return vecs;
    }

    private async Task<float[]?> EmbedSafeAsync(string text, CancellationToken ct)
    {
        try { return await _emb!.EmbedAsync(text, isQuery: false, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn($"Consolidate: embed failed ({ex.Message})", LogCategory);
            return null;
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>
    /// LLM-поиск дублей батчами. Промпт возвращает JSON {"pairs":[["N<i>","E<id>"],…]};
    /// непарсибельный ответ → все батч-кандидаты уходят в Insert (консервативно).
    /// </summary>
    private async Task LlmMergeAsync(List<MemoryItem> rest, List<MemoryItem> actives,
        List<MemoryItem> insert, List<(long, MemoryItem)> supersede,
        HashSet<long> supersededIds, List<long> confirmIds, HashSet<long> confirmed,
        CancellationToken ct)
    {
        var handled = new HashSet<int>();          // индексы rest, убранные LLM-парами
        var existingCtx = actives
            .OrderByDescending(a => a.UpdatedAtUtc)
            .Take(LlmExistingContext)
            .ToList();

        for (var start = 0; start < rest.Count; start += LlmBatchSize)
        {
            var take = Math.Min(LlmBatchSize, rest.Count - start);
            var prompt = BuildMergePrompt(rest, start, take, existingCtx);
            var resp = await AskSafeAsync(prompt, ct);
            if (resp == null)
                continue; // батч без ответа → его кандидаты просто уходят в Insert

            foreach (var (a, b) in ParsePairs(resp))
            {
                var aIdx = IndexOfLabel(a);
                var bIdx = IndexOfLabel(b);

                if (aIdx >= 0 && bIdx >= 0)
                {
                    // Дубль внутри пачки: оставляем более информативный (длинный) текст.
                    handled.Add(PickNewer(aIdx, bIdx));
                    continue;
                }

                // Пара новый↔существующий.
                var nIdx = aIdx >= 0 ? aIdx : bIdx;
                var eLabel = aIdx >= 0 ? b : a;
                if (nIdx < 0 || handled.Contains(nIdx)) continue;
                if (eLabel.Length < 2 || eLabel[0] != 'E' ||
                    !long.TryParse(eLabel[1..], out var eId))
                    continue;
                var existing = actives.FirstOrDefault(x => x.Id == eId);
                if (existing == null || supersededIds.Contains(existing.Id)) continue;
                // Scope-гард: LLM мог сматчить факты разных scope — не трогаем их.
                if (!SameScope(rest[nIdx], existing)) continue;

                var c = rest[nIdx];
                if (IsCoveredBy(TextNormalizer.Normalize(c.Content),
                                TextNormalizer.Normalize(existing.Content)))
                {
                    if (confirmed.Add(existing.Id))
                        confirmIds.Add(existing.Id);
                }
                else
                {
                    supersede.Add((existing.Id, c));
                    supersededIds.Add(existing.Id);
                }
                handled.Add(nIdx);
            }
        }

        for (var i = 0; i < rest.Count; i++)
            if (!handled.Contains(i))
                insert.Add(rest[i]);

        int IndexOfLabel(string label)
        {
            if (label.Length > 1 && label[0] == 'N' &&
                int.TryParse(label[1..], out var idx) && idx >= 0 && idx < rest.Count)
                return idx;
            return -1;
        }

        int PickNewer(int i, int j)
        {
            // «Больше info» для двух кандидатов одной пачки — возвращает индекс
            // ДРОПАЕМОГО (менее информативного) элемента.
            var ni = TextNormalizer.Normalize(rest[i].Content);
            var nj = TextNormalizer.Normalize(rest[j].Content);
            return nj.Length > ni.Length ? i : j;
        }
    }

    private static string BuildMergePrompt(List<MemoryItem> rest, int start, int take,
        List<MemoryItem> existingCtx)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("Найди пары записей, которые описывают ОДИН И ТОТ ЖЕ факт проекта/встречи");
        sb.AppendLine("(одно может уточнять другое или просто повторять). Каждый элемент помечен id:");
        sb.AppendLine("N<i> — новые кандидаты, E<id> — уже хранящиеся факты.");
        sb.AppendLine();
        sb.AppendLine("НОВЫЕ:");
        for (var i = start; i < start + take; i++)
            sb.AppendLine($"  N{i}: [{rest[i].Type}] {rest[i].Content}");
        if (existingCtx.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("СУЩЕСТВУЮЩИЕ:");
            foreach (var e in existingCtx)
                sb.AppendLine($"  E{e.Id}: [{e.Type}] {e.Content}");
        }
        sb.AppendLine();
        sb.AppendLine("Верни ТОЛЬКО JSON вида {\"pairs\":[[\"N0\",\"E12\"],[\"N3\",\"N7\"]]} — пары id дублей.");
        sb.AppendLine("Если дублей нет — {\"pairs\":[]}. Без пояснений и markdown.");
        return sb.ToString();
    }

    private async Task<string?> AskSafeAsync(string userText, CancellationToken ct)
    {
        try
        {
            var r = await _llm!.AskAsync(userText,
                "Ты — модуль дедупликации памяти встреч. Отвечай строго одним JSON-объектом.",
                maxTokens: 1500, new List<ChatMessage>(), ct, background: true, requestTimeout: TimeSpan.FromSeconds(120));
            if (r.Error != null)
            {
                _log?.Warn($"Consolidate: merge LLM failed ({r.Error})", LogCategory);
                return null;
            }
            return string.IsNullOrWhiteSpace(r.Content) ? null : r.Content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn($"Consolidate: merge LLM threw ({ex.Message})", LogCategory);
            return null;
        }
    }

    /// <summary>Выдрать первый {...} из ответа и распарсить пары меток.</summary>
    private static IEnumerable<(string A, string B)> ParsePairs(string content)
    {
        var from = content.IndexOf('{');
        var to = content.LastIndexOf('}');
        if (from < 0 || to <= from) yield break;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(content.Substring(from, to - from + 1)); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("pairs", out var pairs) ||
                pairs.ValueKind != JsonValueKind.Array)
                yield break;
            foreach (var p in pairs.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 2) continue;
                var a = p[0].GetString();
                var b = p[1].GetString();
                if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                    yield return (a.Trim(), b.Trim());
            }
        }
    }

    private static string Truncate(string s) =>
        s.Length <= 60 ? s : s[..57] + "...";
}
