using System.IO;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Services;
namespace BrainstormBuddy;

/// <summary>
/// Фоновая очередь импорта вложений picker'а: один документ за раз —
/// <see cref="Classifier.Classify"/> → <see cref="AttachmentStore.ImportAsync"/>
/// (копия+sha256, дедуп) → <see cref="DocumentExtractor.ExtractAsync"/> →
/// <see cref="AttachmentStore.SaveSegmentsAsync"/>. Статусы уходят в
/// <see cref="ItemUpdated"/> («В обработке» → «Готов» / «Не удалось прочитать»),
/// подписчики — picker-диалог и чипы оверлея. Прогресс чтения (страницы PDF,
/// файл целиком для остальных форматов) приезжает тем же событием —
/// поля <see cref="ImportItem.ProgressDone"/>/<see cref="ImportItem.ProgressTotal"/>.
///
/// Сессия импорта принадлежит очереди, а не окну (аудит 15.10 §5.2): закрытие
/// диалога — это Hide, работа продолжается; повторное открытие
/// (<see cref="BeginSession"/>) возвращает живой снимок тех же пунктов.
/// «Закрыть» (<see cref="CommitSession"/>) линкует готовые документы к встрече,
/// зафиксированной при enqueue, и вооружает долинковку поздних — факты
/// извлекаются один раз по флагу сессии. «Остановить обработку»
/// (<see cref="StopAll"/>/<see cref="StopItem"/>) прерывает чтение, СОХРАНЯЯ
/// обработанные страницы; повтор (<see cref="RetryItem"/>) доезжает с места
/// остановки через ReuseSegments. <see cref="CancelSession"/> — полный отказ:
/// снимает пункты и чистит сироты. <see cref="ReprocessForMode"/> перечитывает
/// fast-прогоны и OCR-отказы при смене режима PDF (§5.1), «Готово» без
/// пропущенных страниц не трогает, второй документ и дубли фактов не создаёт.
/// Fast-прогон, прочитавший текстовые страницы, но нашедший страницы без
/// текстового слоя/с ошибкой чтения, автоматически продолжается тем же
/// документом в Full/OCR с переиспользованием уже извлечённых сегментов:
/// документ, сегменты и Commit-обработчик остаются одни, OCR не зовётся для
/// полностью текстового PDF. Недоступность/сбой OCR честно оставляет
/// частичный результат с номерами непрочитанных страниц и возможностью повтора.
/// <see cref="Remove"/> снимает один пункт до Commit (те же правила сирот);
/// пункт в работе отменяется свой ссылкой CTS — чтение останавливается
/// на ближайшей проверке токена (между страницами/OCR), сирота чистится.
public sealed class AttachmentImportRunner : IDisposable
{
    private const string LogCategory = "Attach";

    /// <summary>Снимок одного пункта очереди для UI (список picker'а, чипы).
    /// AlreadyLinked — контент-дубликат документа, уже привязанного к текущей
    /// встрече: повторное извлечение и линковка не выполняются.
    /// ProgressDone/ProgressTotal — живой прогресс чтения: число ЗАВЕРШЁННЫХ
    /// единиц (для PDF — страницы в любом порядке завершения), не последний индекс.
    /// StoppedByUser — пользовательская остановка («Остановить»/закрытие с
    /// сохранением): обработанные страницы сохранены, RetryItem продолжит с них.
    /// Reprocessing — пункт поставлен на перечитку (смена режима PDF / повтор).
    /// RetryableWithOcr — отказ устраним полным режимом (OCR): смена режима
    /// перезапускает такой пункт автоматически. Kind/ProcessedMode — чем читали;
    /// SkippedNoText — страницы без результата (пропущенные Fast, не распознанные
    /// OCR, не дошедшие при остановке): по нему UI предлагает повтор/Full.
    /// MeetingId — встреча, зафиксированная при постановке: долинковка идёт к ней,
    /// даже если пользователь переключил встречу. ErrorSeeLog — при ошибке UI
    /// советует открыть лог (полный стек уже записан). Warnings — предупреждения
    /// извлечения для итоговой строки («OCR: N стр.…»). DocStats — пометрика
    /// страниц PDF (Processed/Text/Ocr/Empty) для статусной строки.</summary>
    public sealed record ImportItem(
        int Key, string SourcePath, string DisplayName, long SizeBytes,
        string? DocumentId, string? Error,
        bool ExtractFinished, bool ImportFinished, bool AlreadyLinked = false,
        int ProgressDone = 0, int ProgressTotal = 0, bool ErrorSeeLog = false,
        IReadOnlyList<string>? Warnings = null,
        DocumentKind? Kind = null, PdfReadMode? ProcessedMode = null,
        bool StoppedByUser = false, bool Reprocessing = false,
        bool RetryableWithOcr = false, int SkippedNoText = 0,
        string? MeetingId = null, DocumentPageStats? DocStats = null);

    /// <summary>Зафиксированный документ сессии: линкуется к <paramref name="MeetingId"/>
    /// (встреча на момент выбора файла, НЕ текущая); <paramref name="FactsRequested"/> —
    /// галочка «Извлечь факты» на момент Commit — один раз на документ.</summary>
    public sealed record CommittedItem(string DocumentId, string? MeetingId, bool FactsRequested);

    /// <summary>Смена статуса пункта — вызывается на UI-потоке подписчика.</summary>
    public event Action<ImportItem>? ItemUpdated;

    /// <summary>Документ дошёл до терминала ИЗ сессии Commit — линковка уже отменённой
    /// очереди невозможна, поэтому App подписывается и линкует фоново. Срабатывает
    /// один раз на документ (дедуп внутри runner'а); обработчик обязан быть
    /// идемпотентным по (DocumentId, MeetingId).</summary>
    public event Action<CommittedItem>? CommitSessionHandler;

    /// <summary>Проверка «документ уже привязан к встрече» (ставит picker на время
    /// сессии): (documentId, meetingId на момент enqueue). Вызывается с фонового
    /// потока сразу после импорта: true → извлечение пропускается, пункт
    /// помечается AlreadyLinked.</summary>
    public Func<string, string?, bool>? IsAlreadyLinked { get; set; }

    private readonly AttachmentStore _store;
    private readonly IPdfPageOcr? _pdfOcr;
    private readonly LoggingService? _log;

