using System.Text;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Memory;

/// <summary>Граница транскрипта для посылки в LLM: текст части с весом для прогресса.</summary>
public sealed record TranscriptChunk(int Index, string Text)
{
    /// <summary>Вес части для взвешенного прогресса (длина текста в символах).</summary>
    public int CharCount => Text.Length;
}

/// <summary>Один извлечённый факт-кандидат (всегда MEMORY_ADD) с привязкой к части.</summary>
public sealed record FactCandidate(MemoryDirective Directive, int PartIndex, string? TimeAnchor)
{
    /// <summary>Текст факта (тело директивы ADD).</summary>
    public string Text => Directive.Old;
}

/// <summary>Исход всего прогона извлечения по транскрипту.</summary>
public enum ExtractionOutcome
{
    /// <summary>Есть кандидаты - все или часть частей обработаны успешно.</summary>
    CandidatesFound,

    /// <summary>Модель вернула протокольный маркер <see cref="TranscriptFactExtractor.NoFactsMarker"/> по всем частям.</summary>
    NoFactsFound,

    /// <summary>Все части упали с пустым ответом модели.</summary>
    EmptyModelResponse,

    /// <summary>Все части упали с непригодным ответом (не директивы и не маркер).</summary>
    MalformedResponse,

    /// <summary>Все части упали ошибкой сервиса/исключением.</summary>
    ServiceError,

    /// <summary>Прогон отменён (cancellation requested).</summary>
    Cancelled,

    /// <summary>Часть частей успешно, часть упала; кандидатов не найдено.</summary>
    Partial
}

/// <summary>Результат прогона: исход + кандидаты + индексы упавших частей для повтора.</summary>
public sealed record ExtractionRunResult(
    ExtractionOutcome Outcome,
    IReadOnlyList<FactCandidate> Candidates,
    IReadOnlyList<int> FailedParts,
    int PartsDone,
    string? Error);

/// <summary>Прогресс по частям. PartIndex - 1-based номер только что завершённой части;
/// PartsDone - сколько частей успешно закрыто (Ok/NO_FACTS); DoneChars - сумма их
/// символов для взвешенного процента; TotalParts - сколько частей в этом прогоне.</summary>
public sealed record ExtractProgress(int PartsDone, int PartIndex, int TotalParts,
    int CandidatesFound, int DoneChars, TimeSpan Elapsed, bool FailedPartRetryable);

/// <summary>
/// Извлечение фактов из транскрипта через директивный формат: строит промпт,
/// который просит LLM вернуть ТОЛЬКО [[MEMORY_ADD]]-директивы либо маркер
/// <see cref="NoFactsMarker"/>, и парсит ответ через <see cref="MemoryDirectiveParser"/>.
/// Длинный транскрипт режется на части (<see cref="ChunkTranscript"/>) по границам
/// реплик - середина больше не теряется.
/// Транскриптная выжимка работает только на добавление: директивы UPDATE/FORGET
/// из ответа модели отбрасываются, чтобы текст встречи не мог менять чужую память.
/// </summary>
public static class TranscriptFactExtractor
{
    /// <summary>Протокольный маркер «подходящих фактов нет» - отличается от пустого ответа.</summary>
    public const string NoFactsMarker = "NO_FACTS";

    /// <summary>Размер одной части по умолчанию (в запрос влезает вместе с промптом).</summary>
    public const int DefaultMaxChars = 10000;

    // [HH:MM:SS] (FileTranscriptionService.Fmt) или [MM:SS] (MeetingTranscriptStore.TimestampedText).
    private static readonly Regex TimeStampRe = new(@"^\[(?:\d{1,3}:)?\d{1,2}:\d{2}\]", RegexOptions.Compiled);

