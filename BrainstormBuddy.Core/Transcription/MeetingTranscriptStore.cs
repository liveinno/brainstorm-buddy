using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BrainstormBuddy.Transcription;

/// <summary>Одна строка live-журнала встречи (jsonl). r — роль: question/user/answer.</summary>
public sealed class MeetingJournalLine
{
    [JsonPropertyName("t")] public string Ts { get; set; } = "";
    [JsonPropertyName("r")] public string Role { get; set; } = "";
    [JsonPropertyName("x")] public string Text { get; set; } = "";
    /// <summary>Speaker diarization id ("S1"..). Отсутствует в старых файлах → null.</summary>
    [JsonPropertyName("speaker")] public string? Speaker { get; set; }
    /// <summary>Стабильный id сообщения (для image_links и восстановления
    /// незавершённой отправки). Отсутствует в старых файлах → null.</summary>
    [JsonPropertyName("mid")] public string? MessageId { get; set; }
    /// <summary>imageIds изображений сообщения (img-*, см. IChatImageStore).
    /// Реплика без текста, но с ImageIds — полноценная (картинки без вопроса).
    /// Отсутствует в старых файлах → null.</summary>
    [JsonPropertyName("imgs")] public string[]? ImageIds { get; set; }
}

/// <summary>Метаданные встречи (sidecar &lt;id&gt;.meta.json) + производные для списка в UI.</summary>
public sealed class MeetingInfo
{
    public string Id { get; set; } = "";
    public string JournalPath { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime LastLineAt { get; set; }
    public int LineCount { get; set; }
    public string FirstLine { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>Завершённые части и незавершённый ответ AI-резюме для продолжения после ошибки.</summary>
    public string? SummaryCheckpointJson { get; set; }
    /// <summary>Сериализованный MeetingContextState (BrainstormBuddy.Context):
    /// накопительная сводка + прогресс фоновой сводки встречи. Переживает
    /// перезапуск приложения — свежий хвост реплик всегда в самом журнале.</summary>
    public string? ContextStateJson { get; set; }

    // ── Производные для списка (считать строки в XAML-биндингах хрупко). ──
    /// <summary>Число прикреплённых файлов встречи: заполняет UI из
    /// AttachmentStore.CountByMeeting(), в sidecar не сериализуется.</summary>
    [JsonIgnore] public int AttachmentCount { get; set; }
    /// <summary>Число изображений встречи: заполняет UI из
    /// IChatImageStore (GetForMeeting), в sidecar не сериализуется.</summary>
    [JsonIgnore] public int ImageCount { get; set; }
    /// <summary>Бейдж «📎 N» — устаревший текстовый вариант; UI рисует векторную
    /// скрепку (GeoPaperclip) + AttachmentCount. Оставлен как источник числа.</summary>
    [JsonIgnore] public string ListAttachments => AttachmentCount > 0 ? $"📎 {AttachmentCount}" : "";
    [JsonIgnore] public string ListTitle => StartedAt.ToString("dd.MM.yyyy HH:mm");
    [JsonIgnore] public string ListSubtitle
    {
        get
        {
            var d = LastLineAt - StartedAt;
            var dur = d.TotalHours >= 1
                ? UiText.Tr($"{(int)d.TotalHours} ч {d.Minutes} мин", $"{(int)d.TotalHours} h {d.Minutes} min")
                : UiText.Tr($"{Math.Max(0, (int)d.TotalMinutes)} мин", $"{Math.Max(0, (int)d.TotalMinutes)} min");
            var tail = string.IsNullOrEmpty(FirstLine) ? "" : $" · {FirstLine}";
            return $"{dur} · {UiText.Tr("реплик", "lines")}: {LineCount}{tail}";
        }
    }
}

/// <summary>
/// Краш-безопасный журнал живого транскрипта: каждая завершённая реплика
/// (question/user/answer, включая финал стрим-ответа) НЕМЕДЛЕННО дописывается строкой
/// JSONL в %APPDATA%\BrainstormBuddy\meetings\&lt;timestamp&gt;.jsonl. Запись идёт
/// append-open → write → close → WriteThrough на каждую реплику (~1/5с), поэтому при
/// убийстве процесса на диске остаётся всё до последней реплики. Файл write-only:
/// ничего не держим в памяти, кроме пути и счётчиков.
/// Граница встречи = ResetConversation() («новый чат»/кнопка стирания): следующая
/// реплика начинает новый jsonl-файл. Рядом пишется sidecar &lt;id&gt;.meta.json
/// (длительность, первая реплика, саммари) — список окна истории не парсит журналы.
/// </summary>
public sealed class MeetingTranscriptStore
{
    private static readonly JsonSerializerOptions LineOpts = new();
    private static readonly JsonSerializerOptions MetaOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _dir;

    private string? _currentId;
    private string? _currentPath;
    private MeetingInfo? _currentInfo;
    private DateTime _currentStart;
    private DateTime _lastLineAt;

    public MeetingTranscriptStore(string appDataDir)
    {
        _dir = Path.Combine(appDataDir, "meetings");
        System.IO.Directory.CreateDirectory(_dir);
    }

    public string Directory => _dir;
    /// <summary>Id текущей встречи (null — журнал ещё не создан лениво в Append /
    /// EnsureCurrentMeeting). Вложения чата привязываются к этому id — это НЕ
    /// mem_meetings.id из memory.db.</summary>
    public string? CurrentId
    {
        get { lock (_lock) return _currentId; }
    }

    /// <summary>
    /// Гарантировать существование текущего файла встречи и вернуть его id.
    /// Идемпотентно: встреча создаётся один раз и переиспользуется до
    /// BeginNewMeeting()/Delete(). Используется UI вложений: прикрепление файла
    /// не должно ждать первой реплики, а фиктивная строка в журнале запрещена —
    /// создаём пустой .jsonl + sidecar с LineCount=0.
    /// </summary>
    public string EnsureCurrentMeeting()
    {
        lock (_lock)
            return EnsureFileLocked();
    }


    /// <summary>Путь текущего jsonl-файла встречи (null — файл ещё не создан лениво
    /// в Append). Memory-слой пишет его в mem_meetings.journal_path.</summary>
    public string? CurrentPath
    {
        get { lock (_lock) return _currentPath; }
    }

    /// <summary>Следующая реплика начнёт НОВЫЙ файл встречи (вызов при сбросе диалога).</summary>
    public void BeginNewMeeting()
    {
        lock (_lock)
        {
            _currentId = null;
            _currentPath = null;
            _currentInfo = null;
        }
    }

    /// <summary>Дописать завершённую реплику в живой журнал. Потокобезопасно, best-effort.
    /// Реплика без текста, но с imageIds (картинки без вопроса) — полноценная строка;
    /// пустой text без imageIds по-прежнему пропускается. messageId — стабильный id
    /// сообщения для image_links и восстановления прерванной отправки.</summary>
    public void Append(string role, string text, string? speaker = null,
        string? messageId = null, string[]? imageIds = null)
    {
        if (string.IsNullOrWhiteSpace(text) && (imageIds == null || imageIds.Length == 0)) return;
        try
        {
            lock (_lock)
            {
                EnsureFileLocked();

                var now = DateTime.Now;
                var trimmed = text?.Trim() ?? "";
                var line = JsonSerializer.Serialize(new MeetingJournalLine
                {
                    Ts = now.ToString("HH:mm:ss"),
                    Role = role,
                    Text = trimmed,
                    Speaker = speaker,
                    MessageId = messageId,
                    ImageIds = imageIds is { Length: > 0 } ? imageIds : null
                }, LineOpts);

                // WriteThrough: байты уходят на диск сразу — kill -9 процесса не теряет реплику.
                using (var fs = new FileStream(_currentPath!, FileMode.Append, FileAccess.Write,
                           FileShare.Read, 4096, FileOptions.WriteThrough))
                using (var w = new StreamWriter(fs, new UTF8Encoding(true)))
                    w.WriteLine(line);

                _lastLineAt = now;
                var info = _currentInfo!;
                info.LastLineAt = now;
                info.LineCount++;
                if (info.FirstLine.Length == 0 && trimmed.Length > 0)
                    info.FirstLine = trimmed.Replace("\r", " ").Replace('\n', ' ');
                WriteMetaLocked(info);
            }
        }
        catch { /* журнал best-effort: ни одна реплика не должна ронять пайплайн STT/LLM */ }
    }

    /// <summary>
    /// Ленивое создание файла встречи (общая часть Append и EnsureCurrentMeeting).
    /// Возвращает id; при повторном вызове просто отдаёт существующий.
    /// Вызывается строго под _lock.
    /// </summary>
    private string EnsureFileLocked()
    {
        if (_currentPath != null)
            return _currentId!;

        // Первая запись встречи — создаём файл, имя = момент старта.
        _currentStart = DateTime.Now;
        _lastLineAt = _currentStart;
        // Две встречи в одну секунду (сбросили и тут же заговорили) → суффикс -2, -3…
        var id = _currentStart.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = Path.Combine(_dir, id + ".jsonl");
        for (int n = 2; File.Exists(path); n++)
        {
            id = $"{_currentStart:yyyy-MM-dd_HH-mm-ss}-{n}";
            path = Path.Combine(_dir, id + ".jsonl");
        }
        _currentId = id;
        _currentPath = path;
        _currentInfo = new MeetingInfo
        {
            Id = id,
            JournalPath = path,
            StartedAt = _currentStart,
            LastLineAt = _currentStart
        };
        // Пустой журнал + sidecar на диске сразу: встреча видна в истории ещё
        // до первой реплики (например, когда к ней прикрепили только файл).
        File.WriteAllText(path, "", new UTF8Encoding(true));
        WriteMetaLocked(_currentInfo);
        return id;
    }

    /// <summary>Сохранить резюме и его прогресс, не откатывая состояние живой встречи.</summary>
    public void SaveInfo(MeetingInfo info)
    {
        lock (_lock)
        {
            var live = _currentInfo?.Id == info.Id ? _currentInfo : null;
            if (live != null)
            {
                info.LineCount = live.LineCount;
                info.LastLineAt = live.LastLineAt;
                info.FirstLine = live.FirstLine;
                info.ContextStateJson = live.ContextStateJson;
            }
            WriteMeta(info);
            if (live != null)
            {
                live.Summary = info.Summary;
                live.SummaryCheckpointJson = info.SummaryCheckpointJson;
            }
        }
    }

    /// <summary>Сохранить состояние контекста встречи (сводка+прогресс) в sidecar
    /// текущей встречи. Best-effort: ошибка диска не должна ронять пайплайн.</summary>
    public void SaveContextState(string? contextStateJson)
    {
        try
        {
            lock (_lock)
            {
                if (_currentInfo == null) return;
                _currentInfo.ContextStateJson = contextStateJson;
                WriteMetaLocked(_currentInfo);
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Прочитать состояние контекста из sidecar'а встречи по id
    /// (для восстановления после рестарта — встреча уже не «текущая»).</summary>
    public string? LoadContextState(string meetingId)
    {
        try
        {
            var meta = Path.Combine(_dir, meetingId + ".meta.json");
            if (!File.Exists(meta)) return null;
            var info = JsonSerializer.Deserialize<MeetingInfo>(File.ReadAllText(meta), MetaOpts);
            return info?.ContextStateJson;
        }
        catch { return null; }
    }

    /// <summary>Удалить встречу (журнал + sidecar). Если удаляют ЖИВУЮ встречу —
    /// сбрасываем «текущую», чтобы следующая реплика начала новый файл.</summary>
    public void Delete(string id)
    {
        try
        {
            lock (_lock)
            {
                File.Delete(Path.Combine(_dir, id + ".jsonl"));
                File.Delete(Path.Combine(_dir, id + ".meta.json"));
                if (_currentId == id)
                {
                    _currentId = null;
                    _currentPath = null;
                    _currentInfo = null;
                }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Все встречи, свежие сверху. Берёт sidecar'ы; jsonl без sidecar
    /// (старые/повреждённые) сканирует напрямую — они тоже видны в истории.</summary>
    public List<MeetingInfo> LoadAll()
    {
        var list = new List<MeetingInfo>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in System.IO.Directory.GetFiles(_dir, "*.meta.json"))
            {
                try
                {
                    var info = JsonSerializer.Deserialize<MeetingInfo>(File.ReadAllText(f));
                    if (info == null) continue;
                    info.Id = Path.GetFileName(f)[..^".meta.json".Length];
                    if (string.IsNullOrEmpty(info.JournalPath) || !File.Exists(info.JournalPath))
                        info.JournalPath = Path.Combine(_dir, info.Id + ".jsonl");
                    list.Add(info);
                    known.Add(info.Id);
                }
                catch { /* повреждённый sidecar — журнал ниже подхватит сканер */ }
            }
            foreach (var f in System.IO.Directory.GetFiles(_dir, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (known.Contains(id)) continue;
                var info = ScanJournal(f);
                if (info != null) list.Add(info);
            }
        }
        catch { /* каталог недоступен — вернём что нашли */ }
        return list.OrderByDescending(m => m.StartedAt).ToList();
    }

    /// <summary>Все строки журнала по порядку (повреждённые строки пропускаем).</summary>
    public List<MeetingJournalLine> ReadLines(string journalPath)
    {
        var lines = new List<MeetingJournalLine>();
        try
        {
            foreach (var raw in File.ReadLines(journalPath))
            {
                try
                {
                    var l = JsonSerializer.Deserialize<MeetingJournalLine>(raw);
                    if (l != null) lines.Add(l);
                }
                catch { /* битая строка — пропускаем, остальное читаем */ }
            }
        }
        catch { /* файл могли удалить прямо сейчас */ }
        return lines;
    }
    /// <summary>Асинхронное чтение журнала (P0/§3 аудита 2026-10-15): построчно,
    /// не блокируя UI-поток, отменяемо через ct. Повреждённые строки пропускаем,
    /// недоступный/удалённый файл → пустой список (как у синхронного ReadLines).</summary>
    public async Task<List<MeetingJournalLine>> ReadLinesAsync(string journalPath,
        CancellationToken ct = default)
    {
        var lines = new List<MeetingJournalLine>();
        try
        {
            // FileShare.Read — стор пишет append-only с FileShare.Read: совместимо.
            using var fs = new FileStream(journalPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } raw)
            {
                try
                {
                    var l = JsonSerializer.Deserialize<MeetingJournalLine>(raw);
                    if (l != null) lines.Add(l);
                }
                catch { /* битая строка — пропускаем */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* файл могли удалить прямо сейчас */ }
        return lines;
    }

    /// <summary>Асинхронный список встреч: тот же LoadAll, но на пуле потоков —
    /// перечисление каталога и парсинг sidecar'ов не держат UI-поток.</summary>
    public Task<List<MeetingInfo>> LoadAllAsync(CancellationToken ct = default) =>
        Task.Run(() => LoadAll(), ct);


    /// <summary>imageIds последнего сообщения роли "user" с картинками, на которое
    /// в журнале НЕТ следующей строки "answer" (прерванная отправка → восстановление
    /// черновика без автозапроса). Пустой список — такого сообщения нет. Best-effort:
    /// неизвестный/недоступный meetingId → пусто.</summary>
    public IReadOnlyList<string> FindLastUnansweredImageIds(string meetingId)
    {
        try
        {
            var path = Path.Combine(_dir, meetingId + ".jsonl");
            if (!File.Exists(path))
                return [];
            var lines = ReadLines(path);
            var answered = false;
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var l = lines[i];
                if (l.Role == "answer") answered = true;
                else if (l.Role == "user" && l.ImageIds is { Length: > 0 })
                    return answered ? [] : l.ImageIds;
            }
            return [];
        }
        catch { return []; }
    }

    /// <summary>Человекочитаемый транскрипт: [hh:mm:ss] Роль: текст + метки
    /// «[Изображение: имя]» за каждым ImageIds. imageNameResolver переводит
    /// imageId в имя файла (null/промах → короткий id с обрезкой до 12 символов).</summary>
    public static string FormatTranscript(IEnumerable<MeetingJournalLine> lines,
        Func<string, string?>? imageNameResolver = null)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            var label = l.Role == "question" && !string.IsNullOrEmpty(l.Speaker)
                ? l.Speaker
                : RoleLabel(l.Role);
            sb.Append('[').Append(l.Ts).Append("] ").Append(label).Append(": ");
            if (!string.IsNullOrEmpty(l.Text))
                sb.Append(l.Text);
            if (l.ImageIds != null)
                foreach (var id in l.ImageIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                    sb.Append('[').Append(UiText.Tr("Изображение", "Image")).Append(": ")
                      .Append(imageNameResolver?.Invoke(id) ?? ShortImageId(id)).Append(']');
                }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Короткая форма imageId для метки в тексте (img-abcdef123456… → первые 12 + …).</summary>
    private static string ShortImageId(string imageId) =>
        imageId.Length <= 12 ? imageId : imageId[..12] + "…";

    public static string RoleLabel(string role) => role switch
    {
        "question" => UiText.Tr("Собеседник", "Interviewer"),
        "user" => UiText.Tr("Вы", "You"),
        "answer" => UiText.Tr("ИИ", "AI"),
        _ => role
    };

    // Сканирование jsonl без sidecar'а: первая/последняя валидные строки дают
    // время старта, конец, число реплик и превью. StartedAt — из имени файла.
    private static MeetingInfo? ScanJournal(string path)
    {
        try
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var started = DateTime.TryParseExact(id, "yyyy-MM-dd_HH-mm-ss", null,
                System.Globalization.DateTimeStyles.None, out var p) ? p : File.GetCreationTime(path);
            var info = new MeetingInfo { Id = id, JournalPath = path, StartedAt = started, LastLineAt = started };
            string? lastTs = null;
            foreach (var raw in File.ReadLines(path))
            {
                try
                {
                    var l = JsonSerializer.Deserialize<MeetingJournalLine>(raw);
                    if (l == null) continue;
                    info.LineCount++;
                    if (info.FirstLine.Length == 0 && !string.IsNullOrEmpty(l.Text))
                        info.FirstLine = l.Text.Replace("\r", " ").Replace('\n', ' ');
                    if (!string.IsNullOrEmpty(l.Ts)) lastTs = l.Ts;
                }
                catch { }
            }
            if (lastTs != null && DateTime.TryParseExact(lastTs, "HH:mm:ss", null,
                    System.Globalization.DateTimeStyles.None, out var t))
                info.LastLineAt = started.Date + t.TimeOfDay;
            return info;
        }
        catch { return null; }
    }

    private void WriteMetaLocked(MeetingInfo info)
    {
        try { WriteMeta(info); }
        catch { /* sidecar best-effort: журнал важнее */ }
    }

    private void WriteMeta(MeetingInfo info)
    {
        var path = Path.Combine(_dir, info.Id + ".meta.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(info, MetaOpts));
        File.Move(temporary, path, overwrite: true);
    }
}
