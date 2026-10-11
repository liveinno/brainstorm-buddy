using System.Globalization;
using System.Text;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Extraction;

/// <summary>
/// Memory LLM: по сегментам встречи собирает транскрипт с тайм-кодами,
/// просит строгий JSON (саммари + memories) и мапит ответ в MemoryItem-кандидатов.
/// Broken JSON → ровно ОДИН retry с <see cref="MemoryPrompts.Repair"/>; повторный
/// фейл или AskResult.Error → результат с Error, исключения наружу не летят
/// (memory-ошибки не должны ломать звонок). Фильтрация мусорных реплик — на стороне
/// LLM через системный промпт: сюда подаётся ПОЛНЫЙ транскрипт.
/// </summary>
public sealed class MemoryExtractor : IMemoryExtractionService
{
    private const string LogCategory = "Memory";

    // Бюджет ответа: summary + десятки items. Верхняя граница как у MeetingSummarizer.
    private const int ExtractMaxTokens = 4000;
    private const int RepairMaxTokens = 4000;

    private readonly IApiClient _api;
    private readonly LoggingService? _logger;

    public MemoryExtractor(IApiClient api, LoggingService? logger = null)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<MemoryExtractionResult> ExtractAsync(MemoryMeeting m,
        List<MemorySegment> segs, CancellationToken ct)
    {
        try
        {
            var transcript = BuildTranscript(segs);
            if (transcript.Length == 0)
                return new MemoryExtractionResult("", new List<MemoryItem>(), "пустой транскрипт");

            var userText = $"ТРАНСКРИПТ ВСТРЕЧИ{(string.IsNullOrWhiteSpace(m.Title) ? "" : $" «{m.Title}»")}:\n\n" + transcript;

            // Фоновый пайплайн: баннер «LLM недоступен» юзеру не нужен, но
            // медленной локальной модели нужен дедлайн длиннее 30с дефолта.
            var resp = await _api.AskAsync(userText, MemoryPrompts.Extract, ExtractMaxTokens,
                new List<ChatMessage>(), ct, background: true, requestTimeout: TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            if (resp.Canceled) ct.ThrowIfCancellationRequested();
            if (resp.Error != null)
                return Fail($"memory model: {resp.Error}");

            var parsed = ExtractionJson.Parse(resp.Content);
            if (!parsed.Ok)
            {
                // 1 retry с repair-инструкцией; повторный фейл → Error (спека).
                var repair = await _api.AskAsync(resp.Content ?? "", MemoryPrompts.Repair,
                    RepairMaxTokens, new List<ChatMessage>(), ct, background: true, requestTimeout: TimeSpan.FromSeconds(120)).ConfigureAwait(false);
                if (repair.Canceled) ct.ThrowIfCancellationRequested();
                if (repair.Error != null)
                    return Fail($"memory model (repair): {repair.Error}");
                parsed = ExtractionJson.Parse(repair.Content);
                if (!parsed.Ok)
                    return Fail($"extraction JSON не читается после repair: {parsed.Error}");
            }

            var candidates = ToCandidates(parsed.Memories, m.Id);
            _logger?.Info($"Memory extraction done: meeting={m.Id}, items={candidates.Count}", LogCategory);
            return new MemoryExtractionResult(parsed.Summary, candidates, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Error("Memory extraction failed", ex, LogCategory);
            return Fail(ex.Message);
        }

        MemoryExtractionResult Fail(string error)
        {
            _logger?.Warn($"Memory extraction error: {error}", LogCategory);
            return new MemoryExtractionResult("", new List<MemoryItem>(), error);
        }
    }

    /// <summary>Транскрипт вида «[S1 00:12:03] текст»; без метки спикера — Role.</summary>
    internal static string BuildTranscript(List<MemorySegment> segs)
    {
        var sb = new StringBuilder(segs.Count * 80);
        foreach (var s in segs)
        {
            var text = (s.Text ?? "").Replace("\r", " ").Replace('\n', ' ').Trim();
            // Маркер источника OCR ([screen]) — служебная метка, а не часть реплики:
            // из промпта убираем, чтобы не утекала в контент фактов.
            if (text.StartsWith("[screen]", StringComparison.OrdinalIgnoreCase))
                text = text["[screen]".Length..].TrimStart();
            if (text.Length == 0) continue;
            var who = string.IsNullOrWhiteSpace(s.Speaker)
                ? (string.IsNullOrWhiteSpace(s.Role) ? "S?" : s.Role)
                : s.Speaker!.Trim();
            var stamp = s.StartMs is { } ms ? FormatMs(ms) : "--:--:--";
            sb.Append('[').Append(who).Append(' ').Append(stamp).Append("] ").Append(text).Append('\n');
        }
        return sb.ToString();
    }

    private static string FormatMs(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.Hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes:00}:{t.Seconds:00}");
    }

    /// <summary>DTO → MemoryItem-кандидаты; кладём provenance (meeting/ms/speaker).</summary>
    internal static List<MemoryItem> ToCandidates(List<ParsedMemory> memories, long meetingId)
    {
        var list = new List<MemoryItem>(memories.Count);
        foreach (var p in memories)
        {
            var content = (p.Content ?? "").Trim();
            if (content.Length == 0) continue;                 // пустой факт не сохраняем
            if (p.SourceStartMs == null && p.SourceEndMs == null)
                continue;                                      // provenance обязателен
            list.Add(new MemoryItem
            {
                ScopeType = MemoryScopeType.Global,
                Type = MapType(p.Type),
                Content = content,
                Importance = Clamp01(p.Importance ?? 0.5),
                Confidence = Clamp01(p.Confidence ?? 0.5),
                Status = MemoryItemStatus.Active,
                Source = "extraction",
                SourceMeetingId = meetingId,
                SourceStartMs = p.SourceStartMs,
                SourceEndMs = p.SourceEndMs,
                Speaker = string.IsNullOrWhiteSpace(p.SpeakerId) ? null : p.SpeakerId.Trim()
            });
        }
        return list;
    }

    /// <summary>Тип из JSON → MemoryItemType; русские/английские алиасы, fallback Fact.</summary>
    internal static MemoryItemType MapType(string? type)
    {
        switch ((type ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_'))
        {
            case "decision":
            case "решение":
                return MemoryItemType.Decision;
            case "action":
            case "action_item":
            case "actionitem":
            case "todo":
            case "task":
            case "задача":
            case "действие":
                return MemoryItemType.ActionItem;
            case "deadline":
            case "дедлайн":
            case "срок":
                return MemoryItemType.Deadline;
            case "open_question":
            case "openquestion":
            case "question":
            case "вопрос":
            case "открытый_вопрос":
                return MemoryItemType.OpenQuestion;
            case "risk":
            case "риск":
                return MemoryItemType.Risk;
            case "person":
            case "person_fact":
            case "personfact":
            case "персона":
            case "человек":
                return MemoryItemType.PersonFact;
            case "project":
            case "project_fact":
            case "projectfact":
            case "проект":
                return MemoryItemType.ProjectFact;
            case "preference":
            case "предпочтение":
                return MemoryItemType.Preference;
            case "constraint":
            case "ограничение":
                return MemoryItemType.Constraint;
            case "idea":
            case "идея":
                return MemoryItemType.Idea;
            case "correction":
            case "уточнение":
            case "исправление":
                return MemoryItemType.Correction;
            default:
                return MemoryItemType.Fact;   // fact | факт | неизвестное
        }
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
