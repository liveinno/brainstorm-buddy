using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Параметры применения директив, извлечённых из прикреплённого документа
/// (AttachmentFactExtractor): provenance факта — документ-источник и якорь
/// фрагмента; ForcePending — review-режим памяти (кандидаты ждут подтверждения).
/// </summary>
public sealed record AttachmentApplyOptions(
    string? DocumentId,
    string? DocName,
    string? DocSha256,
    string? Anchor,
    bool ForcePending = false);

/// <summary>
/// Применяет memory-директивы из ответа LLM (<see cref="MemoryDirectiveParser"/>)
/// к <see cref="IMemoryStore"/>. Add → вставка fact-item'а (scope Meeting при
/// наличии meetingId, иначе Global); Update → supersession-цепочка (старый item →
/// Superseded, новый → Active); Forget → Invalidate ближайшего активного item'а.
/// Ошибки одной директивы не роняют остальные; не-применённые уходят в Warn-лог.
///
/// Дедупликация: все Add-директивы батча прогоняются через
/// <see cref="IMemoryConsolidator"/> (если передан — обычно из App), который
/// отсекает точные и семантические (embedding) дубли и превращает «тот же факт,
/// другими словами» в supersede, а не в новый ряд. Без консолидатора действует
/// прежний fallback: точное совпадение текста → пропуск. Это закрывает баг
/// 2.29.x: модель, повторяющая одну и ту же директиву на каждом STT-чанке,
/// плодила N строк одного факта (дедуп был только у attachment-directive).
public sealed class MemoryWriter
{
    private const string LogCategory = "Memory";
    private const string SourceTag = "llm-directive";
    private const string AttachmentSourceTag = "attachment-directive";
    private const int MatchLimit = 5;

    private readonly IMemoryStore _store;
    private readonly LoggingService? _logger;
    private readonly MemoryConsolidator? _consolidator;

    /// <param name="consolidator">Конкретный консолидатор (не интерфейс —
    /// нужен ApplyAsync для самой вставки; IMemoryConsolidator его не гарантирует).</param>
    public MemoryWriter(IMemoryStore store, LoggingService? logger = null,
        MemoryConsolidator? consolidator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
        _consolidator = consolidator;
    }