    private readonly object _gate = new();
    private readonly List<Pending> _queue = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Snapshot> _sessions = new();
    private readonly HashSet<int> _goneKeys = new();
    private readonly HashSet<string> _committedDocIds = new(StringComparer.Ordinal);
    private readonly Dictionary<int, CancellationTokenSource> _inflight = new();
    private CancellationTokenSource? _cts;
    private Task _loopTask = Task.CompletedTask;
    /// <summary>Защита от гонки: LoopAsync ещё выполняет «return» (задача не IsCompleted),
    /// а очередь уже пуста — без флага EnsureLoop не поднял бы новый loop и пункт
    /// висел бы в очереди навсегда (run29: retry по кодировке не доехал).</summary>
    private bool _loopRunning;
    private int _nextKey;
    private bool _commitArmed;
    /// <summary>Галочка «Извлечь факты» на момент Commit — едина на сессию,
    /// передаётся обработчику один раз на документ.</summary>
    private bool _commitFacts;
    /// <summary>Пункты, отложенные к авто-перечитке: смена режима PDF пока пункт
    /// в полёте — дорабатывает текущий прогон, затем встает заново с новым режимом
    /// и reuse уже сохранённых сегментов (ProcessOneAsync.finally).</summary>
    private readonly Dictionary<int, PdfReadOptions> _autoRetry = new();

    private sealed class Pending
    {
        public int Key;
        public string Path = "";
        public string DisplayName = "";
        public long Size;
        public string? EncodingWebName;
        /// <summary>Режим чтения PDF, зафиксированный в момент enqueue (null — Default).</summary>
        public PdfReadOptions? PdfOpts;
        /// <summary>Встреча, которой принадлежит файл на момент выбора — линковка
        /// идёт к ней даже после переключения пользователя на другую встречу.</summary>
        public string? MeetingId;
        /// <summary>Перечитка существующего документа (остановка/смена режима):
        /// ImportAsync пропускается, сегменты переиспользуются через ReuseSegments.</summary>
        public string? ReuseDocumentId;
    }

    private sealed class Snapshot
    {
        public readonly HashSet<string> Completed = new();
        public readonly Dictionary<int, ImportItem> Items = new();
    }

    public AttachmentImportRunner(AttachmentStore store, IPdfPageOcr? pdfOcr, LoggingService? log = null)
    {
        _store = store;
        _pdfOcr = pdfOcr;
        _log = log;
    }

    /// <summary>Новая сессия показа диалога. Возвращает текущие пункты (persisted-состояние).</summary>
    public IReadOnlyList<ImportItem> BeginSession()
    {
        lock (_gate)
        {
            _commitArmed = false;
            return _sessions.Values.SelectMany(s => s.Items.Values)
                .GroupBy(i => i.Key).Select(g => g.Last()).OrderBy(i => i.Key).ToList();
        }
    }

    /// <summary>Есть ли недозавершённая работа (для «Отмена» → не закрывать мгновенно).</summary>
    public bool IsBusy
    {
        get { lock (_gate) return _queue.Count > 0; }
    }

    /// <summary>Снимок пунктов очереди без сброса Commit (для чипов оверлея:
    /// показываем «В обработке» ещё до линковки к встрече).</summary>
    public IReadOnlyList<ImportItem> SnapshotItems()
    {
        lock (_gate)
            return _sessions.Values.SelectMany(s => s.Items.Values)
                .GroupBy(i => i.Key).Select(g => g.Last()).OrderBy(i => i.Key).ToList();
    }

    /// <summary>Запланировать файл; путь-дубликат в очереди пропускается. Возвращает ключ.
    /// <paramref name="pdfOptions"/> — режим чтения PDF на момент импорта (пикер читает
    /// Config.Attachments); null — быстрый режим по умолчанию.</summary>
    /// <param name="meetingId">Встреча, которой будет принадлежать документ —
    /// фиксируется при enqueue: документ долинкуется к ней даже после того, как
    /// пользователь переключил встречу (§5.2).</param>
    public int Enqueue(string path, PdfReadOptions? pdfOptions = null, string? meetingId = null)
    {
        var fi = new FileInfo(path);
        var item = new Pending
        {
            Path = fi.FullName,
            DisplayName = fi.Name,
            Size = fi.Exists ? fi.Length : 0,
            PdfOpts = pdfOptions,
            MeetingId = meetingId,
        };
        lock (_gate)
        {
            if (_pendingPaths.Contains(item.Path))
            {
                // Дедуп по пути: ищем существующий ключ в очереди и в сессиях.
                var inQueue = _queue.FirstOrDefault(q =>
                    string.Equals(q.Path, item.Path, StringComparison.OrdinalIgnoreCase));
                if (inQueue != null) return inQueue.Key;
                var inSession = _sessions.Values.SelectMany(s => s.Items.Values)
                    .FirstOrDefault(i => string.Equals(i.SourcePath, item.Path, StringComparison.OrdinalIgnoreCase));
                return inSession?.Key ?? -1;
            }
            item.Key = ++_nextKey;
            _pendingPaths.Add(item.Path);
            _queue.Add(item);
            EnsureLoop();
            Emit(new ImportItem(item.Key, item.Path, item.DisplayName, item.Size,
                null, null, false, false, MeetingId: item.MeetingId));
            return item.Key;
        }
    }

    /// <summary>Повтор пункта с явной кодировкой (после EncodingAmbiguousException и
    /// подтверждения «прочитать как Windows-1251?»).</summary>
    public void RetryWithEncoding(int key, string encodingWebName)
    {
        _log?.Info($"Attach: RetryWithEncoding key={key} enc={encodingWebName}", LogCategory);
        lock (_gate)
        {
            var p = _queue.FirstOrDefault(q => q.Key == key);
            if (p == null)
            {
                // Файл уже ушёл из очереди с ошибкой — планируем заново с кодировкой.
                var snap = _sessions.Values.SelectMany(s => s.Items.Values)
                    .FirstOrDefault(i => i.Key == key);
                if (snap == null) return;
                var item = new Pending
                {
                    Key = key,
                    Path = snap.SourcePath,
                    DisplayName = snap.DisplayName,
                    Size = snap.SizeBytes,
                    EncodingWebName = encodingWebName,
                    MeetingId = snap.MeetingId,
                    ReuseDocumentId = snap.StoppedByUser || snap.Error != null ? snap.DocumentId : null,
                };
                _pendingPaths.Add(item.Path);
                _queue.Insert(0, item);
                EnsureLoop();
                Emit(new ImportItem(key, item.Path, item.DisplayName, item.Size,
                    null, null, false, false, Reprocessing: true, MeetingId: item.MeetingId));
                return;
            }
            p.EncodingWebName = encodingWebName;
        }
    }

