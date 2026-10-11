namespace BrainstormBuddy.Memory.Recall;

/// <summary>
/// Гибридный ранкер recall-кандидатов (спека «MEMORY RETRIEVAL»):
/// score = 0.35·FTS_norm + 0.30·Vec + 0.12·ScopeMatch + 0.10·Recency
///       + 0.08·Importance + 0.05·Status + 0.20·Pinned.
/// FTS нормализуется на максимум выдачи; recency — exp(-days/30) от UpdatedAtUtc.
/// Не-Active статусы не попадают в выдачу: Superseded — только по IncludeSuperseded,
/// остальные (Pending/Invalidated) — только по IncludeHistorical. Дедуп по Item.Id.
/// </summary>
public sealed class MemoryRanker : IMemoryRanker
{
    private readonly Func<DateTime> _now;

    public MemoryRanker() : this(null) { }
    /// <summary>Время инъектируемо — ranker чистый и детерминирован в тестах.</summary>
    public MemoryRanker(Func<DateTime>? now) => _now = now ?? (() => DateTime.UtcNow);

    public List<MemoryRecallHit> Rank(MemoryRecallQuery q,
        List<(MemoryItem Item, double FtsScore)> fts,
        List<(MemoryItem Item, double VecScore)> vec)
    {
        var cand = new Dictionary<long, Cand>();
        var order = new List<long>();
        void Add(MemoryItem item)
        {
            if (!cand.ContainsKey(item.Id)) { cand[item.Id] = new Cand(item); order.Add(item.Id); }
        }
        foreach (var x in fts) Add(x.Item);
        foreach (var x in vec) Add(x.Item);
        if (cand.Count == 0) return new List<MemoryRecallHit>();

        double ftsMax = fts.Count > 0 ? fts.Max(x => x.FtsScore) : 0.0;
        foreach (var x in fts)
            cand[x.Item.Id].Fts = ftsMax > 0 ? Math.Max(0.0, x.FtsScore) / ftsMax : 0.0;
        foreach (var x in vec)
            cand[x.Item.Id].Vec = Math.Clamp(x.VecScore, 0.0, 1.0);

        var now = _now();
        var hits = new List<MemoryRecallHit>(cand.Count);
        foreach (var id in order)
        {
            var c = cand[id];
            var it = c.Item;
            // Не-Active факты не протекают в контекст: историю отдаём только по флагам.
            if (it.Status != MemoryItemStatus.Active && !q.IncludeHistorical
                && !(it.Status == MemoryItemStatus.Superseded && q.IncludeSuperseded))
                continue;
            double scope = ScopeScore(q, it);
            double recency = Math.Exp(-Math.Max(0.0, (now - it.UpdatedAtUtc).TotalDays) / 30.0);
            // Статус-вес — дискриминатор: при равных сигналах свежий факт выше.
            // По IncludeSuperseded superseded-факт обязан всплыть (§30 historical
            // gate) — полный штраф 0.15 навсегда гнал бы его под активные
            // векторные соседи, поэтому смягчаем до 0.9, но не обнуляем.
            double status = it.Status switch
            {
                _ when it.Status == MemoryItemStatus.Superseded && q.IncludeSuperseded => 0.9,
                MemoryItemStatus.Active => 1.0,
                MemoryItemStatus.Superseded => 0.15,
                _ => 0.0, // Pending, Invalidated — доходят только по IncludeHistorical
            };
            double score = 0.35 * c.Fts + 0.30 * c.Vec + 0.12 * scope + 0.10 * recency
                         + 0.08 * it.Importance + 0.05 * status
                         + (it.Pinned ? 0.20 : 0.0);
            hits.Add(new MemoryRecallHit(it, score,
                BuildWhy(c.Fts, c.Vec, scope, recency, status, it.Pinned)));
        }

        hits.Sort((a, b) => b.Score != a.Score
            ? b.Score.CompareTo(a.Score)
            : a.Item.Id.CompareTo(b.Item.Id));
        if (hits.Count > q.MaxItems)
            hits.RemoveRange(q.MaxItems, hits.Count - q.MaxItems);
        return hits;
    }

    private static double ScopeScore(MemoryRecallQuery q, MemoryItem it)
    {
        if (q.Scope is null)
            return 0.6;                                // scope не задан — нейтральный бонус
        if (it.ScopeType == q.Scope.Value &&
            (q.ScopeId is null || it.ScopeId == q.ScopeId.Value))
            return 1.0;                                // точное совпадение контекста
        if (it.ScopeType == MemoryScopeType.Global)
            return 0.6;                                // глобальная память применима всегда
        return 0.0;
    }

    /// <summary>Почему item вытащен — теги сигналов с ненулевым вкладом.</summary>
    private static string BuildWhy(double fts, double vec, double scope, double recency,
        double status, bool pinned)
    {
        var why = new List<string>(6);
        if (fts > 0) why.Add("fts");
        if (vec > 0) why.Add("vec");
        if (scope >= 1.0) why.Add("scope");
        else if (scope > 0) why.Add("scope~");
        if (recency >= 0.9) why.Add("recent");
        if (pinned) why.Add("pinned");
        if (status <= 0.15) why.Add("stale-status");
        return string.Join('+', why);
    }

    private sealed class Cand
    {
        public readonly MemoryItem Item;
        public double Fts;
        public double Vec;
        public Cand(MemoryItem item) => Item = item;
    }
}
