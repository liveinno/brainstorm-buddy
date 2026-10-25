using System.Text.Json;
using System.Text.Json.Serialization;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Media;
using BrainstormBuddy.Stt;
using BrainstormBuddy.Core.Mcp.Outgoing;
using BrainstormBuddy.Transcription;

namespace BrainstormBuddy.Core.Mcp.Jobs;

/// <summary>
/// Контекст исполнения MCP-job исполнителем kind'а. Прогресс «N из M» и
/// чекпоинты пишутся в McpJob напрямую; SaveAsync персистит их в стор.
/// </summary>
public sealed class McpJobRunContext
{
    /// <summary>Текущая запись job'а (ArgsJson/Checkpoint мутируются вручную).</summary>
    public required McpJob Job { get; init; }

    /// <summary>Прогресс: (stage, done, total) — «Изучено 18 из 46 встреч».
    /// Процент вычислять только при известном total.</summary>
    public required Action<string?, int?, int?> ReportProgress { get; init; }

    /// <summary>Сохранить текущее состояние job'а (в т.ч. Checkpoint) в стор.</summary>
    public required Func<Task> SaveAsync { get; init; }

    /// <summary>Лог-строка для журнала приложения (best-effort).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>Результат работы исполнителя kind'а.</summary>
public sealed record McpJobExecutorResult(
    bool Success,
    string? ResultJson = null,
    string? Error = null);

/// <summary>
/// Исполнитель одного kind'а MCP-job (transcribe/extract_facts/…). Ошибка одного
/// источника НЕ должна ронять job — skip + счётчик в прогрессе/результате.
/// Необработанное исключение = Failed.
/// </summary>
public interface IMcpJobExecutor
{
    /// <summary>Обслуживаемый kind («transcribe», «extract_facts», …).</summary>
    string Kind { get; }

    Task<McpJobExecutorResult> RunAsync(McpJobRunContext ctx, CancellationToken ct);
}


/// <summary>ArgsJson задачи kind="transcribe".</summary>
public sealed record TranscribeJobArgs(
    [property: JsonPropertyName("filePath")] string? FilePath = null,
    [property: JsonPropertyName("importId")] string? ImportId = null,
    [property: JsonPropertyName("language")] string? Language = null);

/// <summary>
/// Исполнитель kind="transcribe": аудиодорожка файла → 16кГц mono
/// (MediaAudioExtractor) → IFileTranscriber (GigaAM/Whisper) → текстовый
/// транскрипт. Результат — transcriptRef на сохранённый .txt + сводка.
/// </summary>
public sealed class TranscribeJobExecutor : IMcpJobExecutor
{
    /// <summary>Резолвер importId → путь файла. Провайдит интегратор из пайплайна импорта.</summary>
    private readonly Func<string, CancellationToken, Task<string?>>? _resolveImport;
    private readonly IFileTranscriber _transcriber;
    private readonly MediaAudioExtractor _extractor;
    private readonly string _outDir;

    /// <param name="transcriber">Движок файловой транскрибации (GigaamFileTranscriber/Whisper).</param>
    /// <param name="outDir">Каталог результатов (например %AppData%\BrainstormBuddy\mcp\transcripts).</param>
    /// <param name="resolveImport">importId → исходный путь; null → importId не поддержан.</param>
    /// <param name="extractor">Аудио-экстрактор (null → новый MediaAudioExtractor).</param>
    public TranscribeJobExecutor(IFileTranscriber transcriber, string outDir,
        Func<string, CancellationToken, Task<string?>>? resolveImport = null,
        MediaAudioExtractor? extractor = null)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _outDir = outDir ?? throw new ArgumentNullException(nameof(outDir));
        _resolveImport = resolveImport;
        _extractor = extractor ?? new MediaAudioExtractor();
    }

    public string Kind => "transcribe";

