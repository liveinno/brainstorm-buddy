using System.Text;
using System.Text.Json;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Экспорт/импорт долговременной памяти (спека «EXPORT / IMPORT»).
/// JSON-снимок: версия, приложение, проекты и полный набор полей items —
/// ids, scopes, types, timestamps, validity, provenance, status, tags.
/// Markdown — человекочитаемый список, сгруппированный по скоупам.
/// Импорт: дедупликация по id или (text+scope), project-scope перепривязывается
/// по имени проекта (создаётся при отсутствии), person/meeting-scope → Global с тегом 'imported'.
/// </summary>
public static class MemoryTransfer
{
    /// <summary>Маркер приложения в export-конверте — проверка «наш ли это файл».</summary>
    public const string AppId = "BrainstormBuddy";
    public const int CurrentVersion = 1;

    // Печатная форма для файлов — свой инстанс опций (MemoryJson.Default — компактный для БД).
    private static readonly JsonSerializerOptions FileJson = new(MemoryJson.Default)
    {
        WriteIndented = true,
    };

    /// <summary>Экспорт: по расширению .md → Markdown, иначе JSON. Возвращает число фактов.</summary>
    public static int Export(IMemoryStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        var items = store.ListItems(status: null, limit: int.MaxValue);
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, RenderMarkdown(store, items), Encoding.UTF8);
            return items.Count;
        }

        var payload = BuildExport(store, items);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, FileJson), Encoding.UTF8);
        return items.Count;
    }

    /// <summary>
    /// Импорт JSON-экспорта. Возвращает (inserted, skipped-as-duplicates).
    /// Битый/чужой файл → <see cref="InvalidDataException"/>.
    /// </summary>
    public static (int Imported, int Skipped) Import(IMemoryStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        ExportedMemory? payload;
        try { payload = JsonSerializer.Deserialize<ExportedMemory>(File.ReadAllText(path), MemoryJson.Default); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        { throw new InvalidDataException("Not a BrainstormBuddy memory export", ex); }

        if (payload?.Items == null || payload.App != AppId || payload.Version < 1)
            throw new InvalidDataException("Not a BrainstormBuddy memory export");

        // Существующие: id → id и (scope+text) → id — обе ветки нужны для skip и ремапа superseded_by.
        var existingIds = new HashSet<long>();
        var existingKeys = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var it in store.ListItems(status: null, limit: int.MaxValue))
        {
            existingIds.Add(it.Id);
            existingKeys[DedupeKey(it.ScopeType, it.ScopeId, it.Content)] = it.Id;
        }

        // Проекты перепривязываются по имени (id из файла чужие); отсутствующие создаются.
        var projectsByName = store.ListProjects(includeArchived: true)
            .ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var exportedProjects = (payload.Projects ?? new List<ExportedProject>())
            .Where(p => p.Id.HasValue)
            .ToDictionary(p => p.Id!.Value, p => p);
        var projectIdMap = new Dictionary<long, long>(); // exported id → local id
        var newIds = new Dictionary<long, long>();       // exported item id → local item id

        var imported = 0;
        var skipped = 0;
        foreach (var e in payload.Items)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) { skipped++; continue; }
            if (e.Id.HasValue && existingIds.Contains(e.Id.Value)) { skipped++; continue; }

            var (scopeType, scopeId, fallback) = ResolveScope(e, projectIdMap, exportedProjects, projectsByName, store);
            var key = DedupeKey(scopeType, scopeId, e.Text);
            if (existingKeys.TryGetValue(key, out var dupId))
            {
                if (e.Id.HasValue) newIds[e.Id.Value] = dupId; // superseded_by на дубль → на существующий
                skipped++;
                continue;
            }

            var item = new MemoryItem
            {
                ScopeType = scopeType,
                ScopeId = scopeId,
                Type = e.Type,
                Content = e.Text.Trim(),
                Importance = Math.Clamp(e.Importance, 0, 1),
                Confidence = Math.Clamp(e.Confidence, 0, 1),
                Pinned = e.Pinned,
                Status = e.Status == MemoryItemStatus.Pending ? MemoryItemStatus.Active : e.Status,
                ValidFromUtc = e.Timestamps?.ValidFromUtc ?? DateTime.UtcNow,
                ValidUntilUtc = e.Timestamps?.ValidUntilUtc,
                CreatedAtUtc = e.Timestamps?.CreatedAtUtc ?? DateTime.UtcNow,
                LastConfirmedAtUtc = e.Timestamps?.LastConfirmedAtUtc,
                SourceMeetingId = e.Source?.MeetingId,
                SourceSegmentId = e.Source?.SegmentId,
                SourceStartMs = e.Source?.StartMs,
                SourceEndMs = e.Source?.EndMs,
                Speaker = e.Source?.Speaker,
                Source = "imported",
            };
            var newId = store.InsertItem(item);
            // NB: новый id в existingIds НЕ добавляем — локальный id может совпасть
            // с чужим exported id следующей записи файла → ложный skip (round-trip в пустую БД).
            existingKeys[key] = newId;
            if (e.Id.HasValue) newIds[e.Id.Value] = newId;
            if (fallback) store.AddTag(newId, "imported");
            foreach (var tag in e.Tags ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(tag)) store.AddTag(newId, tag.Trim());
            imported++;
        }

        // Второй проход: superseded_by ссылается на ids из файла — ремапим на новые/существующие.
        foreach (var e in payload.Items)
        {
            if (e.Id is not { } oldId || e.SupersededBy is not { } oldSup) continue;
            if (!newIds.TryGetValue(oldId, out var localId) || !newIds.TryGetValue(oldSup, out var localSup))
                continue;
            var cur = store.GetItem(localId);
            if (cur == null || cur.SupersededById == localSup) continue;
            cur.SupersededById = localSup;
            store.UpdateItem(cur);
        }

        return (imported, skipped);
    }

    // ---------------- export helpers ----------------

    private static ExportedMemory BuildExport(IMemoryStore store, List<MemoryItem> items)
    {
        var projectNames = store.ListProjects(includeArchived: true).ToDictionary(p => p.Id, p => p.Name);
        var personNames = store.ListPeople().ToDictionary(p => p.Id, p => p.Name);
        var meetingTitles = store.ListMeetings(limit: int.MaxValue).ToDictionary(m => m.Id, m => m.Title);

        return new ExportedMemory
        {
            Projects = store.ListProjects(includeArchived: true).Select(p => new ExportedProject
            {
                Id = p.Id, Name = p.Name, Description = p.Description,
                Archived = p.Archived, CreatedAtUtc = p.CreatedAtUtc,
            }).ToList(),
            Items = items.Select(it => new ExportedItem
            {
                Id = it.Id,
                Scope = new ExportedScope
                {
                    Type = it.ScopeType,
                    Id = it.ScopeId,
                    Name = it.ScopeType switch // имя — для перепривязки scope при импорте на чужой БД
                    {
                        MemoryScopeType.Project => it.ScopeId is { } pid && projectNames.TryGetValue(pid, out var pn) ? pn : null,
                        MemoryScopeType.Person => it.ScopeId is { } pid && personNames.TryGetValue(pid, out var pn) ? pn : null,
                        MemoryScopeType.Meeting => it.ScopeId is { } mid && meetingTitles.TryGetValue(mid, out var mt) ? mt : null,
                        _ => null,
                    },
                },
                Type = it.Type,
                Text = it.Content,
                Importance = it.Importance,
                Confidence = it.Confidence,
                Pinned = it.Pinned,
                Status = it.Status,
                SupersededBy = it.SupersededById,
                Origin = it.Source,
                Timestamps = new ExportedTimestamps
                {
                    ValidFromUtc = it.ValidFromUtc,
                    ValidUntilUtc = it.ValidUntilUtc,
                    CreatedAtUtc = it.CreatedAtUtc,
                    UpdatedAtUtc = it.UpdatedAtUtc,
                    LastConfirmedAtUtc = it.LastConfirmedAtUtc,
                },
                Source = new ExportedSource
                {
                    MeetingId = it.SourceMeetingId,
                    SegmentId = it.SourceSegmentId,
                    StartMs = it.SourceStartMs,
                    EndMs = it.SourceEndMs,
                    Speaker = it.Speaker,
                },
                Tags = store.GetTags(it.Id),
            }).ToList(),
        };
    }

    private static string RenderMarkdown(IMemoryStore store, List<MemoryItem> items)
    {
        var projectNames = store.ListProjects(includeArchived: true).ToDictionary(p => p.Id, p => p.Name);
        var personNames = store.ListPeople().ToDictionary(p => p.Id, p => p.Name);
        var meetingTitles = store.ListMeetings(limit: int.MaxValue).ToDictionary(m => m.Id, m => m.Title);

        string ScopeName(MemoryItem it) => it.ScopeType switch
        {
            MemoryScopeType.Global => "Global",
            MemoryScopeType.Project => $"Project: {(it.ScopeId is { } pid && projectNames.TryGetValue(pid, out var n) ? n : $"#{it.ScopeId}")}",
            MemoryScopeType.Person => $"Person: {(it.ScopeId is { } pid && personNames.TryGetValue(pid, out var n) ? n : $"#{it.ScopeId}")}",
            MemoryScopeType.Meeting => $"Meeting: {(it.ScopeId is { } mid && meetingTitles.TryGetValue(mid, out var t) ? t : $"#{it.ScopeId}")}",
            _ => it.ScopeType.ToString(),
        };

        var sb = new StringBuilder();
        sb.AppendLine("# Brainstorm Buddy — Memory export");
        sb.AppendLine();
        sb.AppendLine($"_Exported {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · {items.Count} items_");
        foreach (var g in items.GroupBy(ScopeName).OrderBy(g => g.Key == "Global" ? "" : g.Key))
        {
            sb.AppendLine();
            sb.AppendLine($"## {g.Key}");
            foreach (var it in g.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Importance))
            {
                var text = it.Content.Replace("\r", " ").Replace('\n', ' ').Trim();
                var line = $"- {(it.Pinned ? "★ " : "")}**[{it.Type}]** {text} " +
                           $"_(importance {it.Importance:0%} · {it.Status} · {it.UpdatedAtUtc:yyyy-MM-dd})_";
                // вычеркнутый текст для устаревших версий — читается и в сыром md
                sb.AppendLine(it.Status is MemoryItemStatus.Superseded or MemoryItemStatus.Invalidated
                    ? $"~~{line}~~" : line);
            }
        }
        return sb.ToString();
    }

    // ---------------- import helpers ----------------

    /// <summary>
    /// Ремап scope: Global — как есть; Project — по имени (создать при отсутствии);
    /// Person/Meeting — кросс-БД id бессмысленны → Global + тег 'imported' (fallback=true).
    /// </summary>
    private static (MemoryScopeType Type, long? Id, bool Fallback) ResolveScope(
        ExportedItem e,
        Dictionary<long, long> projectIdMap,
        Dictionary<long, ExportedProject> exportedProjects,
        Dictionary<string, long> projectsByName,
        IMemoryStore store)
    {
        switch (e.Scope?.Type)
        {
            case MemoryScopeType.Project when e.Scope.Name is { Length: > 0 } name:
            {
                if (e.Scope.Id is { } oldPid && projectIdMap.TryGetValue(oldPid, out var mapped))
                    return (MemoryScopeType.Project, mapped, false);
                if (!projectsByName.TryGetValue(name, out var pid))
                {
                    var src = e.Scope.Id is { } eid && exportedProjects.TryGetValue(eid, out var ep) ? ep : null;
                    pid = store.UpsertProject(new MemoryProject
                    {
                        Name = name,
                        Description = src?.Description ?? "",
                        Archived = src?.Archived ?? false,
                    });
                    projectsByName[name] = pid;
                }
                if (e.Scope.Id is { } eid2) projectIdMap[eid2] = pid;
                return (MemoryScopeType.Project, pid, false);
            }
            case MemoryScopeType.Project: // id без имени — привязать некуда
            case MemoryScopeType.Person:
            case MemoryScopeType.Meeting:
                return (MemoryScopeType.Global, null, true);
            default:
                return (MemoryScopeType.Global, null, false);
        }
    }

    private static string DedupeKey(MemoryScopeType scope, long? scopeId, string text)
        => $"{scope}:{scopeId?.ToString() ?? "-"}:{text.Trim()}";

    // ---------------- DTO (схема файла) ----------------

    public sealed class ExportedMemory
    {
        public int Version { get; set; } = CurrentVersion;
        public string App { get; set; } = AppId;
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
        public List<ExportedProject>? Projects { get; set; }
        public List<ExportedItem>? Items { get; set; }
    }

    public sealed class ExportedProject
    {
        public long? Id { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Archived { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public sealed class ExportedItem
    {
        public long? Id { get; set; }
        public ExportedScope? Scope { get; set; }
        public MemoryItemType Type { get; set; } = MemoryItemType.Fact;
        public string Text { get; set; } = "";
        public double Importance { get; set; } = 0.5;
        public double Confidence { get; set; } = 0.5;
        public bool Pinned { get; set; }
        public MemoryItemStatus Status { get; set; } = MemoryItemStatus.Active;
        public long? SupersededBy { get; set; }
        public string Origin { get; set; } = "extraction";
        public ExportedTimestamps? Timestamps { get; set; }
        public ExportedSource? Source { get; set; }
        public List<string>? Tags { get; set; }
    }

    public sealed class ExportedScope
    {
        public MemoryScopeType Type { get; set; } = MemoryScopeType.Global;
        public long? Id { get; set; }
        public string? Name { get; set; }
    }

    public sealed class ExportedTimestamps
    {
        public DateTime ValidFromUtc { get; set; }
        public DateTime? ValidUntilUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? LastConfirmedAtUtc { get; set; }
    }

    public sealed class ExportedSource
    {
        public long? MeetingId { get; set; }
        public long? SegmentId { get; set; }
        public long? StartMs { get; set; }
        public long? EndMs { get; set; }
        public string? Speaker { get; set; }
    }
}
