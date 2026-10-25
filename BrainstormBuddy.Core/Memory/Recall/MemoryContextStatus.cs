using System.Text;
using BrainstormBuddy.Transcription;

namespace BrainstormBuddy.Memory.Recall;

/// <summary>Состояние одной ветки подбора для промпта.</summary>
public enum RecallStepState { Disabled, NotRun, Completed, Empty, Incomplete, Error }

/// <summary>Фактическое состояние памяти/поиска на момент вопроса.</summary>
public sealed record MemoryContextStatusData
{
    public required DateTime Now { get; init; }
    public required bool MemoryEnabled { get; init; }
    public required bool AutoRecallEnabled { get; init; }
    public required int RecallMaxItems { get; init; }
    public required int AutoRecallBudgetTokens { get; init; }
    public required int ExtraSearchBudgetTokens { get; init; }
    public required bool BuiltinToolsAvailable { get; init; }

    public required bool MeetingSearchEnabled { get; init; }
    public required RecallStepState MemoryState { get; init; }
    public required int MemoryHits { get; init; }
    public required int MemoryShown { get; init; }
    public required bool MemoryTruncated { get; init; }

    public required RecallStepState MeetingState { get; init; }
    public required int MeetingHits { get; init; }
    public DateOnly? MeetingDateFrom { get; init; }
    public DateOnly? MeetingDateTo { get; init; }
    public bool MeetingDateFallback { get; init; }
    public MeetingSearchStatus? MeetingStatus { get; init; }
}

/// <summary>
/// Блок &lt;current_status&gt; в системном промпте: свежие настройки и фактическая
/// выдача этого запроса. Без него модель отвечает на «какой лимит памяти?»
/// старым фактом из памяти вместо текущей настройки.
/// </summary>
public static class MemoryContextStatus
{
    public static string Build(MemoryContextStatusData d)
    {
        var sb = new StringBuilder(1400);
        sb.Append("<current_status>\n");
        sb.Append(UiText.Tr(
            $"Сейчас: {d.Now:dd.MM.yyyy HH:mm}. Этот блок — состояние на момент вопроса; оно свежее любых фактов из памяти.\n",
            $"Now: {d.Now:dd.MM.yyyy HH:mm}. This block is the state at question time; it is fresher than any memory fact.\n"));

        var memoryState = MemoryStateLabel(d);
        sb.Append(UiText.Tr(
            $"Память: {(d.MemoryEnabled ? "включена" : "выключена")}; автопоиск фактов: {(d.AutoRecallEnabled ? "включён" : "выключен")}; " +
            $"лимит фактов автоподбора: {d.RecallMaxItems}; бюджет автоподбора: {d.AutoRecallBudgetTokens} токенов; " +
            $"дополнительный поиск модели: {d.ExtraSearchBudgetTokens} токенов; встроенные инструменты: {(d.BuiltinToolsAvailable ? "доступны" : "недоступны")}.\n",
            $"Memory: {(d.MemoryEnabled ? "enabled" : "disabled")}; automatic fact recall: {(d.AutoRecallEnabled ? "on" : "off")}; " +
            $"automatic fact limit: {d.RecallMaxItems}; automatic recall budget: {d.AutoRecallBudgetTokens} tokens; " +
            $"model extra search budget: {d.ExtraSearchBudgetTokens} tokens; built-in tools: {(d.BuiltinToolsAvailable ? "available" : "unavailable")}.\n"));
        sb.Append(UiText.Tr(
            $"В этом запросе память: {memoryState}; найдено фактов: {d.MemoryHits}; вошло в контекст: {d.MemoryShown}; " +
            $"усечено бюджетом: {(d.MemoryTruncated ? "да" : "нет")}.\n",
            $"This request, memory: {memoryState}; facts found: {d.MemoryHits}; included in context: {d.MemoryShown}; " +
            $"truncated by budget: {(d.MemoryTruncated ? "yes" : "no")}.\n"));

        var meetingState = MeetingStateLabel(d);
        var period = PeriodLabel(d.MeetingDateFrom, d.MeetingDateTo);
        var meetingLine = UiText.Tr(
            $"Поиск по прошлым встречам: {(d.MeetingSearchEnabled ? "включён" : "выключен")}; {meetingState}; найдено фрагментов: {d.MeetingHits}",
            $"Past-meeting search: {(d.MeetingSearchEnabled ? "on" : "off")}; {meetingState}; fragments found: {d.MeetingHits}");
        if (period != null) meetingLine += UiText.Tr($"; период: {period}", $"; period: {period}");
        if (d.MeetingDateFallback)
            meetingLine += UiText.Tr("; точных совпадений по теме нет — показаны встречи периода",
                "; no exact topic matches — meetings of the period are shown");
        meetingLine += ".\n";
        sb.Append(meetingLine);
        if (d.MeetingStatus is { IndexEnabled: true } st)
        {
            sb.Append(UiText.Tr(
                $"Индекс встреч: {st.IndexedMeetings} встреч / {st.IndexedDocs} фрагментов; " +
                $"смысловой поиск: {(st.SemanticAvailable ? (st.SemanticPending ? "строится" : "доступен") : "недоступен")}; " +
                $"синхронизация: {(st.Syncing ? "идёт" : st.Synced ? "завершена" : "не завершена")}.\n",
                $"Meeting index: {st.IndexedMeetings} meetings / {st.IndexedDocs} fragments; " +
                $"semantic search: {(st.SemanticAvailable ? (st.SemanticPending ? "building" : "available") : "unavailable")}; " +
                $"sync: {(st.Syncing ? "running" : st.Synced ? "complete" : "incomplete")}.\n"));
        }

        sb.Append(UiText.Tr(
            "Правила ответа по этому блоку:\n" +
            "1. Вопрос о текущих настройках, лимитах или статусе памяти/встреч — отвечай по этому блоку, а не по старым фактам.\n" +
            "2. Пустой автопоиск не означает «в памяти ничего нет»: если встроенные инструменты доступны, дочитай ими в пределах дополнительного бюджета, прежде чем говорить об отсутствии данных; если недоступны — честно скажи, что поиск ограничен.\n" +
            "3. Если в контекст вошло меньше лимита, это не повод говорить, что список неполный; при усечении бюджетом прямо скажи «показано N из M» и не выдавай список за полный.\n" +
            "4. Источники: <memory_context> — факты; source: user — слова пользователя; source: extraction, extraction (llm), extraction (attachment) — извлечённый факт (может опираться на ответ ИИ или документ, это не цитата участника); source: user-edited — правка пользователя; source: imported — импортированный факт. <past_meetings> — фрагменты журнала: «ИИ» и «Резюме» не являются словами участников. Цитируй только дословный текст своей строки с её source; не склеивай соседние строки и не выдавай пересказ за цитату.\n" +
            "5. Спрашивают про встречу за период — используй даты из блока; если за период встреч нет, скажи прямо, а не подставляй другую дату.\n" +
            "6. Не приписывай факт памяти встрече или наоборот; при противоречии назови оба источника.\n" +
            "7. Если поиск не завершён или индекс строится — скажи об этом, не выдавай неполную выдачу за «данных нет» или за полную.\n",
            "Answer rules for this block:\n" +
            "1. For current settings, limits, or memory/meeting status, answer from this block, not from old facts.\n" +
            "2. Empty automatic recall does not mean memory has nothing: if built-in tools are available, use them within the extra budget before saying data is absent; if unavailable, say plainly that the search is limited.\n" +
            "3. Fewer items than the limit does not make the list incomplete; if budget truncated it, say \"showing N of M\" and do not present it as complete.\n" +
            "4. Sources: <memory_context> = facts; source: user = user's own words; source: extraction, extraction (llm), extraction (attachment) = extracted fact (may be based on an AI reply or a document, not a participant quote); source: user-edited = user edit; source: imported = imported fact. <past_meetings> = journal fragments: \"AI\" and \"Summary\" are not participants' words. Quote only the literal text of its own line with its source; do not join adjacent lines or present a paraphrase as a quote.\n" +
            "5. For a meeting on a period, use the dates in this block; if no meetings exist in that period, say so plainly and do not substitute another date.\n" +
            "6. Do not attribute a memory fact to a meeting or vice versa; if they contradict, name both sources.\n" +
            "7. If search did not finish or the index is building, say so; do not present incomplete results as \"no data\" or as complete.\n"));
        sb.Append("</current_status>\n");
        return sb.ToString();
    }