    public async Task<McpJobExecutorResult> RunAsync(McpJobRunContext ctx, CancellationToken ct)
    {
        var args = Parse<TranscribeJobArgs>(ctx.Job.ArgsJson) ?? new TranscribeJobArgs();
        var path = args.FilePath;
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(args.ImportId))
        {
            if (_resolveImport == null)
                return new McpJobExecutorResult(false, null,
                    "importId не поддержан: резолвер импорта не подключён");
            path = await _resolveImport(args.ImportId, ct).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new McpJobExecutorResult(false, null,
                $"файл не найден: {path ?? args.ImportId ?? "<пусто>"}");

        var cp = Parse<McpJobCheckpoint>(ctx.Job.Checkpoint) ?? new McpJobCheckpoint();
        ctx.ReportProgress("Извлечение аудиодорожки", 0, 3);
        cp.Stage = "audio";
        ctx.Job.Checkpoint = JsonSerializer.Serialize(cp, JsonOpts);
        await ctx.SaveAsync().ConfigureAwait(false);

        var audio = _extractor.Extract(path, ct);
        ctx.ReportProgress("Распознавание", 1, 3);
        cp.Stage = "stt";
        ctx.Job.Checkpoint = JsonSerializer.Serialize(cp, JsonOpts);
        await ctx.SaveAsync().ConfigureAwait(false);
        var result = await Task.Run(
            () => _transcriber.Transcribe(audio.Samples, audio.Duration, audio.Method,
                (frac, status) => ctx.ReportProgress(status, (int)(frac * 100), 100), ct),
            ct).ConfigureAwait(false);

        Directory.CreateDirectory(_outDir);
        var outPath = Path.Combine(_outDir, $"{ctx.Job.Id}.txt");
        await File.WriteAllTextAsync(outPath, result.TimestampedText,
            new System.Text.UTF8Encoding(true), ct).ConfigureAwait(false);

        cp.Stage = "done";
        cp.ProcessedIds.Add(path);
        cp.NextCursor = null;
        ctx.Job.Checkpoint = JsonSerializer.Serialize(cp, JsonOpts);
        ctx.ReportProgress("Готово", 3, 3);
        await ctx.SaveAsync().ConfigureAwait(false);

        return new McpJobExecutorResult(true, JsonSerializer.Serialize(new
        {
            transcriptRef = outPath,
            engine = _transcriber.EngineName,
            durationSec = (int)result.Duration.TotalSeconds,
            segments = result.Segments.Count,
            chars = result.TimestampedText?.Length ?? 0,
            sourcePath = path,
        }, JsonOpts));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static T? Parse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch (JsonException) { return default; }
    }
}

/// <summary>ArgsJson задачи kind="extract_facts".</summary>
public sealed record ExtractFactsJobArgs(
    [property: JsonPropertyName("meetingId")] string? MeetingId = null,
    [property: JsonPropertyName("all")] bool All = false,
    [property: JsonPropertyName("reviewMode")] string? ReviewMode = null);

/// <summary>Снимок настроек памяти для FactJob (провайдит интегратор из конфига).</summary>
public sealed record FactJobSettings(string ProviderId, string Model, string Scope, string VerifyMode);

/// <summary>
/// Исполнитель kind="extract_facts": для каждого документа встречи(-ей) гонит
/// реальный AttachmentFactExtractor (части транскрипта/документа, дедуп фактов в
/// MemoryWriter). Ошибка одного источника — skip + count, job не падает.
/// </summary>
public sealed class ExtractFactsJobExecutor : IMcpJobExecutor
{
    private readonly AttachmentFactExtractor _extractor;
    private readonly AttachmentStore _attachments;
    private readonly MeetingTranscriptStore _meetings;
    private readonly Func<FactJobSettings>? _settings;

    public ExtractFactsJobExecutor(AttachmentFactExtractor extractor,
        AttachmentStore attachments, MeetingTranscriptStore meetings,
        Func<FactJobSettings>? settings = null)
    {
        _extractor = extractor;
        _attachments = attachments;
        _meetings = meetings;
        _settings = settings;
    }