    /// <summary>
    /// Применить директивы по порядку. Возвращает человекочитаемые описания
    /// фактически применённых операций (для тоста/лога).
    /// </summary>
    public Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<MemoryDirective> dirs,
        long? meetingId, CancellationToken ct)
        => ApplyAsync(dirs, meetingId, ct, opts: null);

    /// <summary>
    /// Перегрузка для фактов из прикреплённых документов: проставляет provenance
    /// (документ/имя/sha256/якорь фрагмента) и при <c>opts.ForcePending</c> пишет
    /// items как Pending (кандидаты на ревью, а не сохранённые факты).
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<MemoryDirective> dirs,
        long? meetingId, CancellationToken ct, AttachmentApplyOptions? opts)
    {
        var applied = new List<string>();
        if (dirs is not { Count: > 0 })
            return applied;

        // Add-директивы — кандидаты для консолидатора: сначала прогоняем их пачкой
        // (intra-batch дедуп + дедуп против Active), затем по порядку применяем
        // Update/Forget и те Add, что консолидатор пропустил в insert. Порядок
        // внутри батча меняется только между Add и не-Add — приемлемо: Update/Forget
        // на только что добавленный факт — патологический кейс, не практика.
        var addCandidates = new List<(int Index, MemoryItem Item)>();
        var nonAdd = new List<MemoryDirective>();
        for (var i = 0; i < dirs.Count; i++)
        {
            var d = dirs[i];
            if (d.Kind == MemoryDirectiveKind.Add && !string.IsNullOrWhiteSpace(d.Old))
                addCandidates.Add((i, NewFactItem(d.Old.Trim(), meetingId, opts)));
            else
                nonAdd.Add(d);
        }

        var insertedIdx = new HashSet<int>();
        if (addCandidates.Count > 0 && _consolidator != null)
        {
            try
            {
                var plan = await _consolidator.PlanAsync(
                    addCandidates.Select(c => c.Item).ToList(), _store, ct).ConfigureAwait(false);
                await _consolidator.ApplyAsync(plan, _store).ConfigureAwait(false);

                // Кандидаты, попавшие в plan.Insert — вставлены; идентифицируем их по
                // ссылке (та же инстанция MemoryItem). Supersede — старый заменён новым
                // (тоже считается «добавлено», с пометкой). Confirm — дубль, пропуск.
                var insertedSet = new HashSet<MemoryItem>(plan.Insert);
                var supersedeNew = new HashSet<MemoryItem>(plan.Supersede.Select(s => s.NewItem));
                foreach (var (idx, item) in addCandidates)
                {
                    if (insertedSet.Contains(item))
                    {
                        applied.Add($"добавлено: {item.Content}");
                        insertedIdx.Add(idx);
                    }
                    else if (supersedeNew.Contains(item))
                    {
                        applied.Add($"уточнено: {item.Content}");
                        insertedIdx.Add(idx);
                    }
                    else
                    {
                        _logger?.Debug($"MemoryWriter: Add-дубликат (consolidator), пропуск: '{Trunc(item.Content)}'", LogCategory);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn($"MemoryWriter: consolidator упал ({ex.Message}) — fallback на точный дедуп", LogCategory);
                insertedIdx = FallbackInsertAdds(addCandidates, applied);
            }
        }
        else if (addCandidates.Count > 0)
        {
            insertedIdx = FallbackInsertAdds(addCandidates, applied);
        }

        try
        {
            foreach (var d in nonAdd)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var desc = ApplyOne(d, meetingId, opts);
                    if (desc != null)
                        applied.Add(desc);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Warn($"MemoryWriter: директива {d.Kind} '{Trunc(d.Old)}' не применена: {ex.Message}", LogCategory);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Error("MemoryWriter.ApplyAsync: ошибка применения директив", ex, LogCategory);
        }
        return applied;
    }

    /// <summary>Вставка Add без консолидатора: пропускаем точные нормализованные
    /// дубли (среди Active/Pending и внутри батча). Возвращает индексы вставленных.</summary>
    private HashSet<int> FallbackInsertAdds(List<(int Index, MemoryItem Item)> candidates,
        List<string> applied)
    {
        var inserted = new HashSet<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var it in _store.ListItems(status: MemoryItemStatus.Active, limit: 5000))
                seen.Add(TextNormalizer.Normalize(it.Content));
            foreach (var it in _store.ListItems(status: MemoryItemStatus.Pending, limit: 5000))
                seen.Add(TextNormalizer.Normalize(it.Content));
        }
        catch { /* стор без выборки — дедупим только внутри батча */ }

        var batch = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (idx, item) in candidates)
        {
            var norm = TextNormalizer.Normalize(item.Content);
            if (norm.Length == 0 || !batch.Add(norm) || seen.Contains(norm))
            {
                _logger?.Debug($"MemoryWriter: Add-дубликат (fallback), пропуск: '{Trunc(item.Content)}'", LogCategory);
                continue;
            }
            _store.InsertItem(item);
            applied.Add($"добавлено: {item.Content}");
            inserted.Add(idx);
        }
        return inserted;
    }

    private string? ApplyOne(MemoryDirective d, long? meetingId, AttachmentApplyOptions? opts)
    {
        switch (d.Kind)
        {
            case MemoryDirectiveKind.Add:
            {
                // Повторный прогон извлечения из документа не должен плодить
                // дубликаты: активный факт с таким же текстом уже есть → пропуск.
                if (opts?.DocumentId != null)
                {
                    var dup = FindActiveMatch(d.Old);
                    if (dup != null &&
                        string.Equals(dup.Content.Trim(), d.Old.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.Debug($"MemoryWriter: Add-дубликат из документа, пропуск: '{Trunc(d.Old)}'", LogCategory);
                        return null;
                    }
                }
                var item = NewFactItem(d.Old, meetingId, opts);
                _store.InsertItem(item);
                return $"добавлено: {d.Old}";
            }
            case MemoryDirectiveKind.Update:
            {
                if (string.IsNullOrWhiteSpace(d.New))
                {
                    _logger?.Warn($"MemoryWriter: UPDATE без нового текста, пропуск: '{Trunc(d.Old)}'", LogCategory);
                    return null;
                }
                var oldItem = FindActiveMatch(d.Old);
                if (oldItem == null)
                {
                    _logger?.Warn($"MemoryWriter: UPDATE — активный факт не найден: '{Trunc(d.Old)}'", LogCategory);
                    return null;
                }
                // Сначала вставляем новый — получаем id для superseded_by-ссылки.
                var newItem = NewFactItem(d.New, meetingId ?? oldItem.SourceMeetingId, opts);
                var newId = _store.InsertItem(newItem);
                _store.SetStatus(oldItem.Id, MemoryItemStatus.Superseded, newId);
                return $"обновлено: {d.New}";
            }
            case MemoryDirectiveKind.Forget:
            {
                var item = FindActiveMatch(d.Old);
                if (item == null)
                {
                    _logger?.Warn($"MemoryWriter: FORGET — активный факт не найден: '{Trunc(d.Old)}'", LogCategory);
                    return null;
                }
                _store.SetStatus(item.Id, MemoryItemStatus.Invalidated);
                return $"удалено: {item.Content}";
            }
            default:
                return null;
        }
    }

    private static MemoryItem NewFactItem(string content, long? meetingId,
        AttachmentApplyOptions? opts = null) => new()
    {
        ScopeType = meetingId != null ? MemoryScopeType.Meeting : MemoryScopeType.Global,
        ScopeId = meetingId,
        Type = MemoryItemType.Fact,
        Content = content,
        Status = opts is { ForcePending: true } ? MemoryItemStatus.Pending : MemoryItemStatus.Active,
        Source = opts?.DocumentId != null ? AttachmentSourceTag : SourceTag,
        SourceMeetingId = meetingId,
        SourceDocumentId = opts?.DocumentId,
        SourceDocName = opts?.DocName,
        SourceDocSha256 = opts?.DocSha256,
        SourceAnchor = opts?.Anchor
    };

    /// <summary>
    /// Ближайший активный item по тексту директивы: FTS при доступности,
    /// иначе ListItems + Contains (SQLite store умеет LIKE-fallback сам, но
    /// контракт IMemoryStore его не гарантирует — страхуемся явно).
    /// </summary>
    private MemoryItem? FindActiveMatch(string query)
    {
        try
        {
            if (_store.FtsAvailable)
            {
                return _store.SearchFts(query, MatchLimit)
                    .Select(h => h.Item)
                    .FirstOrDefault(i => i.Status == MemoryItemStatus.Active);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"MemoryWriter: SearchFts упал, fallback на Contains: {ex.Message}", LogCategory);
        }

        return _store.ListItems(status: MemoryItemStatus.Active)
            .FirstOrDefault(i => i.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string Trunc(string s, int max = 80) =>
        s.Length <= max ? s : s[..max] + "…";
}
