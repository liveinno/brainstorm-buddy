using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Transcription;

/// <summary>
/// Составляет ПРОТОКОЛ ВСТРЕЧИ через LLM по тексту с тайм-кодами. Та же логика,
/// что в FileTranscriptionWindow (map-reduce для длинных текстов), вынесена в
/// общий сервис — переиспользуется окном истории встреч.
/// </summary>
public static class MeetingSummarizer
{
    // У reasoning-модели бюджеты 1500 и 3000 исчерпывались до финального текста.
    // Протокол имеет отдельный лимит, не меняющий 25000 токенов обычного чата.
    private const int InitialTokens = 4096;
    private const int MaxTokensCap = 8192;
    private const int MaxContinuationAttempts = 2;
    private const int ChunkChars = 6000;

    /// <summary>
    /// Встроенный системный промпт протокола встречи — используется, когда
    /// Advanced.SummaryPrompt пустой. Маркеры в угловых скобках — инструкции
    /// формата для LLM, а не .NET-плейсхолдеры.
    /// </summary>
    public const string DefaultSummaryPrompt =
        "Ты составляешь ПРОТОКОЛ ВСТРЕЧИ по транскрибации (реплики с тайм-кодами [мм:сс]). " +
        "Опирайся ТОЛЬКО на текст, ничего не выдумывай. Если имя/название/компания НЕ звучит в " +
        "тексте — НЕ придумывай их, указывай роль (например «интервьюер», «кандидат»). " +
        "Не пересказывай реплики дословно — обобщай по смыслу. Формат ответа (Markdown, по-русски):\n\n" +
        "## Протокол встречи\n" +
        "**Тема:** <определи по содержанию>  \n" +
        "### Участники и роли\n<кто говорит и их роли, если понятно из реплик>\n" +
        "### Ход встречи\n<по темам; каждый пункт начинай с тайм-кода [мм:сс]; сохраняй цифры, названия, факты>\n" +
        "### Договорённости и решения\n<конкретные договорённости>\n" +
        "### Открытые вопросы\n<что осталось нерешённым>\n\n" +
        "Пиши конкретно и по делу, без общих фраз.";

    /// <summary>
    /// Протокол по транскрипту. onStatus — прогресс для UI (UI-поток сам маршалит).
    /// systemPrompt — кастомный системный промпт (Advanced.SummaryPrompt); пусто/null →
    /// встроенный <see cref="DefaultSummaryPrompt"/>.
    /// <para>
    /// onPart(done, total) — шаг по частям для детерминированного прогресс-бара
    /// (короткая встреча — 1 шаг, длинная — MAP-части + REDUCE).
    /// onUsage — КАЖДЫЙ реальный запрос сервиса, включая ограниченные продолжения:
    /// расход обязан попасть в TokenLedger (иначе «ответы есть, токены 0»).
    /// onFailure — человекочитаемая причина, почему вернулась пустая строка
    /// (ошибка транспорта, пустой ответ модели, исчерпанный бюджет). Пустой
    /// результат никогда не означает «успешный протокол».
    /// </para>
    /// </summary>
    public static async Task<string> SummarizeAsync(IApiClient api, string transcript,
        Action<string>? onStatus, CancellationToken ct, string? systemPrompt = null,
        Action<int, int>? onPart = null, Action<AskResult>? onUsage = null,
        Action<string>? onFailure = null, string? checkpointJson = null,
        Func<string, Task>? onCheckpoint = null)
    {
        var protocolSys = string.IsNullOrWhiteSpace(systemPrompt)
            ? DefaultSummaryPrompt
            : systemPrompt;
        var chunkCount = (transcript.Length + ChunkChars - 1) / ChunkChars;
        var state = RestoreCheckpoint(checkpointJson, transcript, protocolSys,
            transcript.Length <= ChunkChars ? 0 : chunkCount);
        if (transcript.Length <= ChunkChars)
        {
            onPart?.Invoke(0, 1);
            var shortResult = await AskWithContinuationAsync(api,
                "Составь протокол встречи по транскрибации выше.",
                protocolSys + "\n\nТРАНСКРИБАЦИЯ:\n" + transcript, ct,
                state, onCheckpoint, onStatus, onUsage).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (shortResult.Failed)
            {
                onFailure?.Invoke(shortResult.Failure ?? DescribeEmptyAnswer());
                return "";
            }
            onPart?.Invoke(1, 1);
            return shortResult.Text!.Trim();
        }

        // Завершённые части переживают отмену, сетевую ошибку и перезапуск.
        // Хеш входа не позволяет использовать их для другой встречи/инструкции.
        var totalSteps = chunkCount + 1;
        onPart?.Invoke(state.CompletedParts.Count, totalSteps);
        for (int i = state.CompletedParts.Count; i < chunkCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke(UiText.Tr(
                $"Протокол · разбор части {i + 1}/{chunkCount}…",
                $"Summary · parsing part {i + 1}/{chunkCount}…"));
            var offset = i * ChunkChars;
            var part = await AskWithContinuationAsync(api,
                "Выпиши из фрагмента ключевое, СОХРАНЯЯ тайм-коды [мм:сс]: темы, факты, цифры, имена/роли, " +
                "решения и договорённости. Списком, только по тексту, без воды.",
                "Ты обрабатываешь фрагмент транскрибации встречи.\n\n" +
                    transcript.Substring(offset, Math.Min(ChunkChars, transcript.Length - offset)),
                ct, state, onCheckpoint, onStatus, onUsage).ConfigureAwait(false);
            if (part.Failed)
            {
                onFailure?.Invoke(UiText.Tr(
                    $"часть {i + 1}/{chunkCount}: {part.Failure ?? DescribeEmptyAnswer()}",
                    $"part {i + 1}/{chunkCount}: {part.Failure ?? DescribeEmptyAnswer()}"));
                return "";
            }
            state.CompletedParts.Add(part.Text!.Trim());
            state.PendingText = "";
            state.PendingBudget = 0;
            await SaveCheckpointAsync(state, onCheckpoint).ConfigureAwait(false);
            onPart?.Invoke(i + 1, totalSteps);
        }

        ct.ThrowIfCancellationRequested();
        onStatus?.Invoke(UiText.Tr("Протокол · сведение…", "Summary · merging…"));
        var final = await AskWithContinuationAsync(api,
            "Собери из этих конспектов частей ЕДИНЫЙ протокол встречи строго по формату выше.",
            protocolSys + "\n\nКОНСПЕКТЫ ЧАСТЕЙ (с тайм-кодами):\n" + string.Join("\n", state.CompletedParts),
            ct, state, onCheckpoint, onStatus, onUsage).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (final.Failed)
        {
            onFailure?.Invoke(final.Failure ?? DescribeEmptyAnswer());
            return "";
        }
        onPart?.Invoke(totalSteps, totalSteps);
        return final.Text!.Trim();
    }