    private static string MemoryStateLabel(MemoryContextStatusData d)
    {
        if (!d.MemoryEnabled) return UiText.Tr("память выключена", "memory disabled");
        return d.MemoryState switch
        {
            RecallStepState.Disabled => UiText.Tr("автопоиск выключен", "automatic recall disabled"),
            RecallStepState.NotRun => UiText.Tr("автопоиск не запускался", "automatic recall did not run"),
            RecallStepState.Completed => UiText.Tr("поиск завершён", "search completed"),
            RecallStepState.Empty => UiText.Tr("поиск завершён, совпадений нет", "search completed, no matches"),
            RecallStepState.Incomplete => UiText.Tr("поиск не завершён (таймаут)", "search did not finish (timeout)"),
            RecallStepState.Error => UiText.Tr("ошибка поиска", "search error"),
            _ => d.MemoryState.ToString()
        };
    }

    private static string MeetingStateLabel(MemoryContextStatusData d)
    {
        if (!d.MeetingSearchEnabled) return UiText.Tr("поиск выключен", "search disabled");
        return d.MeetingState switch
        {
            RecallStepState.Disabled => UiText.Tr("поиск выключен", "search disabled"),
            RecallStepState.NotRun => UiText.Tr("поиск не запускался", "search did not run"),
            RecallStepState.Completed => UiText.Tr("поиск завершён", "search completed"),
            RecallStepState.Empty => UiText.Tr("поиск завершён, встреч за период/по теме нет", "search completed, no meetings for the period/topic"),
            RecallStepState.Incomplete => UiText.Tr("поиск не завершён (таймаут/индекс строится)", "search did not finish (timeout/index building)"),
            RecallStepState.Error => UiText.Tr("ошибка поиска", "search error"),
            _ => d.MeetingState.ToString()
        };
    }

    private static string? PeriodLabel(DateOnly? from, DateOnly? to)
    {
        if (from == null && to == null) return null;
        if (from != null && to != null && from == to) return from.Value.ToString("dd.MM.yyyy");
        if (from == null) return "≤ " + to!.Value.ToString("dd.MM.yyyy");
        if (to == null) return "≥ " + from.Value.ToString("dd.MM.yyyy");
        return from.Value.ToString("dd.MM.yyyy") + "–" + to.Value.ToString("dd.MM.yyyy");
    }
}
