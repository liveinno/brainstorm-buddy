using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrainstormBuddy.Transcription;

/// <summary>Одна строка live-журнала встречи (jsonl). r — роль: question/user/answer.</summary>
public sealed class MeetingJournalLine
{
    [JsonPropertyName("t")] public string Ts { get; set; } = "";
    [JsonPropertyName("r")] public string Role { get; set; } = "";
    [JsonPropertyName("x")] public string Text { get; set; } = "";
    /// <summary>Speaker diarization id ("S1"..). Отсутствует в старых файлах → null.</summary>
    [JsonPropertyName("speaker")] public string? Speaker { get; set; }
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

    // ── Производные для списка (считать строки в XAML-биндингах хрупко). ──
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

    /// <summary>Дописать завершённую реплику в живой журнал. Потокобезопасно, best-effort.</summary>
    public void Append(string role, string text, string? speaker = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_lock)
            {
                if (_currentPath == null)
                {
                    // Первая реплика встречи — создаём файл, имя = момент старта.
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
                }

                var now = DateTime.Now;
                var line = JsonSerializer.Serialize(new MeetingJournalLine
                {
                    Ts = now.ToString("HH:mm:ss"),
                    Role = role,
                    Text = text.Trim(),
                    Speaker = speaker
                }, LineOpts);

                // WriteThrough: байты уходят на диск сразу — kill -9 процесса не теряет реплику.
                using (var fs = new FileStream(_currentPath, FileMode.Append, FileAccess.Write,
                           FileShare.Read, 4096, FileOptions.WriteThrough))
                using (var w = new StreamWriter(fs, new UTF8Encoding(true)))
                    w.WriteLine(line);

                _lastLineAt = now;
                var info = _currentInfo!;
                info.LastLineAt = now;
                info.LineCount++;
                if (info.FirstLine.Length == 0)
                    info.FirstLine = text.Trim().Replace("\r", " ").Replace('\n', ' ');
                WriteMetaLocked(info);
            }
        }
        catch { /* журнал best-effort: ни одна реплика не должна ронять пайплайн STT/LLM */ }
    }

    /// <summary>Сохранить обновлённые метаданные (например, AI-саммари из окна истории).</summary>
    public void SaveInfo(MeetingInfo info)
    {
        try
        {
            lock (_lock)
            {
                // Не даём окну затереть свежие счётчики живой встречи устаревшим снимком.
                if (_currentInfo != null && _currentInfo.Id == info.Id)
                {
                    info.LineCount = _currentInfo.LineCount;
                    info.LastLineAt = _currentInfo.LastLineAt;
                    info.FirstLine = _currentInfo.FirstLine;
                    _currentInfo.Summary = info.Summary;
                }
                WriteMetaLocked(info);
            }
        }
        catch { /* best-effort */ }
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

    /// <summary>Человекочитаемый транскрипт: [hh:mm:ss] Роль: текст (как экспорт .txt).</summary>
    public static string FormatTranscript(IEnumerable<MeetingJournalLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            var label = l.Role == "question" && !string.IsNullOrEmpty(l.Speaker)
                ? l.Speaker
                : RoleLabel(l.Role);
            sb.Append('[').Append(l.Ts).Append("] ").Append(label).Append(": ")
              .Append(l.Text).Append('\n');
        }
        return sb.ToString();
    }

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
        try { File.WriteAllText(Path.Combine(_dir, info.Id + ".meta.json"), JsonSerializer.Serialize(info, MetaOpts)); }
        catch { /* sidecar best-effort: журнал важнее */ }
    }
}