    /// <summary>«Остановить обработку» для одного пункта (§5.2): пункт в очереди
    /// снимается; чтение в полёте прерывается по его токену, уже обработанные
    /// страницы сохраняются (KeepPartialOnCancel); сирота не удаляется —
    /// <see cref="RetryItem"/> продолжит с того же места. Статус: StoppedByUser.</summary>
    public void StopItem(int key)
    {
        ImportItem? last = null;
        CancellationTokenSource? inflight = null;
        bool wasQueued;
        lock (_gate)
        {
            var p = _queue.FirstOrDefault(q => q.Key == key);
            wasQueued = p != null;
            if (p != null)
            {
                _queue.Remove(p);
                _pendingPaths.Remove(p.Path);
            }
            if (_inflight.TryGetValue(key, out var c)) { inflight = c; _inflight.Remove(key); }
            foreach (var snap in _sessions.Values)
                if (snap.Items.TryGetValue(key, out var it)) last = it;
        }
        if (wasQueued && last != null)
            Emit(last with { StoppedByUser = true, Reprocessing = false });
        // Отмена вне лока: колбэки токена ходят в тот же стор.
        try { inflight?.Cancel(); } catch (ObjectDisposedException) { /* уже отменён */ }
    }

    /// <summary>«Остановить обработку» всей сессии (§5.2): очередь пустеет,
    /// каждый пункт в полёте прерывается с сохранением частичных страниц;
    /// «Готово» остаётся «Готово». Повтор — <see cref="RetryItem"/> на пункте.</summary>
    public void StopAll()
    {
        List<int> queuedKeys;
        List<CancellationTokenSource> inflight;
        List<ImportItem> stopped = new();
        lock (_gate)
        {
            queuedKeys = _queue.Select(q => q.Key).ToList();
            _queue.Clear();
            _pendingPaths.Clear();
            inflight = _inflight.Values.ToList();
            _inflight.Clear();
            foreach (var snap in _sessions.Values)
                foreach (var it in snap.Items.Values)
                    if (!it.ExtractFinished && it.Error == null && !it.AlreadyLinked)
                        stopped.Add(it with { StoppedByUser = true, Reprocessing = false });
        }
        foreach (var it in stopped) Emit(it);
        foreach (var c in inflight)
            try { c.Cancel(); } catch (ObjectDisposedException) { /* уже отменён */ }
    }

    /// <summary>Повтор/продолжение пункта из терминального состояния (StoppedByUser,
    /// Error): встает заново тем же ключом; при наличии DocumentId сегменты
    /// переиспользуются — перечитываются только недостающие страницы, факты и
    /// документ не дублируются. «Готово»/AlreadyLinked не перезапускаются.</summary>
    public bool RetryItem(int key, PdfReadOptions? pdfOptions = null)
    {
        lock (_gate)
        {
            if (_inflight.ContainsKey(key) || _queue.Any(q => q.Key == key)) return false;
            var snap = _sessions.Values.SelectMany(s => s.Items.Values)
                .Where(i => i.Key == key)
                .OrderByDescending(i => i.ExtractFinished) // свежий эмит последним
                .LastOrDefault();
            if (snap == null) return false;
            if (snap.ExtractFinished && snap.Error == null && !snap.StoppedByUser
                && snap.SkippedNoText == 0) return false; // «Готово» без пропусков
            var item = new Pending
            {
                Key = key,
                Path = snap.SourcePath,
                DisplayName = snap.DisplayName,
                Size = snap.SizeBytes,
                PdfOpts = pdfOptions,
                MeetingId = snap.MeetingId,
                ReuseDocumentId = snap.DocumentId,
            };
            _pendingPaths.Add(item.Path);
            _queue.Insert(0, item);
            EnsureLoop();
            Emit(new ImportItem(key, item.Path, item.DisplayName, item.Size,
                null, null, false, false, Reprocessing: true, MeetingId: item.MeetingId));
            return true;
        }
    }