    /// <summary>
    /// Системный промпт: транскрипт в конце, дополнительные правила пользователя -
    /// только если заданы (<see cref="Config.MemoryConfig.TranscriptExtractRules"/>).
    /// Правила пользователя приоритетны над типовым фильтром фактов (лекция →
    /// определения/ответы преподавателя), но не могут отменить формат вывода,
    /// запрет выдумывания и маркер NO_FACTS.
    /// </summary>
    /// <param name="intensity">"low"|"medium"|"high" — клауза §6.4 из
    /// <see cref="Extraction.MemoryPrompts.IntensityClause"/> (null → medium).</param>
    public static string BuildPrompt(string transcript, string? customRules,
        int? partIndex = null, int? partCount = null, string? intensity = null)
    {
        var sb = new StringBuilder(
            "Ты - модуль памяти ассистента. Извлеки из транскрипта встречи долговременные " +
            "факты и выдай ТОЛЬКО директивы памяти - по одной на факт, без пояснений, " +
            "без markdown, без любого другого текста.\n\n" +
            "ФОРМАТ (одна директива на факт):\n" +
            "[[MEMORY_ADD]]факт[[/MEMORY_ADD]]\n\n" +
            "ФАКТЫ: кто есть кто (роли, имена, должности), принятые решения, " +
            "договорённости, дедлайны, суммы/цифры, жёсткие требования и ограничения, " +
            "устойчивые предпочтения. Факт должен быть атомарным и понятным без контекста.\n" +
            "ПРАВИЛА:\n" +
            "1. Пиши только информацию, явно прозвучавшую в транскрипте. Не выдумывай " +
            "факты, имена, цифры, даты и не додумывай за участников.\n" +
            "2. Пропускай мусор: приветствия, благодарности, проверки связи и прочий filler.\n" +
            "3. Реплики «ИИ»/«AI» - ответы ассистента. Не приписывай их человеку и не " +
            "оформляй как его слова; извлекай из них факт только если это прямой ответ " +
            "на вопрос участника и это разрешено правилами ниже.\n" +
            "4. Если извлекать нечего - верни ровно одну строку " + NoFactsMarker + " и больше ничего.");

        if (!string.IsNullOrWhiteSpace(customRules))
        {
            sb.Append("\n\nДополнительные правила пользователя (важнее типового фильтра фактов; " +
                "если это лекция - извлекай определения, формулы и ответы преподавателя на вопросы):\n")
                .Append(customRules.Trim());
            sb.Append("\nЭти правила не отменяют формат вывода, запрет выдумывания и маркер " + NoFactsMarker + ".");
        }

        // Клауза интенсивности (§6.4): управляет ШИРИНОЙ отбора, но не может
        // отменить запрет выдумывания — grounding остаётся при любой строчке.
        sb.Append(Extraction.MemoryPrompts.IntensityClause(intensity));

        sb.Append("\n\nТРАНСКРИПТ");
        if (partIndex is > 0 && partCount is > 1)
            sb.Append($" (часть {partIndex.Value} из {partCount.Value})");
        sb.Append(":\n").Append(transcript);
        return sb.ToString();
    }

    /// <summary>Разобрать ответ LLM в директивы (обёртка над <see cref="MemoryDirectiveParser.Parse"/>).</summary>
    public static IReadOnlyList<MemoryDirective> ParseOutput(string llmOutput)
        => MemoryDirectiveParser.Parse(llmOutput);

    /// <summary>Ответ части - это протокольный маркер «фактов нет» (ровно одна строка NO_FACTS).</summary>
    public static bool IsNoFacts(string? content)
        => string.Equals(NormalizeReply(content), NoFactsMarker, StringComparison.OrdinalIgnoreCase);

