namespace BrainstormBuddy.Memory;

/// <summary>Скоуп памяти: Global / Project / Person / Meeting.</summary>
public enum MemoryScopeType { Global, Project, Person, Meeting }

/// <summary>Типы долговременных memory items (минимальный набор по спеке).</summary>
public enum MemoryItemType
{
    Decision, ActionItem, Fact, Preference, Constraint, Deadline,
    OpenQuestion, Risk, Idea, Correction, PersonFact, ProjectFact
}

/// <summary>Lifecycle: Pending → Active → (Superseded | Invalidated). Hard delete — отдельная операция.</summary>
public enum MemoryItemStatus { Pending, Active, Superseded, Invalidated }

/// <summary>Режим сохранения встречи.</summary>
public enum MemorySaveMode { Off, SummaryOnly, SmartMemory, FullArchive }

/// <summary>Ревью извлечённых memories перед записью.</summary>
public enum MemoryReviewMode { Automatic, Review, Manual }

/// <summary>Проект — лёгкая группировка встреч/фактов.</summary>
public sealed class MemoryProject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool Archived { get; set; }
    public override string ToString() => Name;
}

/// <summary>Персона — только ручные алиасы (S1→«Иван» per-meeting), без биометрии.</summary>
public sealed class MemoryPerson
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public override string ToString() => Name;
}

/// <summary>Встреча = сессия звонка. Подпитывается из MeetingTranscriptStore (jsonl).</summary>
public sealed class MemoryMeeting
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public long? ProjectId { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAtUtc { get; set; }
    public string MemoryMode { get; set; } = "smart";      // off|summary|smart|full
    public bool UseMemory { get; set; }                  // читать прошлую память в этом звонке
    public bool WriteMemory { get; set; }                // записывать эту встречу
    public string Summary { get; set; } = "";
    public string JournalPath { get; set; } = "";        // meetings/<id>.jsonl из MeetingTranscriptStore
    public string Agenda { get; set; } = "";             // повестка из Pre-call Brief → <memory_context>
}

/// <summary>Долговременный факт с temporal validity и provenance.</summary>
public sealed class MemoryItem
{
    public long Id { get; set; }
    public MemoryScopeType ScopeType { get; set; } = MemoryScopeType.Global;
    public long? ScopeId { get; set; }                   // null для Global
    public MemoryItemType Type { get; set; } = MemoryItemType.Fact;
    public string Content { get; set; } = "";
    public double Importance { get; set; } = 0.5;        // 0..1
    public double Confidence { get; set; } = 0.5;        // 0..1
    public bool Pinned { get; set; }
    public MemoryItemStatus Status { get; set; } = MemoryItemStatus.Active;
    public DateTime ValidFromUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ValidUntilUtc { get; set; }         // null = актуален
    public long? SupersededById { get; set; }
    // Provenance — обязателен для extracted items
    public long? SourceMeetingId { get; set; }
    public long? SourceSegmentId { get; set; }
    public long? SourceStartMs { get; set; }
    public long? SourceEndMs { get; set; }
    public string? Speaker { get; set; }
    public string Source { get; set; } = "extraction";   // extraction|user|user-edited
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastConfirmedAtUtc { get; set; }
}

/// <summary>Сегмент транскрипта встречи (зеркало jsonl-реплик + timing).</summary>
public sealed class MemorySegment
{
    public long Id { get; set; }
    public long MeetingId { get; set; }
    public long Seq { get; set; }
    public string Role { get; set; } = "";               // question|user|answer
    public string Text { get; set; } = "";
    public string? Speaker { get; set; }                 // "S1" — внутри встречи
    public long? StartMs { get; set; }
    public long? EndMs { get; set; }
    public DateTime TsUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Фоновая задача memory-пайплайна (переживает рестарт).</summary>
public sealed class MemoryJob
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";               // extract|embed|retention|reembed
    public string PayloadJson { get; set; } = "";
    public string Status { get; set; } = "pending";      // pending|running|completed|failed
    public int Attempts { get; set; }
    public DateTime? RunAfterUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? Error { get; set; }
}

/// <summary>Один hit из recall с объяснением (почему достали).</summary>
public sealed record MemoryRecallHit(MemoryItem Item, double Score, string Why = "");

/// <summary>Параметры recall-запроса.</summary>
public sealed record MemoryRecallQuery(
    string Text,
    MemoryScopeType? Scope = null,
    long? ScopeId = null,
    long? MeetingId = null,
    int MaxItems = 8,
    int MaxTokens = 2000,
    bool IncludeSuperseded = false,
    bool IncludeHistorical = false);

/// <summary>Сводка для диагностики/health.</summary>
public sealed record MemoryStats(
    long Meetings, long Items, long Active, long Superseded,
    long PendingJobs, long DbBytes, bool FtsAvailable);

/// <summary>Результат extraction: саммари + кандидаты.</summary>
public sealed record MemoryExtractionResult(
    string Summary, List<MemoryItem> Candidates, string? Error);

/// <summary>План консолидации: что вставить / что заменить / что подтвердить.</summary>
public sealed record ConsolidationPlan(
    List<MemoryItem> Insert,
    List<(long ExistingId, MemoryItem NewItem)> Supersede,
    List<long> ConfirmIds);
