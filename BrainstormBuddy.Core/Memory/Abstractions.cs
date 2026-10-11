namespace BrainstormBuddy.Memory;

/// <summary>
/// Единая точка записи/чтения memory.db (SQLite source of truth). Все методы
/// потокобезопасны реализацией (одна SqliteConnection + lock). Миграции —
/// идемпотентный Migrate() с schema_version.
/// </summary>
public interface IMemoryStore : IDisposable
{
    /// <summary>Монотонный счётчик мутаций item/embedding-данных — для кэшей recall.</summary>
    long DataRevision { get; }
    string DbPath { get; }
    bool FtsAvailable { get; }                 // false → SearchFts падает в LIKE-fallback
    void Migrate();                            // идемпотентно

    // meetings / segments
    long UpsertMeeting(MemoryMeeting m);
    MemoryMeeting? GetMeeting(long id);
    List<MemoryMeeting> ListMeetings(int limit = 200);
    long AppendSegment(MemorySegment s);       // seq = max(seq)+1
    List<MemorySegment> GetSegments(long meetingId);

    // items
    long InsertItem(MemoryItem it);
    void UpdateItem(MemoryItem it);
    MemoryItem? GetItem(long id);
    List<MemoryItem> ListItems(MemoryScopeType? scope = null, long? scopeId = null,
        MemoryItemStatus? status = null, int limit = 500);
    void SetStatus(long id, MemoryItemStatus st, long? supersededBy = null);
    void HardDeleteItem(long id);

    // projects / people / tags
    long UpsertProject(MemoryProject p);
    List<MemoryProject> ListProjects(bool includeArchived = false);
    long UpsertPerson(MemoryPerson p);
    List<MemoryPerson> ListPeople();
    void AddTag(long itemId, string tag);
    List<string> GetTags(long itemId);

    // FTS + embeddings
    List<(MemoryItem Item, double FtsScore)> SearchFts(string query, int limit = 50);
    void SaveEmbedding(long itemId, float[] vec, string model);
    List<(long ItemId, float[] Vec, string Model)> LoadEmbeddings();

    // jobs (persisted, переживают рестарт)
    long EnqueueJob(string kind, string payloadJson = "");
    List<MemoryJob> ListJobs(string? status = null, int limit = 100);
    MemoryJob? ClaimJob(TimeSpan staleAfter);  // pending|stale-running → running
    void FinishJob(long id, string? error = null);
    void RequeueStale(TimeSpan staleAfter);
    void RetryJob(long id);                    // failed|running → pending, attempts=0, run_after=NULL
    int DeleteJobsByStatus(string status);     // например "failed" — массовая зачистка
    void DeleteProject(long id);               // проект + его scope-items; meetings теряют ссылку
    void DeletePerson(long id);                // персона + её scope-items

    // maintenance
    void DeleteMeeting(long id, bool deleteDerivedItems, bool deleteSegments);
    int PurgeSegmentsOlderThan(DateTime beforeUtc);
    MemoryStats GetStats();
}

/// <summary>Провайдер эмбеддингов. Disabled → EmbedAsync возвращает null.</summary>
public interface IMemoryEmbeddingProvider
{
    string Name { get; }
    /// <summary>
    /// Тег vector-space для сохранённых эмбеддингов: провайдер+модель+фингерпринт.
    /// SaveEmbedding пишет его, recall/consolidator/embed-job фильтруют по нему —
    /// смена embedding-модели не смешивает старые и новые векторы (reindex через
    /// embed-джобу). Дефолт = Name: провайдерам без версионирования ничего не надо.
    /// </summary>
    string EmbeddingTag => Name;
    bool IsAvailable { get; }
    Task<float[]?> EmbedAsync(string text, bool isQuery, CancellationToken ct);
    /// <summary>
    /// Пакетный эмбеддинг: дефолт — последовательный EmbedAsync; реализации с
    /// дорогим lifecycle (lazy-load ONNX) переопределяют, чтобы держать модель
    /// один раз на весь батч (важно для режима MemorySaver).
    /// </summary>
    async Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string> texts, bool isQuery,
        CancellationToken ct)
    {
        var result = new float[]?[texts.Count];
        for (int i = 0; i < texts.Count; i++)
            result[i] = await EmbedAsync(texts[i], isQuery, ct).ConfigureAwait(false);
        return result;
    }
}

/// <summary>Гибридный ранкер: FTS + vector + scope + recency + importance + status.</summary>
public interface IMemoryRanker
{
    List<MemoryRecallHit> Rank(MemoryRecallQuery q,
        List<(MemoryItem Item, double FtsScore)> fts,
        List<(MemoryItem Item, double VecScore)> vec);
}

/// <summary>Recall для assistant/Ask Memory/Pre-call Brief.</summary>
public interface IMemoryRecallService
{
    Task<List<MemoryRecallHit>> RecallAsync(MemoryRecallQuery q, CancellationToken ct);
    /// <summary>&lt;memory_context&gt;…&lt;/memory_context&gt; блок, уложенный в токен-бюджет.</summary>
    string BuildContextBlock(List<MemoryRecallHit> hits, int maxTokens);
}

/// <summary>Memory LLM: саммари + извлечение структурированных items. Никогда не блокирует live.</summary>
public interface IMemoryExtractionService
{
    Task<MemoryExtractionResult> ExtractAsync(MemoryMeeting m,
        List<MemorySegment> segs, CancellationToken ct);
}

/// <summary>Дедупликация и supersession: решить, что insert/supersede/confirm.</summary>
public interface IMemoryConsolidator
{
    Task<ConsolidationPlan> PlanAsync(List<MemoryItem> candidates,
        IMemoryStore store, CancellationToken ct);
}
