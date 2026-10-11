using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Применяет memory-директивы из ответа LLM (<see cref="MemoryDirectiveParser"/>)
/// к <see cref="IMemoryStore"/>. Add → вставка fact-item'а (scope Meeting при
/// наличии meetingId, иначе Global); Update → supersession-цепочка (старый item →
/// Superseded, новый → Active); Forget → Invalidate ближайшего активного item'а.
/// Ошибки одной директивы не роняют остальные; не-применённые уходят в Warn-лог.
/// </summary>
public sealed class MemoryWriter
{
    private const string LogCategory = "Memory";
    private const string SourceTag = "llm-directive";
    private const int MatchLimit = 5;

    private readonly IMemoryStore _store;
    private readonly LoggingService? _logger;

    public MemoryWriter(IMemoryStore store, LoggingService? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>
    /// Применить директивы по порядку. Возвращает человекочитаемые описания
    /// фактически применённых операций (для тоста/лога).
    /// </summary>
    public Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<MemoryDirective> dirs,
        long? meetingId, CancellationToken ct)
    {
        var applied = new List<string>();
        if (dirs is not { Count: > 0 })
            return Task.FromResult<IReadOnlyList<string>>(applied);

        try
        {
            foreach (var d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var desc = ApplyOne(d, meetingId);
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
        return Task.FromResult<IReadOnlyList<string>>(applied);
    }

    private string? ApplyOne(MemoryDirective d, long? meetingId)
    {
        switch (d.Kind)
        {
            case MemoryDirectiveKind.Add:
            {
                var item = NewFactItem(d.Old, meetingId);
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
                var newItem = NewFactItem(d.New, meetingId ?? oldItem.SourceMeetingId);
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

    private static MemoryItem NewFactItem(string content, long? meetingId) => new()
    {
        ScopeType = meetingId != null ? MemoryScopeType.Meeting : MemoryScopeType.Global,
        ScopeId = meetingId,
        Type = MemoryItemType.Fact,
        Content = content,
        Status = MemoryItemStatus.Active,
        Source = SourceTag,
        SourceMeetingId = meetingId
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