    /// <summary>
    /// AskAsync + ограниченный догон обрезанного ответа. Reasoning-модели могут
    /// вернуть finish_reason=length с пустым Content, когда весь бюджет съеден
    /// скрытым рассуждением. Повторяем только отсутствующий финальный ответ
    /// отдельным бюджетом; сырой reasoning никогда не читается и не показывается.
    /// onUsage вызывается на КАЖДЫЙ реальный запрос (первичный и продолжения):
    /// провайдер списал токены за все попытки, счётчик обязан это учесть.
    /// </summary>
    private static async Task<SummaryAttempt> AskWithContinuationAsync(IApiClient api,
        string userText, string systemPrompt, CancellationToken ct, SummaryCheckpoint state,
        Func<string, Task>? onCheckpoint, Action<string>? onStatus, Action<AskResult>? onUsage)
    {
        var text = state.PendingText;
        var maxTokens = Math.Clamp(state.PendingBudget, InitialTokens, MaxTokensCap);
        for (var attempt = 0; attempt <= MaxContinuationAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var hasText = !string.IsNullOrWhiteSpace(text);
            var nextUser = userText;
            var nextSystem = systemPrompt;
            if (hasText)
            {
                nextUser += "\nОтвет обрезан. Продолжи ровно с места обрыва, без повторов и нового заголовка.";
                nextSystem += "\n\nНАЧАЛО ТВОЕГО ОТВЕТА (не повторяй, продолжи):\n" + Tail(text, 2000);
            }
            else if (attempt > 0 || state.PendingBudget > 0)
            {
                // Сохраняем исходное задание: без него MAP-повтор терял требование
                // выписать факты, а не просто пересказать транскрипт.
                nextUser += "\nПредыдущая попытка не дала финального текста. " +
                    "Дай ответ целиком в пределах нового лимита, без рассуждений.";
            }
            if (hasText || attempt > 0)
                onStatus?.Invoke(UiText.Tr("Протокол · дописываю незавершённый ответ…",
                    "Summary · continuing unfinished answer…"));

            var r = await api.AskAsync(nextUser, nextSystem, maxTokens,
                new List<ChatMessage>(), ct, background: true,
                requestTimeout: TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            onUsage?.Invoke(r);
            ThrowIfCanceled(r, ct);
            state.PendingBudget = maxTokens;
            if (r.Error != null)
            {
                await SaveCheckpointAsync(state, onCheckpoint).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                    return new SummaryAttempt(null, true, r.Error);

                // Ошибка на продолжении не отменяет уже выданный текст. Возвращаем
                // честный частичный результат с маркером обрыва: короткий протокол
                // не стирается, а MAP-часть может попасть в REDUCE вместе с остальными.
                return new SummaryAttempt(AddTruncationMarker(text), false, r.Error);
            }

            var incomplete = NeedsContinuation(r);
            if (!string.IsNullOrWhiteSpace(r.Content))
            {
                text += incomplete ? StripTrailingTruncationMarker(r.Content) : r.Content;
                state.PendingText = text;
            }
            if (!incomplete)
            {
                if (!string.IsNullOrWhiteSpace(r.Content) && r.EmptyKind != EmptyAnswerKind.ProviderCap)
                    return new SummaryAttempt(text, false);
                await SaveCheckpointAsync(state, onCheckpoint).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                    return new SummaryAttempt(AddTruncationMarker(text), false, DescribeEmptyResult(r));
                return new SummaryAttempt(null, true, DescribeEmptyResult(r));
            }

            await SaveCheckpointAsync(state, onCheckpoint).ConfigureAwait(false);
            maxTokens = Math.Min(MaxTokensCap, maxTokens * 2);
        }
        // Частичный ответ сохранён для продолжения, но не заменяет старый протокол.
        return new SummaryAttempt(null, true,
            UiText.Tr("бюджет ответа исчерпан; завершённые части сохранены — повторите операцию",
                "response budget exhausted; completed parts saved — retry the operation"));
    }

    private sealed class SummaryCheckpoint
    {
        public SummaryCheckpoint() { }
        public int Schema { get; set; } = 1;
        public string Fingerprint { get; set; } = "";
        public List<string> CompletedParts { get; set; } = new();
        public string PendingText { get; set; } = "";
        public int PendingBudget { get; set; }
    }

    private static SummaryCheckpoint RestoreCheckpoint(string? json, string transcript,
        string protocol, int chunkCount)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<int> lengths = stackalloc int[] { transcript.Length, protocol.Length };
        hash.AppendData(MemoryMarshal.AsBytes(lengths));
        hash.AppendData(MemoryMarshal.AsBytes(transcript.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(protocol.AsSpan()));
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        var fingerprint = Convert.ToHexString(digest);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var state = JsonSerializer.Deserialize<SummaryCheckpoint>(json);
                if (state is { Schema: 1, CompletedParts: not null, PendingText: not null }
                    && state.Fingerprint == fingerprint && state.CompletedParts.Count <= chunkCount
                    && state.CompletedParts.TrueForAll(p => !string.IsNullOrWhiteSpace(p)))
                    return state;
            }
            catch (JsonException) { /* повреждённый checkpoint не меняет прежнее резюме */ }
        }
        return new SummaryCheckpoint { Fingerprint = fingerprint };
    }

    private static Task SaveCheckpointAsync(SummaryCheckpoint state, Func<string, Task>? save) =>
        save?.Invoke(JsonSerializer.Serialize(state)) ?? Task.CompletedTask;

    /// <summary>Причина пустого ответа для UI: ошибка транспорта/бюджет/тишина
    /// модели. Сырые рассуждения сюда не попадают — их в Content и нет.</summary>
    private static string DescribeEmptyResult(AskResult r) =>
        !string.IsNullOrWhiteSpace(r.Error) ? r.Error!
        : r.EmptyKind == EmptyAnswerKind.LengthExhausted
            ? UiText.Tr("бюджет ответа исчерпан", "response budget exhausted")
            : r.EmptyKind == EmptyAnswerKind.ProviderCap
                ? UiText.Tr("провайдер обрезал ответ по своему лимиту",
                    "provider truncated the answer by its own limit")
                : DescribeEmptyAnswer();

    private static string DescribeEmptyAnswer() =>
        UiText.Tr("модель вернула пустой ответ", "the model returned an empty answer");

    private sealed record SummaryAttempt(string? Text, bool Failed, string? Failure = null);
    private static string StripTrailingTruncationMarker(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith('…'))
            return trimmed[..^1];
        if (trimmed.EndsWith("...", StringComparison.Ordinal))
            return trimmed[..^3];
        return text;
    }
    private static string AddTruncationMarker(string text) =>
        StripTrailingTruncationMarker(text) + "…";

    private static bool NeedsContinuation(AskResult response)
    {
        if (!string.IsNullOrWhiteSpace(response.Error) || response.Canceled)
            return false;
        // ProviderCap означает, что провайдер уже применил собственный меньший
        // предел: повтор тем же запросом бесполезен.
        if (response.EmptyKind == EmptyAnswerKind.ProviderCap)
            return false;
        if (response.WasTruncated || response.EmptyKind == EmptyAnswerKind.LengthExhausted)
            return true;
        return string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response.FinishReason, "max_tokens", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response.FinishReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfCanceled(AskResult response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (response.Canceled)
            throw new OperationCanceledException("Summary request was canceled.", ct);
    }

    private static string Tail(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars) return text;
        var start = text.Length - maxChars;
        while (start < text.Length && !char.IsWhiteSpace(text[start])) start++;
        return start < text.Length ? text[start..] : text[^maxChars..];
    }
}