    public string Kind => "extract_facts";

    public async Task<McpJobExecutorResult> RunAsync(McpJobRunContext ctx, CancellationToken ct)
    {
        var args = Parse<ExtractFactsJobArgs>(ctx.Job.ArgsJson) ?? new ExtractFactsJobArgs();
        var cp = Parse<McpJobCheckpoint>(ctx.Job.Checkpoint) ?? new McpJobCheckpoint { Stage = "extract_facts" };
        var done = new HashSet<string>(cp.ProcessedIds, StringComparer.Ordinal);

        // Целевые документы: meetingId → его вложения; all → все встречи журнала.
        var targets = new List<(StoredDocument Doc, string MeetingId)>();
        if (!string.IsNullOrWhiteSpace(args.MeetingId))
        {
            foreach (var ma in _attachments.ListMeetingDocuments(args.MeetingId))
                targets.Add((ma.Doc, args.MeetingId));
        }
        else if (args.All)
        {
            foreach (var m in _meetings.LoadAll())
            {
                ct.ThrowIfCancellationRequested();
                foreach (var ma in _attachments.ListMeetingDocuments(m.Id))
                    targets.Add((ma.Doc, m.Id));
            }
        }
        if (targets.Count == 0)
            return new McpJobExecutorResult(false, null,
                "нет документов для извлечения (привяжите файлы к встрече)");

        var total = targets.Count;
        var processed = 0;
        var skipped = done.Count;
        var errors = 0;
        var factsSaved = 0;
        var factsPending = 0;
        var settings = _settings?.Invoke()
            ?? new FactJobSettings("", "", "meeting", "commit");
        ctx.ReportProgress($"Изучено {skipped} из {total} документов", skipped, total);

        foreach (var (doc, meetingId) in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (done.Contains(doc.DocumentId)) continue; // чекпоинт: уже обработан

            try
            {
                var fj = new FactJob(0, doc.DocumentId, meetingId,
                    FactJobStatus.Queued, 0, 0, 0, 0, 0, null,
                    settings.ProviderId, settings.Model, settings.Scope,
                    settings.VerifyMode, DateTime.UtcNow);
                var id = await _attachments.EnqueueFactJobAsync(fj).ConfigureAwait(false);
                var final = await _extractor.RunAsync(fj with { Id = id },
                    _attachments, memMeetingId: null, reviewMode: args.ReviewMode, ct)
                    .ConfigureAwait(false);

                if (final.Status is FactJobStatus.Done or FactJobStatus.NoFacts)
                {
                    factsSaved += final.FactsSaved;
                    factsPending += final.FactsPending;
                    cp.Results[doc.DocumentId] = JsonSerializer.Serialize(new
                    {
                        doc.DocumentId, meetingId,
                        status = final.Status.ToString(),
                        final.FactsSaved, final.FactsPending,
                    }, JsonOpts);
                }
                else
                {
                    errors++;
                    ctx.Log?.Invoke($"extract_facts {doc.DocumentId}: {final.Error}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Один источник не роняет job: считаем и идём дальше.
                errors++;
                ctx.Log?.Invoke($"extract_facts {doc.DocumentId}: {ex.Message}");
            }

            done.Add(doc.DocumentId);
            processed++;
            cp.ProcessedIds = done.ToList();
            ctx.Job.Checkpoint = JsonSerializer.Serialize(cp, JsonOpts);
            ctx.ReportProgress(
                $"Изучено {skipped + processed} из {total} документов",
                skipped + processed, total);
            await ctx.SaveAsync().ConfigureAwait(false);
        }

        return new McpJobExecutorResult(true, JsonSerializer.Serialize(new
        {
            documents = processed,
            skipped,
            errors,
            factsSaved,
            factsPending,
            total,
        }, JsonOpts));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static T? Parse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch (JsonException) { return default; }
    }
}
