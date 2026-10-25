namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Состояние строки пикера вложений для подсчёта «готово N из M» (§4/§5 спеки):
/// Processing — в очереди/читается; Ready — извлечён и будет прикреплён;
/// NotAttachable — ошибка/ожидает выбора кодировки (не прикрепляется);
/// AlreadyLinked — документ уже привязан к текущей встрече (повторный импорт
/// не нужен, но строка считается «готовой» в счётчике).
/// </summary>
public enum PickerItemState
{
    Processing,
    Ready,
    NotAttachable,
    AlreadyLinked,
}

/// <summary>
/// Агрегат по строкам пикера: сколько готово, сколько ещё обрабатывается,
/// сколько не будет прикреплено.
/// </summary>
public readonly record struct PickerTally(int Total, int ReadyToAttach, int Processing, int NotAttachable)
{
    /// <summary>«Прикрепить (N)» доступна только когда есть что прикреплять
    /// и очередь завершена — по спеке «пока есть обработка, Commit недоступен».</summary>
    public bool CanCommit => ReadyToAttach > 0 && Processing == 0;

    /// <summary>Показывать пояснение «остальные не будут прикреплены».</summary>
    public bool HasUnattachable => NotAttachable > 0;
}

/// <summary>Чистые подсчёты диалога — без WPF, покрывается unit-тестами.</summary>
public static class AttachmentPickerSummary
{
    public static PickerTally Tally(IEnumerable<PickerItemState> states)
    {
        int total = 0, ready = 0, processing = 0, bad = 0;
        foreach (var s in states)
        {
            total++;
            switch (s)
            {
                case PickerItemState.Ready:
                case PickerItemState.AlreadyLinked:
                    ready++;
                    break;
                case PickerItemState.Processing:
                    processing++;
                    break;
                default:
                    bad++;
                    break;
            }
        }
        return new PickerTally(total, ready, processing, bad);
    }

    /// <summary>
    /// Итоговый статус документа для строки пикера/истории (§4 задачи 16.10):
    /// успешный OCR — НЕ ошибка, счётчики честные. <paramref name="segCount"/> —
    /// число сохранённых фрагментов (0 у нового документа → не добавляем).
    /// <paramref name="tr"/> — локализатор (key, fallback) → строка; Core не
    /// имеет доступа к ресурсам WPF, поэтому вызывающий передаёт свой L/Tf.
    /// Примеры: «Готово: стр. 350/350 · текст 348 · OCR 2 · фрагментов: 41»,
    /// «Готово: фрагментов: 12», «Нет текста» (CharCount=0 без warnings — пусто).
    /// </summary>
    public static string FormatDoneStatus(
        StoredDocument doc, int segCount, Func<string, string, string> tr)
    {
        ArgumentNullException.ThrowIfNull(tr);
        var sb = new System.Text.StringBuilder(96);
        var ps = doc.PageStats;
        if (ps is { HasData: true })
        {
            // Частичный результат — показываем прогресс по страницам честно.
            sb.Append(ps.Processed >= ps.Total
                ? tr("L_Att_StDonePagesFmt", "Готово: стр. {0}/{1}")
                : tr("L_Att_StDonePagesPartialFmt", "Частично: стр. {0}/{1}"));
            sb = new System.Text.StringBuilder(
                string.Format(sb.ToString(), ps.Processed, ps.Total));
            if (ps.TextLayer > 0)
                sb.Append(string.Format(tr("L_Att_StTextFmt", " · текст {0}"), ps.TextLayer));
            if (ps.Ocr > 0)
                sb.Append(string.Format(tr("L_Att_StOcrFmt", " · OCR {0}"), ps.Ocr));
            if (ps.Empty > 0)
                sb.Append(string.Format(tr("L_Att_StEmptyFmt", " · пусто {0}"), ps.Empty));
        }
        else if (doc.CharCount > 0)
        {
            sb.Append(tr("L_Att_Ready", "Готово"));
        }
        else
        {
            sb.Append(tr("L_Att_NoText", "Нет текста"));
        }
        if (segCount > 0)
            sb.Append(string.Format(tr("L_Att_SegmentsFmt", " · фрагментов: {0}"), segCount));
        return sb.ToString();
    }
}