    /// <summary>Смена режима PDF на лету (§5.1): fast-прогоны с пропущенными
    /// страницами и OCR-устранимые ошибки перезапускаются с новым режимом;
    /// в очереди обновляются опции; пункт в полёте дорабатывает и встает заново
    /// (reuse сегментов — без дублей документа и фактов). «Готово» полного
    /// прогона не трогается. Возвращает число перезапущенных пунктов.</summary>
    public int ReprocessForMode(PdfReadMode mode, int ocrParallelism, int ocrMaxPages = 0)
    {
        var opts = new PdfReadOptions(mode, ocrMaxPages, ocrParallelism)
            with { KeepPartialOnCancel = true };
        int count = 0;
        lock (_gate)
        {
            // Очередь: просто обновить опции — режим читается при старте.
            // Потолок OCR-страниц сохраняем от исходного enqueue (0 = без потолка).
            foreach (var p in _queue)
                if (IsPdfPath(p.Path))
                {
                    p.PdfOpts = new PdfReadOptions(mode,
                        p.PdfOpts?.OcrMaxPages ?? 0, ocrParallelism,
                        KeepPartialOnCancel: true);
                    Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size,
                        null, null, false, false, Reprocessing: true,
                        MeetingId: p.MeetingId));
                    count++;
                }
            var items = _sessions.Values.SelectMany(s => s.Items.Values)
                .GroupBy(i => i.Key).Select(g => g.Last()).ToList();
            foreach (var it in items)
            {
                var pdf = it.Kind == DocumentKind.Pdf
                          || (it.Kind == null && IsPdfPath(it.SourcePath));
                if (!pdf || _goneKeys.Contains(it.Key)) continue;
                if (_inflight.ContainsKey(it.Key))
                {
                    // Полёт: не дёргаем чтение — после терминального эмита пункт
                    // встанет заново уже с новым режимом и reuse сегментов.
                    if (it.Kind != null && it.ProcessedMode != null && it.ProcessedMode.Value != mode)
                        _autoRetry[it.Key] = opts;
                    continue;
                }
                var needsFull = mode == PdfReadMode.Full &&
                    (it.RetryableWithOcr || it.SkippedNoText > 0);
                var staleMode = it.ProcessedMode != null && it.ProcessedMode.Value != mode;
                if (it.ExtractFinished && it.Error == null && !it.AlreadyLinked
                    && (needsFull || (staleMode && it.StoppedByUser)))
                {
                    if (EnqueueAgain(it, opts)) count++;
                }
                else if (it.Error != null && needsFull)
                {
                    if (EnqueueAgain(it, opts)) count++;
                }
            }
        }
        return count;

        bool EnqueueAgain(ImportItem snap, PdfReadOptions o)
        {
            if (_pendingPaths.Contains(snap.SourcePath) && _queue.Any(q => q.Key == snap.Key))
                return false;
            var p = new Pending
            {
                Key = snap.Key,
                Path = snap.SourcePath,
                DisplayName = snap.DisplayName,
                Size = snap.SizeBytes,
                PdfOpts = o,
                MeetingId = snap.MeetingId,
                ReuseDocumentId = snap.DocumentId,
            };
            _pendingPaths.Add(p.Path);
            _queue.Insert(0, p);
            EnsureLoop();
            Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size,
                null, null, false, false, Reprocessing: true, MeetingId: p.MeetingId));
            return true;
        }
    }

    private static bool IsPdfPath(string path) =>
        path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// «Убрать» документ до Commit: снимает пункт из очереди и снимков,
    /// блокирует его позднюю линковку (CommitIfArmed), отменяет чтение в полёте
    /// и удаляет сироту-импорт (копия без связей со встречами/фактов).
    /// Возвращает последний известный снимок пункта либо null.
    /// </summary>
    public ImportItem? Remove(int key)
    {
        List<string> orphans = new();
        ImportItem? last = null;
        CancellationTokenSource? inflight = null;
        lock (_gate)
        {
            var p = _queue.FirstOrDefault(q => q.Key == key);
            if (p != null)
            {
                _queue.Remove(p);
                _pendingPaths.Remove(p.Path);
            }
            _goneKeys.Add(key);
            // Пункт в работе: отмена по его токену — экстрактор остановится
            // на ближайшей проверке (между страницами/OCR), сирота дочистится.
            if (_inflight.TryGetValue(key, out inflight))
                _inflight.Remove(key);
            foreach (var snap in _sessions.Values)
                if (snap.Items.Remove(key, out var it) && it != null)
                {
                    last = it;
                    if (it.DocumentId != null)
                        snap.Completed.Remove(it.DocumentId);
                }
            if (last?.DocumentId != null && !_committedDocIds.Contains(last.DocumentId))
                orphans.Add(last.DocumentId);
        }
        // Отмену выполняем вне лока: колбэки токена могут трогать тот же стор.
        try { inflight?.Cancel(); } catch (ObjectDisposedException) { /* уже отменён */ }
        foreach (var docId in orphans) _ = DeleteIfOrphanedAsync(docId);
        return last;
    }

    /// <summary>
    /// Отмена диалога: обрывает очередь, отменяет чтение в полёте, снимает ВСЕ
    /// пункты сессии и удаляет временные сироты-импорты этой операции (спека §5:
    /// «Отмена… удаляет только временные копии этой операции»). Привязанные ко
    /// встречам документы остаются — DeleteIfOrphanedAsync их не трогает.
    /// </summary>
    public void CancelSession()
    {
        List<string> orphans = new();
        List<CancellationTokenSource> inflight;
        lock (_gate)
        {
            _commitArmed = false;
            _commitFacts = false;
            _autoRetry.Clear();
            _queue.Clear();
            _pendingPaths.Clear();
            _cts?.Cancel();
            inflight = _inflight.Values.ToList();
            _inflight.Clear();
            foreach (var snap in _sessions.Values)
            {
                foreach (var it in snap.Items.Values)
                {
                    _goneKeys.Add(it.Key);
                    if (it.DocumentId != null && !_committedDocIds.Contains(it.DocumentId))
                        orphans.Add(it.DocumentId);
                }
                snap.Items.Clear();
                snap.Completed.Clear();
            }
        }
        foreach (var c in inflight)
            try { c.Cancel(); } catch (ObjectDisposedException) { /* уже отменён */ }
        foreach (var docId in orphans) _ = DeleteIfOrphanedAsync(docId);
    }

    /// <summary>Пометить документ как зафиксированный (линковка уже выполняется
    /// вызывающим). Защита от удаления сиротой в гонке Remove/CancelSession.</summary>
    public void MarkCommitted(string documentId)
    {
        lock (_gate) _committedDocIds.Add(documentId);
    }

    /// <summary>«Применить»/«Закрыть с линковкой»: вооружает долинковку — каждый
    /// документ, дошедший до терминала, уходит в CommitSessionHandler с встречей,
    /// зафиксированной при enqueue. Уже ГОТОВЫЕ до нажатия документы сбрасываются
    /// обработчику сразу (flush), иначе бы висели нелинкованными.
    /// <paramref name="extractFacts"/> — галочка «Извлечь факты» на момент Commit;
    /// передаётся обработчику один раз на документ (дедуп внутри).</summary>
    public void CommitSession(bool extractFacts)
    {
        List<CommittedItem> flush = new();
        lock (_gate)
        {
            _commitArmed = true;
            _commitFacts = extractFacts;
            foreach (var it in CurrentSnapshot().Items.Values)
                if (it.DocumentId != null && it.ImportFinished && it.ExtractFinished
                    && !it.AlreadyLinked && it.Error == null
                    && !_committedDocIds.Contains(it.DocumentId))
                {
                    _committedDocIds.Add(it.DocumentId);
                    flush.Add(new CommittedItem(it.DocumentId, it.MeetingId, extractFacts));
                }
        }
        foreach (var c in flush)
            try { CommitSessionHandler?.Invoke(c); }
            catch (Exception ex) { _log?.Warn($"Attach: CommitSessionHandler упал: {ex.Message}", LogCategory); }
    }

    private void EnsureLoop()
    {
        // Отменённый сессией CTS больше не годится — иначе loop уйдёт сразу.
        if (_cts?.IsCancellationRequested == true)
        {
            _cts.Dispose();
            _cts = null;
        }
        _cts ??= new CancellationTokenSource();
        if (!_loopRunning)
        {
            _loopRunning = true;
            _loopTask = Task.Run(LoopAsync);
        }
    }

    private void Emit(ImportItem item)
    {
        lock (_gate)
        {
            // Снятые пункты (Remove/CancelSession) не попадают в снимки
            // и не будят подписчиков — иначе строка «воскреснет» в списке.
            if (_goneKeys.Contains(item.Key)) return;
            var snap = CurrentSnapshot();
            snap.Items[item.Key] = item;
            if (item.DocumentId != null && item.ImportFinished) snap.Completed.Add(item.DocumentId);
        }
        try { ItemUpdated?.Invoke(item); }
        catch (Exception ex) { _log?.Warn($"Attach: подписчик ItemUpdated упал: {ex.Message}", LogCategory); }
    }

    /// <summary>Живой прогресс чтения: пробрасываем снимок пункта с обновлёнными
    /// Done/Total тем же событием — UI-подписчик обновляет строку на месте.</summary>
    private void ReportProgress(int key, ExtractionProgress pr)
    {
        ImportItem item;
        lock (_gate)
        {
            if (_goneKeys.Contains(key)) return;
            var snap = CurrentSnapshot();
            if (!snap.Items.TryGetValue(key, out var prev) || prev == null) return;
            item = prev with { ProgressDone = pr.Done, ProgressTotal = pr.Total };
        }
        Emit(item);
    }

    /// <summary>IProgress, который доставляет отчёт в Emit синхронно с потока
    /// экстрактора — без захвата SynchronizationContext (порядок эмитов сохраняется).</summary>
    private sealed class InlineProgress : IProgress<ExtractionProgress>
    {
        private readonly Action<ExtractionProgress> _report;
        public InlineProgress(Action<ExtractionProgress> report) => _report = report;
        public void Report(ExtractionProgress value) => _report(value);
    }

    /// <summary>Сирота-очистка временной копии: удаляет только документ без
    /// связей со встречами/фактов и без Commit-защиты (атомарно в store).</summary>
    private async Task DeleteIfOrphanedAsync(string documentId)
    {
        try
        {
            bool committed;
            lock (_gate) committed = _committedDocIds.Contains(documentId);
            if (committed) return;
            if (await _store.DeleteIfOrphanedAsync(documentId).ConfigureAwait(false))
                _log?.Info($"Attach: временная копия {documentId} удалена (сирота)", LogCategory);
        }
        catch (Exception ex) { _log?.Warn($"Attach: сирота-очистка {documentId} упала: {ex.Message}", LogCategory); }
    }

    private Snapshot CurrentSnapshot()
    {
        // Один сквозной снимок на runner: показ диалога не стирает историю прошлого Commit.
        if (!_sessions.TryGetValue(0, out var s))
            _sessions[0] = s = new Snapshot();
        return s;
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            Pending? p;
            lock (_gate)
            {
                if (_queue.Count == 0 || _cts?.IsCancellationRequested == true)
                {
                    _loopRunning = false;
                    _cts?.Dispose();
                    _cts = null;
                    return;
                }
                p = _queue[0];
                _queue.RemoveAt(0);
                _pendingPaths.Remove(p.Path);
            }
            // Непойманное исключение не должно убивать loop — иначе очередь мёртва
            // до конца сессии (флаг _loopRunning снялся бы только на выходе).
            try { await ProcessOneAsync(p).ConfigureAwait(false); }
            catch (Exception ex) { _log?.Error($"Attach: loop упал на '{p.DisplayName}'", ex, LogCategory); }
        }
    }

    private async Task ProcessOneAsync(Pending p)
    {
        // Свой CTS на пункт: Remove(key) отменяет именно чтение этого файла,
        // сессия отменяет всё разом. Экстрактор проверяет токен между страницами.
        // _cts читаем только под _gate — LoopAsync диспозит его под тем же локом.
        CancellationTokenSource? itemCts = null;
        CancellationToken ct;
        lock (_gate)
        {
            var sessionCt = _cts?.Token ?? CancellationToken.None;
            if (!_goneKeys.Contains(p.Key))
                _inflight[p.Key] = itemCts =
                    CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
            ct = itemCts?.Token ?? sessionCt;
        }
        string? docId = null;
        DocumentKind kind = DocumentKind.Txt;
        var kindStr = "?";
        // Фактический режим последнего прогона: fast может автоматически
        // продолжиться в Full/OCR — статус отражает то, чем реально читали.
        var effectiveMode = (p.PdfOpts ?? PdfReadOptions.Default).Mode;
        DocumentPageStats? docStats = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var extractor = new DocumentExtractor();
            var progress = new InlineProgress(pr => ReportProgress(p.Key, pr));
            var pdfOpts = p.PdfOpts ?? PdfReadOptions.Default;
            ExtractedDocument extracted;
            // Отпечаток запрошенных условий — пишется при успешном прогоне и
            // сравнивается с сохранённым при повторном импорте того же файла.
            string? extractKey = null;
            var reusedStored = false;
            var doc = p.ReuseDocumentId != null ? _store.Get(p.ReuseDocumentId) : null;
            if (doc != null)
            {
                // Перечитка (остановка/смена режима): документ и сегменты уже есть —
                // импорта и дедупа нет, дочитываем недостающее через ReuseSegments.
                docId = doc.DocumentId;
                kind = doc.Kind;
                kindStr = kind.ToString();
                extractKey = ExtractionFingerprint.For(kind, pdfOpts, _pdfOcr);
                Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId,
                    null, false, true, Kind: kind,
                    ProcessedMode: kind == DocumentKind.Pdf ? pdfOpts.Mode : null,
                    MeetingId: p.MeetingId));
                var prior = await _store.LoadSegmentsAsync(docId, ct).ConfigureAwait(false);
                var run = await RunWithAutoOcrAsync(prior).ConfigureAwait(false);
                extracted = run.Doc;
                effectiveMode = run.Mode;
                if (run.Continued) reusedStored = false;
                if (run.Failed)
                {
                    // OCR-продолжение не удалось: не помечаем fast-результат
                    // завершённым, иначе повторный импорт не попробует снова.
                    extractKey = null;
                    reusedStored = false;
                }
                else if (run.Continued)
                    extractKey = ExtractionFingerprint.For(kind,
                        pdfOpts with { Mode = PdfReadMode.Full }, _pdfOcr);
            }
            else
            {
                kind = Classifier.Classify(p.Path);
                kindStr = kind.ToString();
                doc = await _store.ImportAsync(p.Path, p.DisplayName, kind, ct).ConfigureAwait(false);
                docId = doc.DocumentId;
                // Дедуп по контенту: документ уже привязан к этой встрече →
                // повторное извлечение и линковка не нужны (спека §4).
                if (IsAlreadyLinked?.Invoke(docId, p.MeetingId) == true)
                {
                    Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId,
                        null, false, true, AlreadyLinked: true, Kind: kind,
                        MeetingId: p.MeetingId));
                    return;
                }
                Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId, null,
                    false, true, Kind: kind,
                    ProcessedMode: kind == DocumentKind.Pdf ? pdfOpts.Mode : null,
                    MeetingId: p.MeetingId));
                if (ct.IsCancellationRequested) { CommitIfArmed(p.Key, docId, p.MeetingId); return; }
                extractKey = ExtractionFingerprint.For(kind, pdfOpts, _pdfOcr);
                // Повторный импорт того же содержимого (дедуп по sha256, в т.ч.
                // после рестарта): сохранённые сегменты переиспользуются без
                // обращения к экстрактору/OCR, если прогон был доведён до конца
                // и условия извлечения совпали. Частичный прогон отпечаток
                // сбрасывает → читаем заново.
                var restored = string.Equals(doc.ExtractKey, extractKey, StringComparison.Ordinal)
                    || (pdfOpts.Mode == PdfReadMode.Fast
                        && ExtractionFingerprint.CoversFastRequest(doc.ExtractKey, pdfOpts))
                    ? await RestoreStoredAsync(doc, extractKey, kind, pdfOpts,
                        storedFullRun: ExtractionFingerprint.IsFullPdfRun(doc.ExtractKey))
                        .ConfigureAwait(false)
                    : null;
                if (restored != null)
                {
                    reusedStored = true;
                    // Full-прогон — надмножество Fast: показываем фактический
                    // режим сохранённого текста, OCR повторно не запускаем.
                    var restoredFull = ExtractionFingerprint.IsFullPdfRun(doc.ExtractKey);
                    var run = await ContinueWithOcrAsync(restored).ConfigureAwait(false);
                    extracted = run.Doc;
                    effectiveMode = !run.Continued && !run.Failed && restoredFull
                        ? PdfReadMode.Full : run.Mode;
                    // Продолжение переписало сегменты (OCR-страницы) — сохраняем.
                    if (run.Continued || run.Failed) reusedStored = false;
                    if (run.Failed) extractKey = null;
                    else if (run.Continued)
                        extractKey = ExtractionFingerprint.For(kind,
                            pdfOpts with { Mode = PdfReadMode.Full }, _pdfOcr);
                }
                else
                {
                    var run = await RunWithAutoOcrAsync(null).ConfigureAwait(false);
                    extracted = run.Doc;
                    effectiveMode = run.Mode;
                    if (run.Continued) reusedStored = false;
                    if (run.Failed) extractKey = null;
                    else if (run.Continued)
                        extractKey = ExtractionFingerprint.For(kind,
                            pdfOpts with { Mode = PdfReadMode.Full }, _pdfOcr);
                }
            }

            // Fast-прогон прочитал часть страниц, но не все: автоматически
            // продолжаем тот же документ в Full/OCR. Уже извлечённые сегменты
            // (якоря/порядок страниц) переиспользуются, документ и Commit —
            // те же. Если OCR недоступен, остаёмся на честном fast-результате.
            async Task<(ExtractedDocument Doc, PdfReadMode Mode, bool Continued, bool Failed)>
                RunWithAutoOcrAsync(IReadOnlyList<DocSegment>? prior)
            {
                var sourcePath = _store.GetSourcePath(doc!);
                ExtractedDocument first;
                try
                {
                    first = prior != null
                        ? await extractor.ExtractReuseAsync(sourcePath, kind, p.DisplayName,
                            _pdfOcr, prior, pdfOpts, progress, ct,
                            encodingWebName: p.EncodingWebName).ConfigureAwait(false)
                        : await extractor.ExtractAsync(sourcePath, kind, p.DisplayName, _pdfOcr,
                            ct, p.EncodingWebName, pdfOpts, progress).ConfigureAwait(false);
                }
                catch (AttachmentRejectedException rej)
                    when (kind == DocumentKind.Pdf && pdfOpts.Mode == PdfReadMode.Fast
                          && rej.RetryableWithOcr && _pdfOcr is { IsAvailable: true })
                {
                    // Полностью скан: fast не прочитал ни одной текстовой страницы.
                    // Отказ с советом «включите Full» не нужен — сразу OCR.
                    _log?.Info(
                        $"Attach: '{p.DisplayName}' doc={docId} fast: текстового слоя нет — продолжаю OCR",
                        LogCategory);
                    var full = await ExtractFullAsync(sourcePath, null).ConfigureAwait(false);
                    return (full, PdfReadMode.Full, true, false);
                }
                return await ContinueWithOcrAsync(first).ConfigureAwait(false);
            }

            async Task<(ExtractedDocument Doc, PdfReadMode Mode, bool Continued, bool Failed)>
                ContinueWithOcrAsync(ExtractedDocument start)
            {
                if (!CanAutoContinue(start)) return (start, pdfOpts.Mode, false, false);

                _log?.Info(
                    $"Attach: '{p.DisplayName}' doc={docId} fast: " +
                    $"{start.SkippedNoTextPages} стр. без текста — продолжаю OCR",
                    LogCategory);
                Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId,
                    null, false, true, Reprocessing: true, Kind: kind,
                    ProcessedMode: PdfReadMode.Full, MeetingId: p.MeetingId));
                try
                {
                    var full = await ExtractFullAsync(
                        _store.GetSourcePath(doc!), start.Segments).ConfigureAwait(false);
                    return (full, PdfReadMode.Full, true, false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Остановка во время авто-OCR: уже прочитанные страницы
                    // быстрого прогона не выбрасываем — outer сохранит их как
                    // частичный результат, повтор дочитает с этих сегментов.
                    _log?.Info(
                        $"Attach: '{p.DisplayName}' doc={docId}: OCR-продолжение остановлено — " +
                        $"сохраняю {start.Segments.Count} стр. быстрого прогона",
                        LogCategory);
                    return (start with
                    {
                        Interrupted = true,
                        Warnings = [.. start.Warnings, "OCR: продолжение остановлено пользователем"]
                    }, PdfReadMode.Full, false, true);
                }
                catch (AttachmentRejectedException rej)
                {
                    // Текстовые страницы уже прочитаны — не теряем их из-за
                    // сбоя OCR-продолжения; страницы без результата остаются
                    // в предупреждениях и доступны для повтора.
                    _log?.Warn(
                        $"Attach: '{p.DisplayName}' doc={docId}: OCR-продолжение не удалось — {rej.Message}",
                        LogCategory);
                    return (start with
                    {
                        Warnings = [.. start.Warnings, $"OCR: продолжение не удалось — {rej.Message}"]
                    }, pdfOpts.Mode, false, true);
                }
            }

            async Task<ExtractedDocument> ExtractFullAsync(
                string sourcePath, IReadOnlyList<DocSegment>? prior)
            {
                var fullOpts = pdfOpts with
                {
                    Mode = PdfReadMode.Full,
                    // Авто-продолжение после fast — это тот же импорт: остановка
                    // пользователя сохраняет и уже распознанные OCR-страницы.
                    KeepPartialOnCancel = true
                };
                return prior is { Count: > 0 }
                    ? await extractor.ExtractReuseAsync(sourcePath, kind, p.DisplayName,
                        _pdfOcr, prior, fullOpts, progress, ct,
                        encodingWebName: p.EncodingWebName).ConfigureAwait(false)
                    : await extractor.ExtractAsync(sourcePath, kind, p.DisplayName, _pdfOcr,
                        ct, p.EncodingWebName, fullOpts, progress).ConfigureAwait(false);
            }

            bool CanAutoContinue(ExtractedDocument start) =>
                kind == DocumentKind.Pdf
                && pdfOpts.Mode == PdfReadMode.Fast
                && !start.Interrupted
                && start.Segments.Count > 0
                && start.SkippedNoTextPages > 0
                && _pdfOcr is { IsAvailable: true };
            // PageCount — число различных меток вида «Стр. N» (у не-PDF сегменты
            // несут anchor'ы lines/section/sheet → 0).
            var pageCount = extracted.Segments
                .Where(s => s.Anchor.Kind == "page")
                .Select(s => s.Anchor.Label.Split(' ').Skip(1).FirstOrDefault())
                .Where(n => n != null).Distinct().Count();
            if (kind == DocumentKind.Pdf && extracted.TotalPages > 0)
                docStats = new DocumentPageStats(extracted.TotalPages, extracted.ProcessedPages,
                    extracted.TextLayerPages, extracted.OcrPages, extracted.EmptyPages);
            // Переиспользованный прогон уже сохранён этим же store — файл сегментов
            // и счётчики не переписываем. Частичный прогон ключ сбрасывает (null):
            // его сегменты для повторного импорта не годятся.
            if (!reusedStored)
                await _store.SaveSegmentsAsync(docId, extracted.Segments,
                    extracted.CharCount, pageCount,
                    // Остановка отменяет item-токен; частичный результат всё
                    // равно обязан лечь на диск — иначе «сохранено N стр.»
                    // ложно и повтор начнётся с нуля.
                    extracted.Interrupted ? CancellationToken.None : ct,
                    extracted.Warnings, docStats,
                    extractKey: extracted.Interrupted ? null : extractKey).ConfigureAwait(false);
            _log?.Info(
                $"Attach: {(reusedStored ? "переиспользован" : "извлечён")} '{p.DisplayName}' doc={docId} kind={kindStr} " +
                $"size={p.Size}Б pages={pageCount} mode={(kind == DocumentKind.Pdf ? effectiveMode.ToString() : "—")} " +
                $"{sw.ElapsedMilliseconds}мс warnings={extracted.Warnings.Count}" +
                (extracted.Interrupted ? " INTERRUPTED(частично сохранено)" : ""),
                LogCategory);
            if (extracted.Interrupted)
            {
                // Остановка: обработанные страницы сохранены — RetryItem доедет с них.
                Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId, null,
                    true, true, Kind: kind,
                    ProcessedMode: kind == DocumentKind.Pdf ? effectiveMode : null,
                    StoppedByUser: true, Warnings: extracted.Warnings,
                    SkippedNoText: extracted.SkippedNoTextPages,
                    DocStats: docStats, MeetingId: p.MeetingId));
                return;
            }
            _log?.Info(
                $"Attach: ок '{p.DisplayName}' doc={docId ?? "—"} kind={kindStr} size={p.Size}Б " +
                $"{sw.ElapsedMilliseconds}мс segs={extracted.Segments?.Count ?? -1}",
                LogCategory);
            Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId, null,
                true, true, ProgressDone: 1, ProgressTotal: 1, Kind: kind,
                ProcessedMode: kind == DocumentKind.Pdf ? effectiveMode : null,
                SkippedNoText: extracted.SkippedNoTextPages,
                Warnings: extracted.Warnings, DocStats: docStats, MeetingId: p.MeetingId));
            CommitIfArmed(p.Key, docId!, p.MeetingId);
        }
        catch (EncodingAmbiguousException amb)
        {
            _log?.Info($"Attach: '{p.DisplayName}' key={p.Key} кодировка неоднозначна, enc={amb.SuggestedEncoding} — ждём выбора", LogCategory);
            // Picker решает: подтверждение «Windows-1251?» → RetryWithEncoding.
            Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId,
                $"ENCODING:{amb.SuggestedEncoding}|{amb.Message}", false, docId != null,
                Kind: kind, MeetingId: p.MeetingId));
        }
        catch (AttachmentRejectedException rej)
        {
            _log?.Info(
                $"Attach: отказ '{p.DisplayName}' doc={docId ?? "—"} kind={kindStr} size={p.Size}Б " +
                $"mode={(kindStr == nameof(DocumentKind.Pdf) ? effectiveMode.ToString() : "—")} " +
                $"{sw.ElapsedMilliseconds}мс — {rej.Message}",
                LogCategory);
            Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId,
                rej.Message, true, docId != null, Kind: kind,
                ProcessedMode: kind == DocumentKind.Pdf
                    ? (p.PdfOpts ?? PdfReadOptions.Default).Mode : null,
                RetryableWithOcr: kind == DocumentKind.Pdf
                    && rej.Message.Contains("OCR", StringComparison.Ordinal),
                MeetingId: p.MeetingId));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Отмена по токену (Remove/CancelSession/Dispose) — не ошибка (спека §6).
            _log?.Info($"Attach: отменено '{p.DisplayName}' doc={docId ?? "—"} kind={kindStr} {sw.ElapsedMilliseconds}мс", LogCategory);
            Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId,
                null, false, docId != null, Kind: kind, MeetingId: p.MeetingId));
            if (docId != null) CommitIfArmed(p.Key, docId, p.MeetingId);
        }
        catch (Exception ex)
        {
            // Сюда же падают OCE без нашей отмены (внутренний таймаут чтения/OCR) —
            // это уже ошибка, и в лог она идёт полностью.
            _log?.Error($"Attach: импорт '{p.DisplayName}' упал doc={docId ?? "—"} kind={kindStr}", ex, LogCategory);
            Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId,
                ex.Message, true, docId != null, ErrorSeeLog: true, Kind: kind,
                MeetingId: p.MeetingId));
            if (docId != null) CommitIfArmed(p.Key, docId, p.MeetingId);
        }
        finally
        {
            PdfReadOptions? retryOpts = null;
            bool gone;
            lock (_gate)
            {
                if (itemCts != null) _inflight.Remove(p.Key);
                gone = _goneKeys.Contains(p.Key);
                // Смена режима прилетела в полёте — встаём заново с новым режимом.
                if (!gone) _autoRetry.Remove(p.Key, out retryOpts);
                else retryOpts = null;
            }
            itemCts?.Dispose();
            // Пункт сняли (Remove/CancelSession) пока он обрабатывался:
            // документ доехал — проверяем и убираем временную сироту.
            if (gone && docId != null)
                await DeleteIfOrphanedAsync(docId).ConfigureAwait(false);
            else if (retryOpts != null && docId != null)
            {
                var re = new Pending
                {
                    Key = p.Key, Path = p.Path, DisplayName = p.DisplayName,
                    Size = p.Size, PdfOpts = retryOpts,
                    MeetingId = p.MeetingId, ReuseDocumentId = docId,
                };
                lock (_gate)
                {
                    _pendingPaths.Add(re.Path);
                    _queue.Insert(0, re);
                    EnsureLoop();
                }
                Emit(new ImportItem(p.Key, p.Path, p.DisplayName, p.Size, docId,
                    null, false, true, Reprocessing: true, Kind: kind,
                    DocStats: docStats, MeetingId: p.MeetingId));
            }
        }

        // Повторный импорт уже извлечённого содержимого: сегменты читаем как есть,
        // метрики — из строки documents (их записал доведённый до конца прогон).
        // Пустой файл сегментов доверия не заслуживает — извлекаем заново.
        async Task<ExtractedDocument?> RestoreStoredAsync(StoredDocument existing, string key,
            DocumentKind requestedKind, PdfReadOptions requestedOptions, bool storedFullRun)
        {
            var prior = await _store.LoadSegmentsAsync(existing.DocumentId, ct).ConfigureAwait(false);
            if (prior.Count == 0) return null;
            var ps = existing.PageStats;
            // Fast-прогон мог пропустить страницы без текстового слоя — их число
            // (Total - Processed) снова отдаём как SkippedNoText, иначе после
            // рестарта «Готово» не предложит дочитать документ в полном режиме.
            // Full-прогон уже пытался OCR-ить пустые страницы: повторный Fast
            // не должен запускать этот OCR заново.
            var skippedNoText = requestedKind == DocumentKind.Pdf
                && requestedOptions.Mode == PdfReadMode.Fast
                && !storedFullRun
                ? Math.Max(0, (ps?.Total ?? 0) - (ps?.Processed ?? 0))
                : 0;
            _log?.Info(
                $"Attach: сегменты '{existing.DisplayName}' doc={existing.DocumentId} " +
                $"kind={existing.Kind} segs={prior.Count} key={key} — прогон совпал, чтение пропущено",
                LogCategory);
            return new ExtractedDocument(prior, existing.CharCount, existing.Warnings,
                Interrupted: false, SkippedNoTextPages: skippedNoText,
                TotalPages: ps?.Total ?? 0, ProcessedPages: ps?.Processed ?? 0,
                TextLayerPages: ps?.TextLayer ?? 0, OcrPages: ps?.Ocr ?? 0,
                EmptyPages: ps?.Empty ?? 0);
        }
    }

    private void CommitIfArmed(int key, string docId, string? meetingId)
    {
        bool armed;
        bool facts;
        lock (_gate)
        {
            armed = _commitArmed && !_goneKeys.Contains(key);
            facts = _commitFacts;
            // Защита от сирота-удаления: зафиксированные документы никогда не сироты.
            if (armed) _committedDocIds.Add(docId);
        }
        if (!armed) return;
        try { CommitSessionHandler?.Invoke(new CommittedItem(docId, meetingId, facts)); }
        catch (Exception ex) { _log?.Warn($"Attach: CommitSessionHandler упал: {ex.Message}", LogCategory); }
    }

    /// <summary>Завершение работы: отмена очереди и освобождение CTS.
    /// Текущий await в цикле сам доотменяется через токен — ждать _loopTask нельзя
    /// (возможен дедлок, если loop ждёт UI-диспетчер).</summary>
    public void Dispose()
    {
        List<CancellationTokenSource> inflight;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            inflight = _inflight.Values.ToList();
            _inflight.Clear();
        }
        foreach (var c in inflight)
            try { c.Cancel(); } catch (ObjectDisposedException) { /* уже отменён */ }
    }
}
