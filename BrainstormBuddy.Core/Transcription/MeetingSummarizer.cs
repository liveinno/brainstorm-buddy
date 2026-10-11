using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Transcription;

/// <summary>
/// Составляет ПРОТОКОЛ ВСТРЕЧИ через LLM по тексту с тайм-кодами. Та же логика,
/// что в FileTranscriptionWindow (map-reduce для длинных текстов), вынесена в
/// общий сервис — переиспользуется окном истории встреч.
/// </summary>
public static class MeetingSummarizer
{
    /// <summary>Протокол по транскрипту. onStatus — прогресс для UI (UI-поток сам маршалит).</summary>
    // Верхняя граница max_tokens для продолжений (как кламп настройки 2.7.2).
    private const int MaxTokensCap = 4096;

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
    /// </summary>
    public static async Task<string> SummarizeAsync(IApiClient api, string transcript,
        Action<string>? onStatus, CancellationToken ct, string? systemPrompt = null)
    {
        const int chunkChars = 6000;

        var protocolSys = string.IsNullOrWhiteSpace(systemPrompt)
            ? DefaultSummaryPrompt
            : systemPrompt;
        if (transcript.Length <= chunkChars)
        {
            var r = await AskWithContinuationAsync(api,
                "Составь протокол встречи по транскрибации выше.",
                protocolSys + "\n\nТРАНСКРИБАЦИЯ:\n" + transcript, 1500, ct);
            // AskAsync при отмене возвращает Canceled-результат (не бросает) —
            // восстанавливаем семантику отмены, чтобы caller показал «отменено».
            ct.ThrowIfCancellationRequested();
            return (r ?? "").Trim();
        }

        // MAP: из каждого фрагмента вытащить факты, СОХРАНЯЯ тайм-коды.
        var chunks = SplitChars(transcript, chunkChars);
        var partials = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke(UiText.Tr(
                $"Протокол · разбор части {i + 1}/{chunks.Count}…",
                $"Summary · parsing part {i + 1}/{chunks.Count}…"));
            var part = await AskWithContinuationAsync(api,
                "Выпиши из фрагмента ключевое, СОХРАНЯЯ тайм-коды [мм:сс]: темы, факты, цифры, имена/роли, " +
                "решения и договорённости. Списком, только по тексту, без воды.",
                "Ты обрабатываешь фрагмент транскрибации встречи.\n\n" + chunks[i],
                600, ct);
            if (!string.IsNullOrWhiteSpace(part)) partials.Add(part.Trim());
            ct.ThrowIfCancellationRequested();
        }

        // REDUCE: собрать протокол из конспектов частей.
        onStatus?.Invoke(UiText.Tr("Протокол · сведение…", "Summary · merging…"));
        var combined = string.Join("\n", partials);
        var final = await AskWithContinuationAsync(api,
            "Собери из этих конспектов частей ЕДИНЫЙ протокол встречи строго по формату выше.",
            protocolSys + "\n\nКОНСПЕКТЫ ЧАСТЕЙ (с тайм-кодами):\n" + combined, 1800, ct);
        ct.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(final) ? combined : final.Trim();
    }

    /// <summary>
    /// AskAsync + догон обрезанного ответа (бэклог 2.8.x п.9): при WasTruncated досылаем
    /// «продолжи с места обрыва» с удвоенным max_tokens (максимум 2 продолжения) и склеиваем.
    /// Ответ пишется НАПРЯМУЮ к хвосту: разрыв мог резать слово/токен, поэтому ни пробел,
    /// ни перенос не добавляем; визуальный «…» обрезанной части перед склейкой снимаем.
    /// </summary>
    private static async Task<string?> AskWithContinuationAsync(IApiClient api,
        string userText, string systemPrompt, int maxTokens, CancellationToken ct)
    {
        var r = await api.AskAsync(userText, systemPrompt, maxTokens, new List<ChatMessage>(), ct);
        var text = r.Content;
        var budget = maxTokens;

        for (var cont = 0; r.WasTruncated && cont < 2 && !string.IsNullOrEmpty(text); cont++)
        {
            // Хвост — якорь продолжения: модель видит, где оборвалась мысль, и доезжает
            // до конца, а не переписывает ответ с начала.
            var tail = text.TrimEnd();
            if (tail.EndsWith('…')) tail = tail[..^1].TrimEnd();
            if (tail.Length == 0) break;

            budget = Math.Min(MaxTokensCap, Math.Max(budget * 2, 1024));
            var anchor = tail.Length > 400 ? tail[^400..] : tail;
            var contRes = await api.AskAsync(
                "Твой предыдущий ответ оборвался по лимиту токенов. Продолжи его с места обрыва " +
                "— выведи ТОЛЬКО продолжение (без повтора уже написанного, без комментариев).\n\n" +
                "КОНЕЦ ОБОРВАННОГО ОТВЕТА:\n" + anchor,
                systemPrompt, budget, new List<ChatMessage>(), ct);
            if (string.IsNullOrWhiteSpace(contRes.Content)) break;

            text = tail + contRes.Content;
            r = contRes;
        }
        return text;
    }

    private static List<string> SplitChars(string s, int size)
    {
        var list = new List<string>();
        for (int i = 0; i < s.Length; i += size)
            list.Add(s.Substring(i, Math.Min(size, s.Length - i)));
        return list;
    }
}