    // Разворачивает code-fence и обрезку вокруг ответа перед проверкой маркера.
    private static string? NormalizeReply(string? content)
    {
        var s = content?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = s.IndexOf('\n');
            var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNl >= 0 && lastFence > firstNl)
                s = s.Substring(firstNl + 1, lastFence - firstNl - 1).Trim();
        }
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Режет транскрипт на последовательные части по границам реплик:
    /// реплика - строка, начинающаяся с таймкода [HH:MM:SS] или [MM:SS]; если таймкодов
    /// нет вообще - каждая непустая строка считается отдельной репликой. Незавершённая
    /// реплика переносится в следующую часть целиком (сплит внутри реплики - только если
    /// она сама длиннее maxChars). Текст не пропускается и не дублируется: конкатенация
    /// частей покрывает весь вход без повторов.
    /// </summary>
    public static List<TranscriptChunk> ChunkTranscript(string text, int maxChars = DefaultMaxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        var chunks = new List<TranscriptChunk>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        // Реплики = диапазоны [start,len) исходного текста; храним диапазоны, а не
        // подстроки, чтобы части склеивались обратно без расхождений.
        var units = new List<(int Start, int Len)>();
        var lineStart = 0;
        var curStart = -1;      // начало текущей реплики; -1 = ещё не открыта
        var curEnd = 0;
        while (lineStart < text.Length)
        {
            var nl = text.IndexOf('\n', lineStart);
            var lineEnd = nl < 0 ? text.Length : nl;                 // без '\n'
            var segEnd = nl < 0 ? text.Length : nl + 1;              // с '\n'
            var line = text.AsSpan(lineStart, lineEnd - lineStart);
            var isHeader = TimeStampRe.IsMatch(line);
            var isEmpty = line.TrimEnd('\r').IsWhiteSpace();

            if (curStart < 0)
            {
                if (isEmpty) { lineStart = segEnd; continue; }       // ведущие пустые строки
                curStart = lineStart; curEnd = segEnd;
            }
            else if (isHeader && !isEmpty)
            {
                units.Add((curStart, curEnd - curStart));
                curStart = lineStart; curEnd = segEnd;
            }
            else
            {
                curEnd = segEnd;                                     // продолжение реплики / пустая строка внутри
            }
            lineStart = segEnd;
        }
        if (curStart >= 0) units.Add((curStart, curEnd - curStart));

        // Пакуем реплики в части; переполненная реплика сплитится, «хвост» продолжается
        // в следующей части (перенос незавершённой реплики вперёд, без дублирования).
        var u = 0;
        while (u < units.Count)
        {
            var start = units[u].Start;
            var limit = start + maxChars;
            var end = start;
            while (u < units.Count)
            {
                var (s, len) = units[u];
                if (end + len <= limit && s >= end) { end = s + len; u++; }
                else if (end == start)
                {
                    // Реплика длиннее maxChars: берём её начало, остаток пойдёт следующей частью.
                    var take = Math.Min(maxChars, len);
                    units[u] = (s + take, len - take);
                    end = s + take;
                    break;
                }
                else break;
            }
            Emit(chunks, text, start, end);
        }
        return chunks;
    }

    private static void Emit(List<TranscriptChunk> chunks, string text, int start, int end)
    {
        var piece = text.Substring(start, end - start).Trim('\r', '\n', ' ', '\t');
        if (piece.Length > 0) chunks.Add(new TranscriptChunk(chunks.Count, piece));
    }

    private enum PartOutcome { Ok, NoFacts, Empty, Malformed, ServiceError }

    /// <summary>
    /// Полный прогон по транскрипту: чанкинг -> llmCall по каждой части (один встроенный
    /// retry неуспешной части) -> сведение исходов. Исключения llmCall не летят наружу,
    /// кроме OperationCanceledException при запрошенной отмене.
    /// </summary>
    public static Task<ExtractionRunResult> ExtractAsync(
        string transcript, string? userRules,
        Func<string, CancellationToken, Task<string>> llmCall,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken ct = default, string? intensity = null)
        => ExtractPartsAsync(ChunkTranscript(transcript), userRules, llmCall,
                             partIndices: null, progress, ct, intensity);

    /// <summary>
    /// Повтор только перечисленных частей уже готовых чанков («Повторить ошибки» в диалоге).
    /// partIndices - значения <see cref="TranscriptChunk.Index"/>; null/пусто = все части.
    /// </summary>
    /// <param name="intensity">"low"|"medium"|"high" → клауза в промпт (§6.4).</param>
    public static async Task<ExtractionRunResult> ExtractPartsAsync(
        IReadOnlyList<TranscriptChunk> chunks, string? userRules,
        Func<string, CancellationToken, Task<string>> llmCall,
        IReadOnlyCollection<int>? partIndices = null,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken ct = default, string? intensity = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(llmCall);

        var order = (partIndices == null || partIndices.Count == 0)
            ? chunks.Select(c => c.Index).ToList()
            : chunks.Where(c => partIndices.Contains(c.Index)).Select(c => c.Index).ToList();
        var total = order.Count;

        var candidates = new List<FactCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = new Dictionary<int, (PartOutcome Kind, string? Error)>();
        var done = 0;
        var doneChars = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        ExtractionOutcome? cancelled = null;
        try
        {
            foreach (var idx in order)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = chunks[idx];
                var prompt = BuildPrompt(chunk.Text, userRules, chunk.Index + 1, chunks.Count, intensity);

                var (kind, content, error) = await RunPartAsync(prompt, llmCall, ct).ConfigureAwait(false);
                if (kind == PartOutcome.Ok)
                {
                    foreach (var d in MemoryDirectiveParser.Parse(content!))
                    {
                        if (d.Kind != MemoryDirectiveKind.Add) continue;   // транскрипт не меняет чужую память
                        var norm = NormalizeFact(d.Old);
                        if (norm.Length == 0 || !seen.Add(norm)) continue;
                        candidates.Add(new FactCandidate(d, chunk.Index, FirstTimeAnchor(chunk.Text)));
                    }
                    done++;
                    doneChars += chunk.CharCount;
                }
                else if (kind == PartOutcome.NoFacts)
                {
                    done++;
                    doneChars += chunk.CharCount;
                }
                else
                {
                    failures[idx] = (kind, error);
                }

                progress?.Report(new ExtractProgress(done, chunk.Index + 1, total,
                    candidates.Count, doneChars, sw.Elapsed, failures.Count > 0));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = ExtractionOutcome.Cancelled;
        }

        sw.Stop();
        var outcome = cancelled ?? Classify(total, done, failures, candidates.Count);
        return new ExtractionRunResult(outcome, candidates,
            failures.Keys.OrderBy(i => i).ToList(), done,
            failures.Count > 0 ? failures.OrderBy(kv => kv.Key).First().Value.Error : null);
    }

    // Один запрос к модели + ровно один retry при неуспехе (кроме отмены).
    private static async Task<(PartOutcome Kind, string? Content, string? Error)> RunPartAsync(
        string prompt, Func<string, CancellationToken, Task<string>> llmCall, CancellationToken ct)
    {
        (PartOutcome Kind, string? Content, string? Error) last = default;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string? content;
            try
            {
                content = await llmCall(prompt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = (PartOutcome.ServiceError, null, ex.Message);
                continue;
            }

            if (IsNoFacts(content)) return (PartOutcome.NoFacts, null, null);
            var normalized = NormalizeReply(content);
            if (normalized == null)
            {
                last = (PartOutcome.Empty, null, "модель вернула пустой ответ");
                continue;
            }
            var dirs = MemoryDirectiveParser.Parse(normalized);
            if (dirs.Count == 0)
            {
                last = (PartOutcome.Malformed, null, "модель вернула непригодный ответ (нет директив)");
                continue;
            }
            return (PartOutcome.Ok, normalized, null);
        }
        return last;
    }

    private static ExtractionOutcome Classify(int total, int done,
        Dictionary<int, (PartOutcome Kind, string? Error)> failures, int candidates)
    {
        if (failures.Count == 0)
            return candidates > 0 ? ExtractionOutcome.CandidatesFound : ExtractionOutcome.NoFactsFound;
        if (done > 0)
            return candidates > 0 ? ExtractionOutcome.CandidatesFound : ExtractionOutcome.Partial;

        // Все части упали - исход по доминирующему типу сбоя.
        if (failures.Values.All(f => f.Kind == PartOutcome.Empty)) return ExtractionOutcome.EmptyModelResponse;
        if (failures.Values.All(f => f.Kind == PartOutcome.Malformed)) return ExtractionOutcome.MalformedResponse;
        return ExtractionOutcome.ServiceError;
    }

    private static string? FirstTimeAnchor(string chunkText)
    {
        var m = TimeStampRe.Match(chunkText);
        return m.Success ? m.Value.Trim('[', ']') : null;
    }

    /// <summary>Нормализация текста факта для дедупа (регистр/пробелы).</summary>
    public static string NormalizeFact(string s)
        => WhitespaceRe.Replace(s.Trim(), " ").ToLowerInvariant();

    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);
}
