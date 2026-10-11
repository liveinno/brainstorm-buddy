using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Memory.Storage;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Jobs;

/// <summary>
/// Стандартные handler'ы memory jobs. Возвращают null при успехе, строку ошибки —
/// при retryable/фатальном сбое (решение retry vs failed принимает worker).
/// kind'ы:
///   extract   {"meetingId":N} — саммари + extraction + consolidation для встречи
///   embed     {"itemIds":[...]} | {"meetingId":N} | {} — до-эмбед изменённых items
///   retention {}              — PurgeSegmentsOlderThan по TranscriptRetention
///   reembed   {}              — пересчитать все embeddings (смена embedding-модели)
/// </summary>
public sealed class MemoryJobHandlers
{
    private const string LogCategory = "Memory";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IMemoryStore _store;
    private readonly MemoryConfig _cfg;
    private readonly IMemoryExtractionService? _extractor;
    private readonly IMemoryConsolidator? _consolidator;
    private readonly IMemoryEmbeddingProvider? _embedder;
    private readonly LoggingService? _log;
    private readonly CancellationToken _ct;

    public MemoryJobHandlers(IMemoryStore store, MemoryConfig cfg,
        IMemoryExtractionService? extractor = null,
        IMemoryConsolidator? consolidator = null,
        IMemoryEmbeddingProvider? embedder = null,
        LoggingService? log = null,
        CancellationToken ct = default)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _extractor = extractor;
        _consolidator = consolidator;
        _embedder = embedder;
        _log = log;
        _ct = ct;
    }

    /// <summary>
    /// Колбэк после успешного extract-джоба (meetingId). Вызывается на потоке worker'а —
    /// подписчик (App) сам маршаллит на UI для показа MemoryReviewWindow в review-режиме.
    /// </summary>
    public Action<long>? ExtractionApplied { get; set; }

    /// <summary>Готовый dispatch-словарь для <see cref="MemoryJobWorker"/>.</summary>
    public IReadOnlyDictionary<string, Func<MemoryJob, Task<string?>>> CreateHandlers() =>
        new Dictionary<string, Func<MemoryJob, Task<string?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["extract"] = ExtractAsync,
            ["embed"] = EmbedAsync,
            ["retention"] = RetentionAsync,
            ["reembed"] = ReembedAsync,
        };

    /// <summary>
    /// extract: meeting+segments из store → extractor → consolidator.PlanAsync →
    /// применить план (insert/supersede/confirm) → meeting.Summary через UpsertMeeting.
    /// </summary>
    public async Task<string?> ExtractAsync(MemoryJob job)
    {
        ExtractPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ExtractPayload>(
                string.IsNullOrWhiteSpace(job.PayloadJson) ? "{}" : job.PayloadJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            return $"extract payload json: {ex.Message}";
        }
        if (payload?.MeetingId is not > 0)
            return "extract payload: missing meetingId";

        var meeting = _store.GetMeeting(payload.MeetingId.Value);
        if (meeting == null)
            return $"meeting {payload.MeetingId.Value} not found";

        // Manual-режим: автоматическая экстракция выключена (джоба могла остаться
        // в очереди от прошлой сессии/до смены настройки) — пропускаем до вызова LLM.
        if (string.Equals(_cfg.ReviewMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Info($"extract meeting {meeting.Id}: skipped (review mode = manual)", LogCategory);
            return null;
        }

        var segments = _store.GetSegments(meeting.Id);

        // Пустая встреча (ни одной реплики) — LLM-вызов бессмысленен: джоба
        // завершается успехом, а не ретрами до failed из-за «пустого транскрипта».
        if (segments.Count == 0)
        {
            _log?.Info($"extract meeting {meeting.Id}: no segments — skipped", LogCategory);
            return null;
        }


        if (_extractor == null)
            return "extraction service unavailable";

        MemoryExtractionResult result;
        try
        {
            result = await _extractor.ExtractAsync(meeting, segments, _ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"extract: {ex.Message}";
        }
        if (result.Error != null)
            return $"extract: {result.Error}";

        if (_consolidator == null)
            return "consolidator unavailable";

        ConsolidationPlan plan;
        try
        {
            plan = await _consolidator.PlanAsync(result.Candidates, _store, _ct)
                .ConfigureAwait(false);
            ApplyPlan(plan, meeting.MemoryMode);
        }
        catch (Exception ex)
        {
            return $"consolidate: {ex.Message}";
        }

        if (result.Summary != meeting.Summary)
        {
            meeting.Summary = result.Summary;
            _store.UpsertMeeting(meeting);
        }
        _log?.Info($"extracted meeting {meeting.Id}: {plan.Insert.Count} new, " +
            $"{plan.Supersede.Count} superseded, {plan.ConfirmIds.Count} confirmed", LogCategory);
        // Только что появились активные факты → сразу ставим embed-доиндексацию
        // (спека §34): пустой payload = «всё без embedding текущего тега».
        try { _store.EnqueueJob("embed"); }
        catch (Exception ex) { _log?.Warn($"enqueue embed failed: {ex.Message}", LogCategory); }
        try { ExtractionApplied?.Invoke(meeting.Id); }
        catch (Exception ex) { _log?.Warn($"extraction callback failed: {ex.Message}", LogCategory); }
        return null;
    }

    /// <summary>
    /// SaveMode-гейт (спека MEMORY SAVE MODES): summary/summaryonly → только Decision/ActionItem
    /// (саммари встречи пишется отдельно в ExtractAsync), off → ничего,
    /// smart/full/неизвестный → всё. Сегменты — отдельная retention-политика, тут не трогаем.
    /// </summary>
    internal static bool ShouldKeepItem(MemoryItemType type, string? saveMode) =>
        saveMode?.ToLowerInvariant() switch
        {
            "off" => false,
            "summary" or "summaryonly" =>
                type is MemoryItemType.Decision or MemoryItemType.ActionItem,
            _ => true,
        };

    /// <summary>
    /// Применение ConsolidationPlan: insert новых, supersede старых, confirm.
    /// ReviewMode=review → новые items пишутся Pending и ждут MemoryReviewWindow;
    /// при отклонении pending-item окно возвращает superseded-оригинал в Active.
    /// </summary>
    private void ApplyPlan(ConsolidationPlan plan, string? saveMode)
    {
        var review = string.Equals(_cfg.ReviewMode, "review", StringComparison.OrdinalIgnoreCase);
        var status = review ? MemoryItemStatus.Pending : MemoryItemStatus.Active;

        foreach (var it in plan.Insert)
        {
            if (!ShouldKeepItem(it.Type, saveMode)) continue;
            it.Status = status;
            _store.InsertItem(it);
        }
        foreach (var (existingId, newItem) in plan.Supersede)
        {
            if (!ShouldKeepItem(newItem.Type, saveMode)) continue;
            newItem.Status = status;
            _store.InsertItem(newItem);
            _store.SetStatus(existingId, MemoryItemStatus.Superseded, newItem.Id);
        }
        foreach (var id in plan.ConfirmIds)
        {
            var item = _store.GetItem(id);
            if (item == null) continue;
            item.LastConfirmedAtUtc = DateTime.UtcNow;
            item.UpdatedAtUtc = DateTime.UtcNow;
            _store.UpdateItem(item);
        }
    }

    /// <summary>
    /// retention: {} → PurgeSegmentsOlderThan(now − TranscriptRetention) +
    /// PurgeItemsOlderThan(now − MemoryRetention[Days]) — две независимые политики
    /// (спека RETENTION: transcript и extracted memory хранятся раздельно).
    /// never|forever → соответствующий purge пропускается.
    /// </summary>
    public Task<string?> RetentionAsync(MemoryJob job)
    {
        string? error = null;

        var segDays = RetentionDays(_cfg.TranscriptRetention);
        if (segDays == null)
        {
            _log?.Debug($"transcript retention '{_cfg.TranscriptRetention}' → skip purge", LogCategory);
        }
        else
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(segDays.Value);
            try
            {
                var purged = _store.PurgeSegmentsOlderThan(cutoff);
                if (purged > 0)
                    _log?.Info($"retention: purged {purged} transcript segments older than {segDays.Value}d", LogCategory);
            }
            catch (Exception ex)
            {
                error = $"segment purge: {ex.Message}";
            }
        }

        var itemDays = ItemRetentionDays(_cfg.MemoryRetention, _cfg.MemoryRetentionDays);
        if (itemDays == null)
        {
            _log?.Debug($"memory retention '{_cfg.MemoryRetention}' → skip purge", LogCategory);
        }
        else if (_store is SqliteMemoryStore sqlite)
        {
            // PurgeItemsOlderThan — конкретный метод SqliteMemoryStore (интерфейс стабилен).
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(itemDays.Value);
            try
            {
                var purged = sqlite.PurgeItemsOlderThan(cutoff);
                if (purged > 0)
                    _log?.Info($"retention: purged {purged} memory items older than {itemDays.Value}d", LogCategory);
            }
            catch (Exception ex)
            {
                error = error == null ? $"item purge: {ex.Message}" : $"{error}; item purge: {ex.Message}";
            }
        }
        return Task.FromResult(error);
    }

    /// <summary>MemoryRetention → дни хранения extracted items; null = хранить вечно.</summary>
    private int? ItemRetentionDays(string? mode, int customDays) => mode?.ToLowerInvariant() switch
    {
        "days30" => 30,
        "days90" => 90,
        "custom" => customDays > 0 ? customDays : null,
        _ => null,   // forever/неизвестный — факты не удаляем
    };

    private static int? RetentionDays(string mode) => mode?.ToLowerInvariant() switch
    {
        "days7" => 7,
        "days30" => 30,
        "days90" => 90,
        "never" or "forever" or null => null,
        _ => null,   // неизвестный режим — ничего не удаляем
    };

    /// <summary>
    /// embed: re-embed изменённых items. Payload:
    ///   {"itemIds":[1,2]}  — точечно
    ///   {"meetingId":N}    — items, извлечённые из встречи
    ///   {}                 — items без embedding текущей модели
    /// </summary>
    public Task<string?> EmbedAsync(MemoryJob job)
    {
        EmbedPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EmbedPayload>(
                string.IsNullOrWhiteSpace(job.PayloadJson) ? "{}" : job.PayloadJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            return Task.FromResult<string?>($"embed payload json: {ex.Message}");
        }

        var items = ResolveEmbedTargets(payload);
        return EmbedItemsAsync(items);
    }

    /// <summary>reembed: пересчитать embeddings для всех items (смена embedding-модели).</summary>
    public Task<string?> ReembedAsync(MemoryJob job) =>
        EmbedItemsAsync(_store.ListItems(limit: int.MaxValue));

    private List<MemoryItem> ResolveEmbedTargets(EmbedPayload? payload)
    {
        if (payload?.ItemIds is { Count: > 0 } ids)
        {
            var items = new List<MemoryItem>(ids.Count);
            foreach (var id in ids)
            {
                var it = _store.GetItem(id);
                if (it != null) items.Add(it);
            }
            return items;
        }
        if (payload?.MeetingId is long mid && mid > 0)
        {
            // Items без собственных embeddings для данной встречи.
            var have = new HashSet<long>(_store.LoadEmbeddings().Select(e => e.ItemId));
            return _store.ListItems(limit: int.MaxValue)
                .Where(i => i.SourceMeetingId == mid && !have.Contains(i.Id)).ToList();
        }
        // Изменённые/новые = без embedding под текущим vector-space тегом (§35):
        // тег включает модель+фингерпринт — смена модели переименовывает пространство.
        var embedded = _store.LoadEmbeddings()
            .Where(e => e.Model == _embedder?.EmbeddingTag)
            .Select(e => e.ItemId).ToHashSet();
        return _store.ListItems(status: MemoryItemStatus.Active, limit: int.MaxValue)
            .Where(i => !embedded.Contains(i.Id)).ToList();
    }

    /// <summary>
    /// Весь набор — ОДНИМ батчем EmbedBatchAsync: провайдер чанкует сам, но держит
    /// один lease на задание — для MemorySaver «задание» = весь embed-джоб,
    /// а не per-item load/unload (спека §13/§36). До-эмбед происходит между
    /// acquire и release — idle countdown стартует после backfill.
    /// </summary>
    private async Task<string?> EmbedItemsAsync(List<MemoryItem> items)
    {
        if (_embedder == null || !_embedder.IsAvailable)
            return "embedding provider unavailable";
        if (items.Count == 0) return null;

        float[]?[] vecs;
        try
        {
            vecs = await _embedder.EmbedBatchAsync(
                       items.Select(i => i.Content).ToArray(), isQuery: false, _ct)
                   .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"embed batch: {ex.Message}";
        }

        var tag = _embedder.EmbeddingTag;
        int done = 0, skipped = 0;
        for (int i = 0; i < items.Count && i < vecs.Length; i++)
        {
            var vec = vecs[i];
            if (vec == null) { skipped++; continue; }
            _store.SaveEmbedding(items[i].Id, vec, tag);
            done++;
        }
        if (skipped > 0)
            _log?.Warn($"embed: {skipped} items skipped (provider returned null)", LogCategory);
        _log?.Info($"embed: {done} items embedded", LogCategory);
        return null;
    }

    private sealed class ExtractPayload { public long? MeetingId { get; set; } }
    private sealed class EmbedPayload
    {
        public List<long>? ItemIds { get; set; }
        public long? MeetingId { get; set; }
    }
}
