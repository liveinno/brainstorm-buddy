using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Core.Images;
using BrainstormBuddy.Controls;
using BrainstormBuddy.Transcription;

namespace BrainstormBuddy;

/// <summary>
/// Окно истории живых встреч: журналы %APPDATA%\BrainstormBuddy\meetings\*.jsonl
/// (краш-безопасный live-транскрипт). Список слева (дата, длительность, первая реплика),
/// справа — транскрипт с тайм-кодами, копирование, экспорт .txt, AI-саммари
/// (тот же протокол, что и у файловой транскрибации, — MeetingSummarizer), удаление.
/// </summary>
public partial class MeetingHistoryWindow : Window
{
    private readonly MeetingTranscriptStore _store;
    private CancellationTokenSource? _cts;           // саммари (8 мин)
    private CancellationTokenSource? _transcriptCts; // загрузка транскрипта (отмена на смене выбора/закрытии)
    private MeetingInfo? _current;
    /// <summary>Полный текст транскрипта выбранной встречи. Отдельно от
    /// TranscriptBox.Text, т.к. бокс заполняется инкрементально (P0/§3).</summary>
    private string _transcriptFull = "";
    /// <summary>Маркер «инкрементальный рендер транскрипта закончен» (интеграция
    /// §7-поиска, PreciousLark): задача заменяется на старте каждой загрузки в
    /// LoadTranscriptForAsync и завершается true, когда весь текст уже в боксе;
    /// false — отмена (смена выбора/удаление/закрытие) или ошибка чтения.
    /// Брать ПОСЛЕ установки MeetingList.SelectedItem.</summary>
    internal TaskCompletionSource<bool> _transcriptRenderedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Список файлов выбранной встречи для секции «Файлы (N)» (binding ItemsSource).</summary>
    public ObservableCollection<AttachFileVm> AttachFiles { get; } = new();
    /// <summary>Ответы ИИ из журнала встречи (роль «answer») — раздел «Ответы ИИ».</summary>
    public ObservableCollection<AiAnswerVm> AiAnswers { get; } = new();
    /// <summary>Исходный текст AI-саммари С разметкой — как пришёл от модели.
    /// Форматирование применяется только при отрисовке (MarkdownView), поэтому
    /// экспорт .txt сохраняет разметку, а копирование — видимый текст.</summary>
    private string _summaryRaw = "";
    /// <summary>true — в саммари сейчас выделен найденный фрагмент (§7-навигация).</summary>
    private bool _summaryHighlightActive;
    /// <summary>Markdown-компоненты, уже подписанные на клик по ссылке (карточки
    /// ответов ИИ появляются шаблоном — подписка идёт по мере появления).</summary>
    private readonly List<MarkdownView> _linkedViews = new();
    private bool _busy;
    // ---- §7: поиск по встречам ----
    private readonly MeetingSearchService _search;
    /// <summary>true — сервис создан этим окном и должен быть освобождён при
    /// закрытии; false — переиспользуем App.MeetingSearch с E5-провайдером,
    /// чтобы поиск в истории видел перефразы так же, как основной чат.</summary>
    private readonly bool _ownsSearch;
    private CancellationTokenSource? _searchCts;    // текущий запрос (дебаунс сменяет)
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _attachCts; // текущий снимок файлов встречи
    private int _attachGeneration;
    /// <summary>Встреча, чьи строки файлов сейчас показаны в AttachFiles.
    /// Отличается от _current, пока летит снимок новой встречи (старые строки
    /// остаются на экране до атомарного применения): действия со строкой и
    /// точечные обновления обязаны сверяться с ним, а не с _current — иначе
    /// клик по строке прошлой встречи поставил бы fact-джобу с id новой.</summary>
    private string? _attachRowsMeetingId;
    /// <summary>Полный снимок секции файлов в полёте (RefreshAttachFiles до
    /// ApplyAttachRows/выхода). Точечное обновление строки, которой нет в
    /// списке, не перезапускает снимок — он уже везёт свежие данные.</summary>
    private bool _attachSnapshotInFlight;
    /// <summary>Встреча летящего снимка секции файлов. Только для чтения под
    /// _attachSnapshotInFlight: повторный выбор той же встречи (восстановление
    /// выделения после LoadMeetings, §7-навигация) не должен отменять и
    /// перезапускать уже идущий снимок без изменения состава.</summary>
    private string? _attachSnapshotMeetingId;
    /// <summary>Просьба полного снимка секции от точечного обновления
    /// (документ без строки в списке / строка без привязки). Запуск откладывается
    /// до finally UpdateAttachRowAsync — вызов RefreshAttachFiles на летящем
    /// _rowRefreshInFlight возвращал бы документ в _rowRefreshPending и
    /// получался бы вечный цикл «точечное обновление → полный снимок → доигровка
    /// → точечное обновление» (событие FactJobChanged по отвязанному/удалённому
    /// документу с живой джобой крутило полный снимок бесконечно).</summary>
    private bool _attachListRefreshNeeded;
    /// <summary>Поколение LoadMeetings: параллельные перезагрузки списка
    /// (удаление встречи во время стартовой загрузки) — устаревший, более
    /// медленный проход не должен затирать свежий список и выделение.</summary>
    private int _meetingsGeneration;
    /// <summary>Поколение снимка изображений встречи: поздний IO после смены
    /// встречи или закрытия окна не должен применяться к UI.</summary>
    private int _imageGeneration;
    /// <summary>Документы, чья строка файлов сейчас перечитывается на пуле
    /// (UpdateAttachRowAsync): события FactJobChanged приходят чаще, чем читается
    /// SQLite; без сериализации конкурирующие снимки строки могли применяться
    /// не в том порядке — последним применяем свежий.</summary>
    private readonly HashSet<string> _rowRefreshInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rowRefreshPending = new(StringComparer.Ordinal);
    private bool _closed;
    private readonly HashSet<string> _factEnqueueInFlight = new(StringComparer.Ordinal);
    private readonly List<int> _transcriptSearchMatches = new();
    private string _transcriptSearchQuery = "";
    private int _transcriptSearchIndex = -1;
    private int _transcriptSearchVersion;
    private bool _transcriptHasLines;
    /// <summary>Строки журнала текущего транскрипта — нужны навигации §7 для
    /// разрешения RecordId (mid) → LineIndex и офсета.</summary>
    private IReadOnlyList<MeetingJournalLine> _transcriptLines = Array.Empty<MeetingJournalLine>();
    /// <summary>Офсеты начала реплик в отрендеренном тексте (тот же индекс, что у
    /// ReadLines/LineIndex). null — рендер расходится со строковой формулой
    /// (внутритекстные '\n' в реплике) → навигация идёт старыми сканами по '['/Ts.</summary>
    private int[]? _recordStarts;
    /// <summary>mid → LineIndex: стабильный адрес реплики, устойчивый к сдвигам
    /// индексов при дозаписи журнала. При дубликате mid берётся первая строка.</summary>
    private IReadOnlyDictionary<string, int> _midIndex = new Dictionary<string, int>();
    /// <summary>Действия над строкой файла, уже в полёте по await (link/delete/save):
    /// второй клик до завершения первой операции не дублирует её.</summary>
    private readonly HashSet<string> _attachOpsInFlight = new(StringComparer.Ordinal);

    /// <summary>Кусок вывода транскрипта за один проход диспетчера: длинные
    /// встречи (5 ч ≈ 1+ МБ) рендерим инкрементально — один гигантский SetText
    /// в TextBox с переносами это один layout-проход O(n²) и вешал UI ≥20 с
    /// (09:54:23 kill от UiWatchdog, аудит §3).</summary>
    private const int TranscriptChunkChars = 60_000;
    /// <summary>Локализованная строка из Strings.{lang}.xaml по ключу.</summary>
    private string L(string key) => (string)FindResource(key);

    public MeetingHistoryWindow()
    {
        InitializeComponent();
        // Живой стор приложения, если он уже создан (тот же экземпляр, что пишет
        // реплики и ContextState): иначе SaveInfo не видит живых счётчиков, а
        // Delete не сбрасывает текущую встречу. Свой экземпляр — только фолбэк.
        _store = App.Current.MeetingJournal ?? new MeetingTranscriptStore(App.Current.AppDataDir);
        // ItemsSource="{Binding AttachFiles}" в секции файлов смотрит на это окно.
        DataContext = this;
        // Ссылки в markdown-тексте открываются только по клику пользователя
        // и только для безопасных схем (http/https/mailto).
        SummaryView.LinkClicked += (s, e) => MarkdownLinkOpener.Open(e.Url);
        // Карточки ответов ИИ живут в шаблоне ItemsControl — контейнеры создаются
        // после лейаута, поэтому подписка на клик по ссылке идёт по мере появления
        // (повторные подписки отсекаются множеством).
        AiAnswersList.LayoutUpdated += (_, _) => HookMarkdownLinks(AiAnswersList);
        // Смена статуса факт-джобы вложения → перечитываем список (событие уже на UI-потоке).
        App.Current.FactJobChanged += OnFactJobChanged;
        // Волна 04: путь хранения — служебной строкой 12/18 под описанием
        // пустого состояния (реальный путь вместо %APPDATA%-маски).
        StoragePath.Text = _store.Directory;
        // §7: поиск по журналам; золотая подсветка совпадений задана в XAML
        // (SearchHighlightBrush + непрозрачный тёмный текст — читаемо в обеих
        // темах; правило видимости «чужие окна» не трогаем).
        // Один индекс и один E5-провайдер на приложение: история получает
        // смысловую ветку, не открывая второе соединение с meetings-index.db.
        // Собственный сервис — только если композиция приложения ещё не готова.
        if (App.Current.MeetingSearch is { } sharedSearch)
        {
            _search = sharedSearch;
            _ownsSearch = false;
        }
        else
        {
            _search = new MeetingSearchService(_store);
            _ownsSearch = true;
        }
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); _ = RefreshSearchAsync(); };
        LoadMeetings();
    }

    // ---- Адаптивная раскладка (волна 04) ----
    // Ширина колонки истории и перенос панелей кнопок — в XAML через общие
    // FtHistoryColumnWidthConverter / FtWidthThresholdConverter (FileTranscriptionWindow.xaml.cs).
    // Здесь остаётся только пересчёт MinHeight строк у сплиттера и всей сетки:
    // шапка в два ряда (≈106 высотой) сменяет одну строку (36) при минимуме текста 140.

    private void OnWorkAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = WorkArea.ActualWidth < 580;
        TranscriptRow.MinHeight = narrow ? 246 : 184;
        SummaryRow.MinHeight = narrow ? 248 : 186;
        DocGrid.MinHeight = narrow ? 506 : 382;
    }

    private async void LoadMeetings()
    {
        var generation = ++_meetingsGeneration;
        // P0/§3: перечисление каталога + парсинг sidecar'ов — на пуле, не на UI.
        List<MeetingInfo> meetings;
        try { meetings = await _store.LoadAllAsync().ConfigureAwait(true); }
        catch { meetings = new(); }
        // Бейджи со скрепкой: число вложений по встрече одним проходом. Это
        // синхронный SQLite-запрос в AttachmentStore — читаем на пуле, не на UI.
        try
        {
            var counts = await Task.Run(() => App.Current.Attachments?.CountByMeeting())
                .ConfigureAwait(true);
            if (counts != null)
                foreach (var m in meetings)
                    m.AttachmentCount = counts.TryGetValue(m.Id, out var c) ? c : 0;
        }
        catch { /* стор вложений мог не подняться — бейджи просто скрыты */ }

        // Более старый проход (стартовал раньше, доехал позже нового) не должен
        // затирать ни список, ни восстановленное выделение.
        if (_closed || generation != _meetingsGeneration) return;
        MeetingList.ItemsSource = meetings;
        // Замена ItemsSource сбрасывает выделение и скролл списка (P0/§3:
        // «скачки интерфейса» после удаления другой встречи). Восстанавливаем
        // выбранную встречу по id — OnMeetingSelect распознает уже
        // отрендеренный транскрипт и не будет перечитывать журнал.
        var currentId = _current?.Id;
        if (currentId == null) return;
        var same = meetings.FirstOrDefault(m => m.Id == currentId);
        if (same != null)
        {
            MeetingList.SelectedItem = same;
            MeetingList.ScrollIntoView(same);
        }
    }

    // ---- Выбор встречи ----
    // P0/§3 аудита 2026-10-15: транскрипт раньше читался и форматировался
    // СИНХРОННО на UI-потоке: ReadLines (IO) + FormatTranscript (StringBuilder
    // на весь журнал) + один гигантский SetText. 5-часовая встреча ≈ 1+ МБ текста:
    // layout TextBox'а с переносами вешал окно дольше 20 с → UiWatchdog убивал
    // процесс (09:54:23). Теперь: IO и форматирование на пуле, рендер кусками
    // по 60К с отдачей UI-потока, отмена на смену выбора/закрытие, индикатор.
    private async void OnMeetingSelect(object sender, SelectionChangedEventArgs e)
    {
        // _busy (генерация саммари) не блокирует выбор: §7-навигация программно
        // выставляет SelectedItem и должна открыть встречу. Транскрипт грузится
        // своим токеном (_transcriptCts), кнопки внизу страхуются флагом _busy.
        if (MeetingList.SelectedItem is not MeetingInfo info) return;
        // Повторный выбор той же встречи с уже отрендеренным транскриптом
        // (восстановление выделения после перезагрузки списка, клик по выбранной
        // строке) не перечитывает и не перерендеривает журнал — иначе сбросились
        // бы скролл TranscriptBox и текущее выделение поиска. В полёте тоже
        // считаем загруженной: отмена живого рендера той же встречи из-за
        // перезагрузки списка обнуляла бы прогресс чтения и якоря навигации.
        var alreadyLoaded = _current?.Id == info.Id
            && (!_transcriptRenderedTcs.Task.IsCompleted
                || (_transcriptRenderedTcs.Task is { IsCompletedSuccessfully: true } t
                    && t.Result));
        _current = info;
        // Снимок файлов — один на встречу: при повторном выборе той же
        // (восстановление выделения после LoadMeetings, §7-навигация, клик по
        // выбранной строке) состав не менялся, а перезапуск отменял бы живой
        // снимок и перечитывал SQLite+диск заново. Снимок считается текущим,
        // если строки уже этой встречи или в полёте снимок именно её; явно
        // запрошенная починка состава (_attachListRefreshNeeded) всегда
        // проходит.
        var attachShown = _attachSnapshotInFlight
            ? _attachSnapshotMeetingId
            : _attachRowsMeetingId;
        if (attachShown != info.Id || _attachListRefreshNeeded)
            RefreshAttachFiles();
        EmptyPanel.Visibility = Visibility.Collapsed;
        SetSummary(info.Summary ?? (alreadyLoaded ? _summaryRaw : ""));
        DeleteBtn.IsEnabled = !_busy;
        if (alreadyLoaded) return;
        await LoadTranscriptForAsync(info).ConfigureAwait(true);
        // Пока транскрипт грузился, мог быть выбран другой элемент — его
        // обработчик уже обновил изображения; повторный прогон не нужен.
        if (ReferenceEquals(_current, info)) RefreshMeetingImages();
    }

    /// <summary>Чтение + форматирование + инкрементальный вывод транскрипта встречи.
    /// Отменяется сменой выбранной встречи и закрытием окна.</summary>
    private async Task LoadTranscriptForAsync(MeetingInfo info)
    {
        try { _transcriptCts?.Cancel(); } catch { /* best-effort */ }
        _transcriptCts?.Dispose();
        var cts = _transcriptCts = new CancellationTokenSource();
        var ct = cts.Token;
        // Новый маркер завершения рендера: потребители (§7-поиск) берут его
        // после смены SelectedItem; завершаем только локальный tcs — старые
        // await-еры отменённой загрузки получат false именно от неё.
        var tcs = _transcriptRenderedTcs =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        ProgressPanel.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        StatusText.Text = UiText.Tr("Загружаю транскрипт...", "Loading transcript...");
        SummaryBtn.IsEnabled = false;
        ExtractBtn.IsEnabled = false;
        _transcriptFull = "";
        TranscriptBox.Text = "";
        // Якоря навигации принадлежат тексту прошлой встречи — сбрасываем на
        // старте каждой загрузки, чтобы сбой чтения не оставил чужой карты.
        _transcriptLines = Array.Empty<MeetingJournalLine>();
        _recordStarts = null;
        _midIndex = new Dictionary<string, int>();

        // Поиск по транскрипту: прежние совпадения относятся к старому тексту.
        // Индекс выбранного совпадения тоже сбрасываем на старте загрузки —
        // иначе deferred-выделение отменённого рендера доедет в конце нового
        // (UpdateTranscriptSearch с navigate ниже) как ложный переход.
        _transcriptHasLines = false;
        _transcriptSearchIndex = -1;
        try
        {
            // IO (чтение jsonl построчно) и сборка текста — на пуле потоков.
            var lines = await _store.ReadLinesAsync(info.JournalPath, ct).ConfigureAwait(true);
            // IO (jsonl построчно), сборка текста и таблица якорей для навигации §7
            // — на пуле потоков. Офсеты считает MeetingSearchService.LineCharOffsets
            // (null при расхождении формулы с текстом → скан-фолбэки ниже).
            var rendered = lines.Count > 0
                ? await Task.Run(() =>
                {
                    var text = MeetingTranscriptStore.FormatTranscript(lines);
                    var starts = MeetingSearchService.LineCharOffsets(lines, text);
                    var mids = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (var i = 0; i < lines.Count; i++)
                        if (lines[i].MessageId is { } mid) mids.TryAdd(mid, i);
                    return (Text: text, Starts: starts, Mids: (IReadOnlyDictionary<string, int>)mids);
                }, ct).ConfigureAwait(true)
                : (Text: L("L_Meet_EmptyTranscript"), Starts: (int[]?)null,
                    Mids: (IReadOnlyDictionary<string, int>?)null);
            ct.ThrowIfCancellationRequested();
            _transcriptFull = rendered.Text;
            _transcriptLines = lines;
            // Раздел «Ответы ИИ»: только ответы модели из журнала (роль «answer»).
            ApplyAiAnswers(lines);
            _recordStarts = rendered.Starts;
            _midIndex = rendered.Mids ?? new Dictionary<string, int>();
            _transcriptHasLines = lines.Count > 0;
            var full = rendered.Text;

            // Рендер кусками: SetText целиком на 1+ МБ — один сверхдорогой
            // layout; AppendText + yield на Background-приоритете оставляют
            // диспетчеру свободное время между вставками.
            for (var pos = 0; pos < full.Length; pos += TranscriptChunkChars)
            {
                ct.ThrowIfCancellationRequested();
                var len = Math.Min(TranscriptChunkChars, full.Length - pos);
                TranscriptBox.AppendText(full.Substring(pos, len));
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            // Счётчик/совпадения поиска по транскрипту — до TrySetResult, чтобы
            // отложенное выделение (SelectTranscriptMatch на незаконченном
            // рендере) увидело уже пересчитанный список. Пользователь мог
            // ввести запрос/нажать Enter ВО ВРЕМЯ рендера: тогда ждущее
            // выделение (_transcriptSearchIndex >= 0) глушится сменой версии —
            // переиздаём его на том же индексе после пересчёта (защита
            // Math.Min — на случай изменившегося числа совпадений).
            var pendingMatch = _transcriptSearchIndex;
            UpdateTranscriptSearch();
            if (_transcriptSearchMatches.Count > 0)
            {
                // pendingMatch >= 0 — во время рендера ждало конкретное совпадение
                // (Enter/кнопки): переиздаём его на том же индексе (Math.Min — на
                // случай изменившегося числа совпадений). pendingMatch < 0 —
                // запрос был введён в полёте без навигации: прыгаем к первому
                // совпадению, иначе счётчик остался бы «0/N» без выделения.
                _transcriptSearchIndex = pendingMatch >= 0
                    ? Math.Min(pendingMatch, _transcriptSearchMatches.Count - 1)
                    : 0;
                UpdateTranscriptSearchCount();
                SelectTranscriptMatch();
            }
            tcs.TrySetResult(true); // весь текст в боксе — §7-поиск может выделять строку

            var hasTranscript = _transcriptHasLines;
            SummaryBtn.IsEnabled = hasTranscript && !_busy;
            ExtractBtn.IsEnabled = hasTranscript && !_busy;
            Progress.IsIndeterminate = false;
            Progress.Value = Progress.Maximum;
            StatusText.Text = UiText.Tr("Транскрипт загружен", "Transcript loaded");
        }
        catch (OperationCanceledException) { tcs.TrySetResult(false); /* смена выбора/закрытие — новая загрузка уже идёт */ }
        catch (Exception ex)
        {
            tcs.TrySetResult(false);
            ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_transcriptCts, cts))
            {
                Progress.IsIndeterminate = false;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ---- Изображения встречи (feature-chat-images, ТЗ §11) ----
    // Данные — IChatImageStore через ChatImageFlow; тайлы — ChatImageTileVm
    // (Thumb декодирует превью ≤640 через ChatImageCodec). Секция скрыта при 0.

    /// <summary>Загрузить тайлы изображений выбранной встречи. Чтение превью с
    /// диска и декод PNG — на пуле (синхронный IO на UI вешал окно, P0/§3);
    /// поколение отсекает применение после смены встречи/закрытия.</summary>
    private async void RefreshMeetingImages()
    {
        MeetingImagesList.ItemsSource = null;
        ImagesSection.Visibility = Visibility.Collapsed;
        var flow = App.Current.ChatImages;
        var meetingId = _current?.Id;
        if (flow == null || meetingId == null || _closed) return;
        var generation = ++_imageGeneration;
        try
        {
            var tiles = await Task.Run(() => BuildImageTiles(flow, meetingId))
                .ConfigureAwait(true);
            // Устаревший снимок (смена встречи/закрытие) не должен ни применяться,
            // ни показывать чужие тайлы.
            if (_closed || generation != _imageGeneration || _current?.Id != meetingId)
                return;
            MeetingImagesList.ItemsSource = tiles;
            ImagesHeaderText.Text = $"{L("L_ImgSection")} ({tiles.Count})";
            ImagesSection.Visibility = tiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"MeetingImages refresh failed: {ex.Message}", "Images");
        }
    }

    /// <summary>Тайлы встречи: запрос в стор, проверка превью и декод —
    /// весь IO на пуле потоков. ChatImageCodec.ToBitmap возвращает замороженный
    /// BitmapSource, он безопасно передаётся на UI.</summary>
    private static List<ChatImageTileVm> BuildImageTiles(ChatImageFlow flow, string meetingId)
    {
        var tiles = new List<ChatImageTileVm>();
        foreach (var img in flow.GetMeetingImages(meetingId))
        {
            var tile = new ChatImageTileVm
            {
                ImageId = img.Ref.ImageId,
                FileName = img.Ref.FileName,
                State = img.State == ChatImageState.Ready ? ChatImageTileState.Ready : ChatImageTileState.Error,
                Width = img.Ref.Width,
                Height = img.Ref.Height,
            };
            // Превью ≤640 пишется кодеком при импорте; файла может не быть — тогда тайл без картинки.
            var preview = img.PreviewPath;
            if (img.State == ChatImageState.Ready && !string.IsNullOrEmpty(preview) && File.Exists(preview))
            {
                try { tile.Thumb = ChatImageCodec.ToBitmap(File.ReadAllBytes(preview)); }
                catch { /* битый файл превью — тайл останется с именем */ }
            }
            tiles.Add(tile);
        }
        return tiles;
    }

    /// <summary>Сворачивание/разворачивание секции изображений.</summary>
    private void OnImagesToggle(object sender, RoutedEventArgs e)
        => MeetingImagesList.Visibility = MeetingImagesList.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Клик по тайлу — просмотрщик над набором картинок встречи.</summary>
    private void OnMeetingImageClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        (System.Windows.Application.Current as App)?.Logger.Info("ChatImg", $"OnMeetingImageClick: tag={(sender as FrameworkElement)?.Tag?.GetType().Name}");
        if ((sender as FrameworkElement)?.Tag is not ChatImageTileVm tile) return;
        var tiles = (MeetingImagesList.ItemsSource as IEnumerable<ChatImageTileVm>)?.ToList();
        if (tiles == null || tiles.Count == 0) return;
        ImageViewerWindow.Show(this, tiles, tiles.IndexOf(tile), App.Current.ChatImages);
        e.Handled = true;
    }

    /// <summary>Меню «Открыть изображение» — тот же просмотрщик.</summary>
    private void OnImageMenuOpen(object sender, RoutedEventArgs e)
    {
        (System.Windows.Application.Current as App)?.Logger.Info("ChatImg", $"OnImageMenuOpen: tag={(sender as FrameworkElement)?.Tag?.GetType().Name}");
        if ((sender as FrameworkElement)?.Tag is not ChatImageTileVm tile) return;
        var tiles = (MeetingImagesList.ItemsSource as IEnumerable<ChatImageTileVm>)?.ToList();
        if (tiles == null || tiles.Count == 0) return;
        ImageViewerWindow.Show(this, tiles, tiles.IndexOf(tile), App.Current.ChatImages);
    }

    /// <summary>Меню «Сохранить как…» — оригинал байтами через flow.</summary>
    private async void OnImageMenuSaveAs(object sender, RoutedEventArgs e)
    {
        (System.Windows.Application.Current as App)?.Logger.Info("ChatImg", $"OnImageMenuSaveAs: tag={(sender as FrameworkElement)?.Tag?.GetType().Name}");
        if ((sender as FrameworkElement)?.Tag is not ChatImageTileVm tile) return;
        var flow = App.Current.ChatImages;
        var bytes = flow == null ? null : await flow.LoadOriginalBytesAsync(tile.ImageId).ConfigureAwait(true);
        if (bytes == null || bytes.Length == 0) return;
        // Повторный клик по пункту меню до конца первого сохранения не ставит
        // второй диалог поверх (чтение оригинала и запись идут через await).
        var key = "imgsave:" + tile.ImageId;
        if (!_attachOpsInFlight.Add(key)) return;
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L("L_ImgSaveTitle"),
                FileName = tile.FileName,
            };
            if (dlg.ShowDialog(this) != true) return;
            try { await File.WriteAllBytesAsync(dlg.FileName, bytes).ConfigureAwait(true); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        finally { _attachOpsInFlight.Remove(key); }
    }

    /// <summary>Меню «Копировать изображение» — PNG+DIB в буфер (переживает закрытие).</summary>
    private async void OnImageMenuCopy(object sender, RoutedEventArgs e)
    {
        (System.Windows.Application.Current as App)?.Logger.Info("ChatImg", $"OnImageMenuCopy: tag={(sender as FrameworkElement)?.Tag?.GetType().Name}");
        if ((sender as FrameworkElement)?.Tag is not ChatImageTileVm tile) return;
        var flow = App.Current.ChatImages;
        var bytes = flow == null ? null : await flow.LoadOriginalBytesAsync(tile.ImageId).ConfigureAwait(true);
        var bmp = bytes == null ? tile.Thumb : (ChatImageCodec.TryDecode(bytes)?.Bitmap ?? tile.Thumb);
        if (bmp == null) return;
        ChatImageCodec.PutImageToClipboard(ChatImageCodec.ToPngBytes(bmp), bmp);
        App.Current.PushOverlayToast(ToastKind.Info, L("L_ImgCopied"), "");
    }

    /// <summary>Меню «Добавить к сообщению» — картинка из истории в черновик оверлея.</summary>
    private void OnImageMenuAddToMsg(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChatImageTileVm tile) return;
        App.Current.ChatImages?.AddDraftFromStored(tile.ImageId);
    }

    // ---- Поиск по встречам (§7) ----
    // Сканы идут в MeetingSearchService поверх того же стора: async IO, отмена
    // дебаунсом/новым запросом, удалённые встречи выпадают (файлов нет на диске).

    private void OnMeetSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        if (string.IsNullOrWhiteSpace(MeetSearchBox.Text))
        {
            try { _searchCts?.Cancel(); } catch { }
            SearchResultsList.ItemsSource = null;
            SearchResultsList.Visibility = Visibility.Collapsed;
            MeetingList.Visibility = Visibility.Visible;
            NoResultsText.Visibility = Visibility.Collapsed;
            return;
        }
        // Старая выдача не должна переживать смену запроса: поиск асинхронный
        // (дебаунс + E5), и до прихода новых хитов под новым текстом висели бы
        // результаты предыдущего запроса. Пустой список честнее stale-строк.
        SearchResultsList.ItemsSource = null;
        SearchResultsList.Visibility = Visibility.Visible;
        MeetingList.Visibility = Visibility.Collapsed;
        NoResultsText.Visibility = Visibility.Collapsed;
        _searchDebounce.Start();
    }

    private async Task RefreshSearchAsync()
    {
        var q = MeetSearchBox.Text.Trim();
        if (q.Length == 0) return;
        try { _searchCts?.Cancel(); } catch { }
        _searchCts?.Dispose();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            var hits = await _search.SearchAsync(q, cts.Token).ConfigureAwait(true);
            // Ответ запроса, который уже сменил дебаунс/очистку строки (cts
            // отменён, но поиск успел завершиться успехом до отмены), не должен
            // затирать свежую выдачу — применяем только актуальный CTS.
            if (cts.IsCancellationRequested || !ReferenceEquals(_searchCts, cts)
                || _closed) return;
            SearchResultsList.ItemsSource = hits;
            SearchResultsList.Visibility = Visibility.Visible;
            MeetingList.Visibility = Visibility.Collapsed;
            NoResultsText.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { /* запрос сменился — новый уже летит */ }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>Клик по результату поиска: открыть встречу и подсветить строку.</summary>
    private async void OnSearchResultSelect(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not MeetingSearchHit hit) return;
        SearchResultsList.SelectedItem = null; // повторный клик по тому же результату тоже навигация
        await NavigateToHitAsync(hit).ConfigureAwait(true);
    }

    /// <summary>Публичная навигация к найденной строке (извне: ссылка «Источник»
    /// в оверлее; изнутри: список результатов). Выбирает встречу, ждёт полного
    /// рендера транскрипта (_transcriptRenderedTcs, §3) и выделяет строку золотом.
    /// LineIndex=-1 — совпадение в саммари: выделяем термин в markdown-компоненте.</summary>
    public async Task NavigateToHitAsync(MeetingSearchHit hit)
    {
        // Список встреч грузится async в LoadMeetings — дождёмся появления id.
        MeetingInfo? target = null;
        for (var i = 0; i < 80 && target == null && !_closed; i++)
        {
            target = (MeetingList.ItemsSource as IEnumerable<MeetingInfo>)
                ?.FirstOrDefault(m => m.Id == hit.MeetingId);
            if (target == null) await Task.Delay(100).ConfigureAwait(true);
        }
        if (target == null || _closed) return; // встреча удалена/журнал недоступен

        // Та же встреча уже выбрана и её транскрипт в боксе (или догружается —
        // задача ещё не завершена) — повторный выбор не нужен: выделим сразу
        // после ожидания рендера. Перезапуск по пустому Text делаем только при
        // ЗАВЕРШИВШЕЙСЯ загрузке — иначе клик во время инкрементального рендера
        // отменял живую загрузку и стартовал новую (§3: лишняя перечитка журнала).
        var needSelect = !ReferenceEquals(MeetingList.SelectedItem, target)
            || _current?.Id != hit.MeetingId
            || (TranscriptBox.Text.Length == 0 && _transcriptRenderedTcs.Task.IsCompleted);
        if (needSelect)
        {
            // Если уже выбрана — повторный выбор не сработает: сбрасываем, чтобы
            // OnMeetingSelect перезапустил загрузку.
            if (ReferenceEquals(MeetingList.SelectedItem, target))
                MeetingList.SelectedItem = null;
            MeetingList.SelectedItem = target;
        }
        MeetingList.ScrollIntoView(target);

        // Проверка выбора: если смена SelectedItem по какой-то причине не прошла
        // (_current остался прежним) — подсвечивать чужую встречу нельзя.
        if (_current?.Id != hit.MeetingId) return;

        // Переход к хиту — более позднее намерение, чем любой уже ждущий
        // конца рендера авто-переход поиска по транскрипту (SelectTranscriptMatch):
        // версия глушит их при пробуждении (как в OnClosed), иначе они затирали
        // бы подсветку строки/саммари хита первым совпадением запроса.
        _transcriptSearchVersion++;
        if (hit.LineIndex < 0)
        {
            // Хит саммари: сначала термин в markdown-резюме; если там его нет (или
            // саммари ещё не построено), но хит несёт текст строки журнала —
            // пытаемся подсветить строку транскрипта, иначе просто прокручиваем
            // к секции саммари.
            if (HighlightSummaryMatch(hit)) return;
            if (!string.IsNullOrEmpty(hit.LineText) || !string.IsNullOrEmpty(hit.Snippet))
            {
                var rendered = await _transcriptRenderedTcs.Task.ConfigureAwait(true);
                if (rendered && _current?.Id == hit.MeetingId && !_closed
                    && HighlightTranscriptLine(hit))
                    return;
            }
            DocScroll.ScrollToBottom(); // саммари — нижняя секция документа
            return;
        }

        var transcriptRendered = await _transcriptRenderedTcs.Task.ConfigureAwait(true);
        if (!transcriptRendered || _current?.Id != hit.MeetingId || _closed) return;
        HighlightTranscriptLine(hit);
    }

    /// <summary>Выделить строку журнала хита в TranscriptBox. Адрес строки —
    /// стабильный: сперва RecordId (mid) → LineIndex через _midIndex, затем
    /// LineIndex → офсет через _recordStarts (точные смещения FormatTranscript,
    /// устойчивы к '\n' внутри реплик и к повторяющимся меткам времени). Сanity-
    /// чек на Ts уводит в скан-фолбэки (префикс «[Ts]», позиция текста строки).
    /// true — строка найдена и выделена.</summary>
    private bool HighlightTranscriptLine(MeetingSearchHit hit)
    {
        var text = TranscriptBox.Text;
        if (text.Length == 0) return false;
        var start = FindRecordStart(text, hit, out var idx);
        if (start < 0) return false;
        // Выделяем реплику целиком (включая продолжения строк и метки картинок):
        // конец записи — следующий офсет из карты, либо следующая строка с
        // меткой «[чч:» (многострочные реплики не обрезаются), либо конец текста.
        int end;
        if (idx is { } i && _recordStarts != null && i + 1 < _recordStarts.Length)
            end = _recordStarts[i + 1];
        else if ((end = NextRecordStart(text, start)) < 0)
            end = text.Length;
        if (end <= start) end = text.Length;
        TranscriptBox.Focus();
        TranscriptBox.Select(start, end - start);
        var rect = TranscriptBox.GetRectFromCharacterIndex(start);
        if (rect != Rect.Empty) TranscriptBox.ScrollToVerticalOffset(rect.Top - 8);
        TranscriptBox.BringIntoView(); // прокрутка документа к секции транскрипта
        return true;
    }

    /// <summary>LineIndex хита: предпочтительно по RecordId (mid), иначе — сам
    /// LineIndex, если он в диапазоне загруженных строк.</summary>
    private int? HitLineIndex(MeetingSearchHit hit)
    {
        if (hit.RecordId != null && _midIndex.TryGetValue(hit.RecordId, out var i))
            return i;
        if (hit.LineIndex >= 0 && hit.LineIndex < _transcriptLines.Count)
            return hit.LineIndex;
        return null;
    }

    /// <summary>Старт записи хита в отрендеренном тексте. Точный путь —
    /// _recordStarts[LineIndex]; при расхождении карты (null) или провале
    /// sanity-чека Ts — сканы: все вхождения «[Ts]» с фильтром по фрагменту
    /// текста, затем начало записи, содержащей LineText/Snippet.</summary>
    private int FindRecordStart(string text, MeetingSearchHit hit, out int? lineIndex)
    {
        lineIndex = HitLineIndex(hit);
        if (lineIndex is { } i && _recordStarts != null && i < _recordStarts.Length)
        {
            var start = _recordStarts[i];
            // Запись должна начинаться с «[Ts]», если метка известна.
            if (start >= 0 && (string.IsNullOrEmpty(hit.Ts) || LineStartsWithTs(text, start, hit.Ts)))
                return start;
        }
        if (!string.IsNullOrEmpty(hit.Ts))
        {
            var alt = FindLineByTs(text, hit.Ts, hit.LineText);
            if (alt >= 0) { lineIndex = null; return alt; }
        }
        lineIndex = null;
        return FindRecordByText(text,
            !string.IsNullOrEmpty(hit.LineText) ? hit.LineText : hit.Snippet);
    }

    /// <summary>Скан-фолбэк: вхождения «[Ts]» на границе строки, предпочтительно
    /// в записи, содержащей начало текста строки (повторные метки не сбивают).</summary>
    private static int FindLineByTs(string text, string ts, string lineText)
    {
        var prefix = "[" + ts + "]";
        var pos = 0;
        var snippet = lineText.Length > 24 ? lineText[..24] : lineText;
        while ((pos = text.IndexOf(prefix, pos, StringComparison.Ordinal)) >= 0)
        {
            if (pos == 0 || text[pos - 1] == '\n')
            {
                var lineEnd = text.IndexOf('\n', pos);
                var line = lineEnd < 0 ? text[pos..] : text[pos..lineEnd];
                if (snippet.Length == 0 || line.Contains(snippet, StringComparison.Ordinal))
                    return pos;
            }
            pos += prefix.Length;
        }
        return -1;
    }

    /// <summary>Текст в позиции start начинается с «[ts]».</summary>
    private static bool LineStartsWithTs(string text, int start, string ts)
        => start >= 0 && start + ts.Length + 2 <= text.Length
            && text.AsSpan(start).StartsWith(("[" + ts + "]").AsSpan(), StringComparison.Ordinal);

    /// <summary>Фолбэк без индекса и метки: ищем фрагмент текста реплики и
    /// поднимаемся к началу записи (ближайшая строка «[чч:» перед вхождением).</summary>
    private static int FindRecordByText(string text, string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return -1;
        var probe = fragment.Trim('…', ' ');
        if (probe.Length == 0) return -1;
        if (probe.Length > 64) probe = probe[..64];
        var pos = text.IndexOf(probe, StringComparison.Ordinal);
        if (pos < 0) return -1;
        return RecordStartBefore(text, pos);
    }

    /// <summary>Начало записи транскрипта, содержащей позицию pos: идём вверх до
    /// строки, начинающейся с «[»+цифра+цифра+«:» (метка времени реплики).</summary>
    private static int RecordStartBefore(string text, int pos)
    {
        // Сам pos может быть началом записи; иначе поднимаемся по '\n': старт
        // записи — позиция после '\n', начинающаяся с «[чч:».
        if (LooksLikeRecordStart(text, pos)) return pos;
        var p = pos > 0 ? text.LastIndexOf('\n', pos - 1) : -1;
        while (p >= 0)
        {
            if (LooksLikeRecordStart(text, p + 1)) return p + 1;
            p = p > 0 ? text.LastIndexOf('\n', p - 1) : -1;
        }
        return LooksLikeRecordStart(text, 0) ? 0 : -1;
    }

    /// <summary>Позиция начинает строку-запись журнала: начало текста или '\n'
    /// перед ней, затем «[» и метка «чч:».</summary>
    private static bool LooksLikeRecordStart(string text, int p)
    {
        if (p < 0 || p + 3 >= text.Length) return false;
        if (p > 0 && text[p - 1] != '\n') return false;
        return text[p] == '[' && char.IsDigit(text[p + 1])
            && char.IsDigit(text[p + 2]) && text[p + 3] == ':';
    }

    /// <summary>Начало следующей записи после позиции start: первая строка
    /// с «[чч:» правее текущей. -1 — записей дальше нет (start — последняя).</summary>
    private static int NextRecordStart(string text, int start)
    {
        var p = start;
        while ((p = text.IndexOf('\n', p)) >= 0)
        {
            var cand = p + 1;
            if (LooksLikeRecordStart(text, cand)) return cand;
            p = cand;
        }
        return -1;
    }

    /// <summary>Совпадение в AI-саммари: выделяем термин в markdown-компоненте.
    /// Ищем по ВИДИМОМУ тексту (PlainText) — сырой текст с разметкой не совпадает
    /// с тем, что видит пользователь. true — термин найден и выделен; false —
    /// саммари пусто или термина в нём нет (вызывающий решает: скролл к секции
    /// или поиск строки в транскрипте).</summary>
    private bool HighlightSummaryMatch(MeetingSearchHit hit)
    {
        var text = SummaryView.PlainText ?? "";
        if (text.Length == 0) return false;
        // Термин из сниппета: ищем самое длинное слово-фрагмент сниппета в тексте.
        var probe = StripMarkup(hit.Snippet.Trim('…', ' '));
        var start = probe.Length >= 4
            ? text.IndexOf(probe, StringComparison.OrdinalIgnoreCase)
            : -1;
        if (start < 0)
        {
            // Шире: любое слово сниппета длиной ≥4.
            foreach (var w in hit.Snippet.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w2 = StripMarkup(w.Trim('…', '.', ',', ';', ':', '!'));
                if (w2.Length < 4) continue;
                start = text.IndexOf(w2, StringComparison.OrdinalIgnoreCase);
                if (start >= 0) { probe = w2; break; }
            }
        }
        if (start < 0) return false;
        SummaryView.SelectPlainRange(start, probe.Length);
        _summaryHighlightActive = true;
        SummaryView.BringIntoView();
        return true;
    }

    /// <summary>Убрать markdown-маркеры из фрагмента: в видимом тексте их нет,
    /// поэтому поиск по «сырому» сниппету не нашёл бы совпадение. Длина
    /// очищенного фрагмента = длина выделения в видимом тексте.</summary>
    private static string StripMarkup(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c is not ('*' or '_' or '#' or '`' or '~' or '|' or '>')) sb.Append(c);
        return sb.ToString().Trim();
    }

    // ---- Markdown: саммари, ответы ИИ, ссылки ----

    /// <summary>Показать AI-саммари. Исходный текст с разметкой хранится как
    /// есть (для экспорта .txt), форматирование применяется при отрисовке.</summary>
    private void SetSummary(string? raw)
    {
        _summaryRaw = raw ?? "";
        _summaryHighlightActive = false;
        SummaryView.ClearSelection();
        SummaryView.Text = _summaryRaw;
    }

    /// <summary>Ответы ИИ из журнала встречи: роль «answer» — то, что модель
    /// ответила в чате. Реплики (user/question) остаются в транскрипте
    /// обычным текстом — их разметка не разбирается.</summary>
    private void ApplyAiAnswers(IReadOnlyList<MeetingJournalLine> lines)
    {
        AiAnswers.Clear();
        foreach (var l in lines)
        {
            if (!string.Equals(l.Role, "answer", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(l.Text)) continue;
            AiAnswers.Add(new AiAnswerVm(l.Ts, l.Text));
        }
        UpdateAiAnswersHeader();
    }

    /// <summary>Очистить раздел «Ответы ИИ» (встреча удалена или не выбрана).</summary>
    private void ClearAiAnswers()
    {
        AiAnswers.Clear();
        UpdateAiAnswersHeader();
    }

    /// <summary>Заголовок раздела «Ответы ИИ (N)»; сам раздел скрыт, пока
    /// у встречи нет ответов модели.</summary>
    private void UpdateAiAnswersHeader()
    {
        AiAnswersHeaderText.Text = string.Format(
            UiText.Tr("Ответы ИИ ({0})", "AI answers ({0})"), AiAnswers.Count);
        AiAnswersSection.Visibility = AiAnswers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Развернуть/свернуть список сохранённых ответов ИИ.</summary>
    private void OnAiAnswersToggle(object sender, RoutedEventArgs e)
    {
        AiAnswersList.Visibility = AiAnswersList.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>Подписать markdown-компоненты в визуальном дереве на клик по
    /// ссылке. Карточки ответов ИИ создаются шаблоном ItemsControl — контейнеры
    /// появляются после лейаута, поэтому подписка идёт по мере появления;
    /// повторные подписки отсекаются (сравнение по ссылке).</summary>
    private void HookMarkdownLinks(DependencyObject root)
    {
        foreach (var view in EnumerateMarkdownViews(root))
        {
            if (_linkedViews.Any(v => ReferenceEquals(v, view))) continue;
            _linkedViews.Add(view);
            view.LinkClicked += (s, e) => MarkdownLinkOpener.Open(e.Url);
        }
    }

    private static IEnumerable<MarkdownView> EnumerateMarkdownViews(DependencyObject root)
    {
        if (root is MarkdownView mv) yield return mv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            foreach (var v in EnumerateMarkdownViews(
                System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                yield return v;
    }


    /// <summary>После удаления встречи перезапускает активный поиск (§7/U12 —
    /// удалённая встреча выпадает из результатов сразу, без очистки запроса).</summary>
    private void RefreshSearchIfActive()
    {
        if (!string.IsNullOrWhiteSpace(MeetSearchBox.Text))
        {
            _searchDebounce.Stop();
            _ = RefreshSearchAsync();
        }
    }

    // ---- Поиск по тексту транскрипта (Ctrl+F / панель над TranscriptBox) ----
    // Совпадения — символьные офсеты в _transcriptFull; применять выделение можно
    // только после полного инкрементального рендера (TranscriptBox к этому
    // моменту содержит тот же текст). Скан по уже загруженной строке — O(n) на
    // UI-потоке, ~1 МБ ищется за миллисекунды, отдельный фон-поток не нужен.

    /// <summary>Ctrl+F фокусирует строку поиска по транскрипту из любого места окна.</summary>
    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt))
                == ModifierKeys.Control)
        {
            TranscriptSearchBox.Focus();
            TranscriptSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Запрос изменился — пересчёт совпадений и прыжок к первому.</summary>
    private void OnTranscriptSearchChanged(object sender, TextChangedEventArgs e)
        => UpdateTranscriptSearch(navigate: true);

    /// <summary>Enter — следующее совпадение, Shift+Enter — предыдущее, Esc — очистка.</summary>
    private void OnTranscriptSearchPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MoveTranscriptMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (TranscriptSearchBox.Text.Length > 0) TranscriptSearchBox.Text = "";
            else TranscriptBox.Focus();
            e.Handled = true;
        }
    }

    private void OnTranscriptSearchNext(object sender, RoutedEventArgs e) => MoveTranscriptMatch(+1);
    private void OnTranscriptSearchPrevious(object sender, RoutedEventArgs e) => MoveTranscriptMatch(-1);

    /// <summary>Пересчитать офсеты совпадений по _transcriptFull (регистр
    /// неважен, пустой запрос и «нет строк транскрипта» — не совпадения) и
    /// обновить счётчик «k/n». navigate — выделить первое совпадение.</summary>
    private void UpdateTranscriptSearch(bool navigate = false)
    {
        _transcriptSearchVersion++;
        _transcriptSearchMatches.Clear();
        _transcriptSearchIndex = -1;
        _transcriptSearchQuery = TranscriptSearchBox.Text.Trim();
        if (_transcriptSearchQuery.Length > 0 && _transcriptHasLines
            && !string.IsNullOrEmpty(_transcriptFull))
        {
            var pos = 0;
            while ((pos = _transcriptFull.IndexOf(
                        _transcriptSearchQuery, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                _transcriptSearchMatches.Add(pos);
                pos += _transcriptSearchQuery.Length;
            }
        }
        UpdateTranscriptSearchCount();
        if (navigate && _transcriptSearchMatches.Count > 0)
        {
            _transcriptSearchIndex = 0;
            UpdateTranscriptSearchCount();
            SelectTranscriptMatch();
        }
    }

    /// <summary>Переход на соседнее совпадение с зацикливанием. Если пользователь
    /// перед этим кликнул в текст, стартуем от ближайшего к каретке совпадения,
    /// а не от последнего выделенного поиском.</summary>
    private void MoveTranscriptMatch(int direction)
    {
        var n = _transcriptSearchMatches.Count;
        if (n == 0) return;
        int i;
        if (_transcriptSearchIndex >= 0
            && TranscriptBox.SelectionStart == _transcriptSearchMatches[_transcriptSearchIndex])
        {
            i = _transcriptSearchIndex + direction;
        }
        else
        {
            var c = _transcriptSearchMatches.BinarySearch(TranscriptBox.SelectionStart);
            i = c >= 0 ? c + direction
                : direction > 0 ? ~c
                : ~c - 1;
        }
        _transcriptSearchIndex = ((i % n) + n) % n;
        UpdateTranscriptSearchCount();
        SelectTranscriptMatch();
    }

    /// <summary>Выделить текущее совпадение и прокрутить до него. Транскрипт ещё
    /// рендерится кусками — откладываем до _transcriptRenderedTcs (версия
    /// отсекает устаревшие ожидания: смена запроса/встречи/отмена рендера).</summary>
    private async void SelectTranscriptMatch()
    {
        var i = _transcriptSearchIndex;
        if (i < 0 || i >= _transcriptSearchMatches.Count) return;
        var version = _transcriptSearchVersion;
        var renderTask = _transcriptRenderedTcs.Task;
        var deferred = false;
        try
        {
            if (!renderTask.IsCompleted
                || (renderTask.IsCompletedSuccessfully && !renderTask.Result))
            {
                deferred = true;
                var rendered = await renderTask.ConfigureAwait(true);
                if (!rendered) return;
            }
        }
        catch { return; }
        // Отложенное выделение — авто-переход, а не прямая команда: если за
        // время ожидания рендера в документе появилось другое выделение
        // (§7-навигация к хиту подсветила строку/термин в саммари, пользователь
        // сам выделил текст), прыгать на своё совпадение уже нельзя.
        if (deferred
            && (TranscriptBox.SelectionLength > 0 || _summaryHighlightActive))
            return;
        if (version != _transcriptSearchVersion
            || i != _transcriptSearchIndex || _closed) return;
        var start = _transcriptSearchMatches[i];
        var len = _transcriptSearchQuery.Length;
        var text = TranscriptBox.Text;
        if (start < 0 || start + len > text.Length) return;
        TranscriptBox.Select(start, len);
        var rect = TranscriptBox.GetRectFromCharacterIndex(start);
        if (rect != Rect.Empty) TranscriptBox.ScrollToVerticalOffset(Math.Max(0, rect.Top - 8));
        TranscriptBox.BringIntoView(); // прокрутка документа к секции транскрипта
    }

    /// <summary>Счётчик «k/n» у строки поиска: «—» при пустом запросе,
    /// «0» при отсутствии совпадений, «1/7» при навигации.</summary>
    private void UpdateTranscriptSearchCount()
    {
        TranscriptSearchCount.Text =
            _transcriptSearchQuery.Length == 0 ? L("L_Meet_TranscriptSearchCountEmpty")
            : _transcriptSearchMatches.Count == 0 ? "0"
            : $"{_transcriptSearchIndex + 1}/{_transcriptSearchMatches.Count}";
    }


    // ---- Удаление ----
    private async void OnDeleteMeeting(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // Кнопка действует на выбранную встречу; из контекстного меню — на ту,
        // по которой кликнули правой (WPF выделяет её при открытии меню).
        if (MeetingList.SelectedItem is not MeetingInfo info) return;
        // Двойной клик/повторный пункт меню не должен запускать второй проход
        // удаления (File.Delete идемпотентен, но лишний проход — лишняя
        // перезагрузка списка и сброс скролла).
        if (!_attachOpsInFlight.Add("delmeeting")) return;
        try
        {
            // File.Delete журнала+sidecar — на пуле: удаление не держит UI-поток
            // (Delete под _lock ещё и сбрасывает «текущую» встречу стора).
            await Task.Run(() => _store.Delete(info.Id)).ConfigureAwait(true);
            if (_current?.Id == info.Id)
            {
                // Отменяем идущую загрузку транскрипта удалённой встречи —
                // иначе она дорисуется после очистки панели.
                try { _transcriptCts?.Cancel(); } catch { /* best-effort */ }
                _current = null;
                _transcriptFull = "";
                _transcriptHasLines = false;
                _transcriptLines = Array.Empty<MeetingJournalLine>();
                _recordStarts = null;
                _midIndex = new Dictionary<string, int>();
                RefreshAttachFiles();
                UpdateTranscriptSearch();
                TranscriptBox.Text = "";
                SetSummary("");
                ClearAiAnswers();
                SummaryBtn.IsEnabled = false;
                ExtractBtn.IsEnabled = false;
                DeleteBtn.IsEnabled = false;
                EmptyPanel.Visibility = Visibility.Visible;
            }
            LoadMeetings();
            RefreshSearchIfActive();
        }
        finally { _attachOpsInFlight.Remove("delmeeting"); }
    }

    // ---- Копирование / скачивание ----
    // Берём _transcriptFull, а не TranscriptBox.Text: бокс заполняется кусками
    // и на момент клика может содержать лишь часть транскрипта (P0/§3).
    private void OnCopyTranscript(object sender, RoutedEventArgs e) => CopyText(_transcriptFull, L("L_Meet_CopiedTranscript"));
    // Копируем ВИДИМЫЙ текст саммари (без markdown-маркеров).
    private void OnCopySummary(object sender, RoutedEventArgs e) => CopyText(SummaryView.PlainText, L("L_Meet_CopiedSummary"));

    private void CopyText(string text, string ok)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); App.Current.Notifier?.ShowInfo(L("L_Meet_Title"), ok); }
        catch { /* буфер иногда занят другим процессом */ }
    }

    private void OnDownloadTranscript(object sender, RoutedEventArgs e) => SaveText(_transcriptFull, "transcript");
    // Экспорт сохраняет ИСХОДНЫЙ текст саммари (с разметкой) — его можно
    // переиспользовать в markdown-документе.
    private void OnDownloadSummary(object sender, RoutedEventArgs e) => SaveText(_summaryRaw, "summary");

    private async void SaveText(string text, string kind)
    {
        if (string.IsNullOrEmpty(text)) { ShowError(L("L_Meet_NothingToSave")); return; }
        var baseName = _current?.Id ?? "meeting";
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = L("L_Meet_FilterTxt"), FileName = $"{baseName}_{kind}.txt" };
        if (dlg.ShowDialog() == true)
        {
            // Запись транскрипта/саммари до ~1+ МБ — на пуле, не на UI.
            try
            {
                await File.WriteAllTextAsync(dlg.FileName, text).ConfigureAwait(true);
                App.Current.Notifier?.ShowInfo(L("L_Meet_Title"), L("L_Meet_FileSaved"));
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }

    // ---- Извлечение фактов транскрипта в долговременную память ----
    private void OnExtractToMemory(object sender, RoutedEventArgs e)
    {
        if (_busy || _current == null) return;
        var transcript = _transcriptFull;
        if (string.IsNullOrWhiteSpace(transcript))
        {
            App.Current.Notifier?.ShowInfo(L("L_FileTr_ExtractToastTitle"), L("L_FileTr_TranscribeFirstShort"));
            return;
        }
        new TranscriptExtractDialog(transcript) { Owner = this }.ShowDialog();
    }

    // ---- AI-саммари (тот же протокол встречи, что у файловой транскрибации) ----
    private async void OnGenerateSummary(object sender, RoutedEventArgs e)
    {
        if (_busy || _current == null) return;
        // Источник резюме фиксируем на старте: за 8 минут генерации пользователь
        // может переключить встречу — записывать готовый текст в чужую карточку
        // и чужой бокс нельзя (§5: результат остаётся привязанным к исходной).
        var meeting = _current;
        string transcript = _transcriptFull;
        if (string.IsNullOrWhiteSpace(transcript)) { ShowError(L("L_Meet_NoTranscript")); return; }

        SetBusy(true);
        Progress.IsIndeterminate = true;
        Progress.Value = 0;
        StatusText.Text = L("L_Meet_StGenerating");
        SummaryStopBtn.IsEnabled = true;
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = _cts.Token;
        // Причина провала от сервиса (§5): «LLM не ответил» без причины не даёт
        // понять, исчерпан бюджет, модель промолчала или упала сеть.
        string? failure = null;
        try
        {
            string summary = await MeetingSummarizer.SummarizeAsync(
                App.Current.ApiClient, transcript,
                s => Dispatcher.Invoke(() => { if (!_closed) StatusText.Text = s; }), ct,
                App.Current.Config.Advanced.SummaryPrompt,
                // Прогресс по частям: короткая встреча — бегунок (один запрос),
                // длинная — determinate по MAP-частям + REDUCE.
                onPart: (done, total) => Dispatcher.Invoke(() =>
                {
                    if (_closed) return;
                    if (total <= 1) { Progress.IsIndeterminate = true; return; }
                    Progress.IsIndeterminate = false;
                    Progress.Maximum = total;
                    Progress.Value = Math.Min(done, total);
                }),
                // Расход всех запросов и повторов — в тот же TokenLedger, что у чата,
                // иначе счётчик врёт («ответы есть, токены 0»).
                onUsage: r => App.Current.AccountBackgroundUsage(r),
                onFailure: reason => failure = reason,
                checkpointJson: meeting.SummaryCheckpointJson,
                onCheckpoint: checkpoint => Task.Run(() =>
                {
                    meeting.SummaryCheckpointJson = checkpoint;
                    _store.SaveInfo(meeting);
                }));
            Progress.IsIndeterminate = false;
            if (string.IsNullOrWhiteSpace(summary))
            {
                var reason = string.IsNullOrWhiteSpace(failure)
                    ? L("L_Meet_ErrLlmUnavailable")
                    : string.Format(L("L_Meet_ErrSummaryFmt"), failure);
                // Старое сохранённое резюме не трогаем: пустой результат — ошибка,
                // а не «протокол на пустой встрече».
                ShowError(reason);
                App.Current.Notifier?.ShowError(L("L_Meet_SummaryBtn"), reason);
            }
            else
            {
                Progress.Value = Progress.Maximum;
                meeting.Summary = summary;
                meeting.SummaryCheckpointJson = null;
                // Сначала sidecar, затем бокс, и только после этого — статус
                // «готово»: иначе UI успевает показать завершение с ещё старым
                // текстом (гонка с медленной записью на диск), а при сбое записи
                // в боксе остался бы несохранённый протокол.
                // SaveInfo — синхронная запись sidecar; на пул.
                await Task.Run(() => _store.SaveInfo(meeting)).ConfigureAwait(true);
                // В бокс — только если исходная встреча всё ещё выбрана
                // (сравнение по Id: LoadMeetings подменяет объект при перезагрузке).
                if (_current?.Id == meeting.Id && !_closed)
                {
                    _current.Summary = summary;
                    _current.SummaryCheckpointJson = null;
                    SetSummary(summary);
                }
                StatusText.Text = L("L_Meet_StSummaryDone");
            }
        }
        catch (OperationCanceledException) { StatusText.Text = L("L_Meet_StSummaryCancelled"); }
        catch (Exception ex)
        {
            StatusText.Text = L("L_Meet_StSummaryError");
            App.Current.Notifier?.ShowError(L("L_Meet_SummaryBtn"),
                string.Format(L("L_Meet_ErrSummaryFmt"), ex.Message));
        }
        finally
        {
            if (_current?.Id == meeting.Id)
                _current.SummaryCheckpointJson = meeting.SummaryCheckpointJson;
            Progress.IsIndeterminate = false;
            SummaryStopBtn.IsEnabled = false;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Остановка генерации резюме (§5): отменяет текущий запрос и продолжения.
    /// Уже сохранённое резюме остаётся — отмена не подменяет его частичным текстом.
    /// </summary>
    private void OnStopSummary(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* уже отменено/освобождено */ }
        SummaryStopBtn.IsEnabled = false;
        if (!_closed) StatusText.Text = L("L_Meet_StSummaryStopping");
    }

    // ---- Общее ----
    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            // Сбрасываем цвет статуса (после прошлой ошибки мог остаться красным).
            if (TryFindResource("TextMutedBrush") is System.Windows.Media.Brush mb)
                StatusText.Foreground = mb;
        }
        MeetingList.IsEnabled = !busy;
        SummaryBtn.IsEnabled = !busy && _current != null && !string.IsNullOrWhiteSpace(_transcriptFull);
        ExtractBtn.IsEnabled = !busy && _current != null && !string.IsNullOrWhiteSpace(_transcriptFull);
        DeleteBtn.IsEnabled = !busy && _current != null;
    }

    private void ShowError(string msg)
    {
        App.Current.Logger?.Warn($"MeetingHistory: {msg}", "UI");
        // Ошибку показываем прямо в окне (в теме приложения), без системного win-диалога.
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = "⚠ " + msg;
        if (TryFindResource("ErrorBrush") is System.Windows.Media.Brush b)
            StatusText.Foreground = b;
    }

    // ---- Прикреплённые файлы встречи (секция «Файлы (N)» над транскриптом) ----
    // Владелец данных — AttachmentStore (App.Attachments): локальные копии переживают
    // перезапуск и доступны из истории. Секция скрыта, если у встречи нет вложений.

    /// <summary>Отменить летящий снимок секции файлов: новый запрос — новый
    /// CancellationTokenSource + поколение. Отменённые LoadSegmentsAsync
    /// завершаются OperationCanceledException, который RefreshAttachFiles глотает;
    /// даже если они завершатся успехом — применение отсечёт проверка
    /// поколения и id встречи (устаревшие строки не перетирают текущие).</summary>
    private void CancelAttachLoad()
    {
        _attachGeneration++;
        var old = _attachCts;
        _attachCts = new CancellationTokenSource();
        try { old?.Cancel(); } catch { /* best-effort */ }
        old?.Dispose();
    }

    /// <summary>Неизменяемый снимок одной строки файловой секции: карточка
    /// документа + всё, что нужно подписи. Собирается целиком на пуле потоков —
    /// ни одного синхронного чтения стора/диска на UI-потоке.</summary>
    private sealed record AttachRowSnapshot(
        StoredDocument Doc, int SegCount, bool Exists, FactJob? Job);

    /// <summary>Перечитать список файлов выбранной встречи (шапка «Файлы (N)» + строки).
    /// Снимок (список документов, File.Exists, статус факт-джобы, число сегментов)
    /// собирается одним отменяемым проходом на пуле и применяется атомарно;
    /// коллекция AttachFiles — та же самая (binding ItemsSource), строки
    /// существующих документов обновляются на месте — свёрнутость секции и
    /// позиция списка не сбрасываются. Дубли DocumentId сливаются в одну строку.</summary>
    private async void RefreshAttachFiles()
    {
        if (_closed) return;
        _attachListRefreshNeeded = false; // запрошенный снимок — этот
        CancelAttachLoad();
        var generation = _attachGeneration; // поколение зафиксировано ДО первого await
        var ct = _attachCts!.Token;
        var store = App.Current.Attachments;
        var meetingId = _current?.Id;
        if (store == null || meetingId == null)
        {
            AttachFiles.Clear();
            _attachRowsMeetingId = null;
            _attachSnapshotMeetingId = null;
            _attachSnapshotInFlight = false; // «пустой снимок» применён — полёта нет
            // Доигровки точечных обновлений относились к старой встрече — её
            // строк больше нет, перечитывать их для удалённой/отменённой
            // встречи бессмысленно (иначе при следующем выборе встречи
            // доигровка прочла бы строки и выставила лишний полный снимок).
            _rowRefreshPending.Clear();
            FilesSection.Visibility = Visibility.Collapsed;
            return;
        }
        _attachSnapshotInFlight = true;
        _attachSnapshotMeetingId = meetingId;
        // Точечные обновления, уже летящие на момент старта снимка, могут
        // примениться раньше ApplyAttachRows и быть затёрты его данными —
        // их документы тоже ставим на доигровку после применения снимка.
        foreach (var id in _rowRefreshInFlight) _rowRefreshPending.Add(id);
        try
        {
            // Кэш строк текущего списка — для повторного использования числа
            // сегментов, пока карточка документа не менялась (Чтение segments.json
            // на каждый снимок было бы лишним IO).
            var existing = new Dictionary<string, AttachFileVm>(StringComparer.Ordinal);
            foreach (var vm in AttachFiles) existing[vm.Doc.DocumentId] = vm;
            // Весь IO — на пуле: ListMeetingDocuments/LatestJobFor (SQLite под
            // _gate), File.Exists, segments.json. Одна строка на DocumentId:
            // PK (document_id, meeting_id) дублей не даёт, но если запись всё же
            // задвоена — показываем одну строку, базу не трогаем (§3 аудита:
            // визуальный дубль — не повод удалять данные).
            var snapshot = await Task.Run(async () =>
            {
                var docs = store.ListMeetingDocuments(meetingId);
                var rows = new List<AttachRowSnapshot>(docs.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ma in docs)
                {
                    ct.ThrowIfCancellationRequested();
                    var doc = ma.Doc;
                    if (!seen.Add(doc.DocumentId)) continue;
                    var exists = File.Exists(store.GetSourcePath(doc));
                    FactJob? job = null;
                    try { job = store.LatestJobFor(doc.DocumentId, meetingId); }
                    catch { /* статус фактов — бест-эфорт */ }
                    // Число сегментов — только для строк, где оно реально
                    // печатается (есть текст, нет ошибок извлечения, файл на месте).
                    var segCount = 0;
                    if (doc.CharCount > 0 && doc.Warnings.Count == 0 && !doc.Deleted && exists)
                    {
                        if (existing.TryGetValue(doc.DocumentId, out var vm)
                            && vm.Doc.CharCount == doc.CharCount
                            && vm.Doc.Warnings.Count == doc.Warnings.Count
                            && vm.Doc.Deleted == doc.Deleted)
                        {
                            segCount = vm.SegCount;
                        }
                        else
                        {
                            segCount = (await store.LoadSegmentsAsync(doc.DocumentId, ct)
                                .ConfigureAwait(false)).Count;
                        }
                    }
                    rows.Add(new AttachRowSnapshot(doc, segCount, exists, job));
                }
                return rows;
            }, ct).ConfigureAwait(true);
            ApplyAttachRows(snapshot, generation, meetingId);
        }
        catch (OperationCanceledException) { /* смена встречи/закрытие — новый снимок уже идёт */ }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"AttachFiles refresh failed: {ex.Message}", "Attach");
            if (generation == _attachGeneration && !_closed)
                FilesSection.Visibility = Visibility.Collapsed;
        }
        finally
        {
            // Флаг снимаем только от своего поколения: младший снимок не должен
            // снять чужой признак «снимок в полёте».
            if (generation == _attachGeneration)
            {
                _attachSnapshotInFlight = false;
                // События, пришедшие за время снимка, откладывались — данные
                // снимка для них могли устареть. Доигрываем точечно поверх
                // применённого списка (UpdateAttachRow сам разрулит, если
                // поколение/встреча уже другие или строки нет).
                if (_rowRefreshPending.Count > 0 && !_closed)
                {
                    var pending = new List<string>(_rowRefreshPending);
                    _rowRefreshPending.Clear();
                    foreach (var id in pending) UpdateAttachRow(id);
                }
            }
        }
    }

    /// <summary>Применить снимок к коллекции: пропавшие документы — удалить,
    /// новые — вставить по порядку ListMeetingDocuments, существующие — обновить
    /// подпись/карточку на месте. Любое применение строго под проверкой
    /// поколения и актуального _current — чужой снимок не затирает список.</summary>
    private void ApplyAttachRows(List<AttachRowSnapshot> snapshot,
        int generation, string meetingId)
    {
        // Устаревший снимок (смена встречи → новое поколение, закрытие окна) не
        // должен ни применяться, ни очищать чужие строки.
        if (_closed || generation != _attachGeneration || _current?.Id != meetingId)
            return;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in snapshot) keep.Add(row.Doc.DocumentId);
        // Дубли DocumentId в коллекции (если когда-либо возникли до введения
        // слива в снимке) сводим к одной строке: второй и далее экземпляры
        // удаляются, уникальность «одна строка на DocumentId» восстанавливается.
        var seenRow = new HashSet<string>(StringComparer.Ordinal);
        for (var i = AttachFiles.Count - 1; i >= 0; i--)
            if (!keep.Contains(AttachFiles[i].Doc.DocumentId)
                || !seenRow.Add(AttachFiles[i].Doc.DocumentId))
                AttachFiles.RemoveAt(i);
        for (var i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot[i];
            var doc = row.Doc;
            var subtitle = BuildFileSubtitle(row);
            AttachFileVm? vm = null;
            var cur = -1;
            for (var j = 0; j < AttachFiles.Count; j++)
                if (AttachFiles[j].Doc.DocumentId == doc.DocumentId) { vm = AttachFiles[j]; cur = j; break; }
            if (vm == null)
            {
                AttachFiles.Insert(Math.Min(i, AttachFiles.Count),
                    new AttachFileVm(doc, subtitle, row.SegCount));
            }
            else
            {
                vm.Update(doc, subtitle, row.SegCount);
                if (cur != i)
                {
                    AttachFiles.RemoveAt(cur);
                    AttachFiles.Insert(Math.Min(i, AttachFiles.Count), vm);
                }
            }
        }
        _attachRowsMeetingId = meetingId; // строки на экране теперь принадлежат этой встрече
        FilesHeaderText.Text = string.Format(L("L_Att_FilesHeaderFmt"), snapshot.Count);
        FilesSection.Visibility = snapshot.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Точечное обновление одной строки (смена статуса факт-джобы,
    /// удаление копии): карточка/файл/джоба перечитываются на пуле, обновляется
    /// только строка документа — список целиком не перечитывается, не сбрасываются
    /// ни свёрнутость секции, ни скролл. Повторные события по тому же документу
    /// сериализуются (_rowRefreshInFlight/_rowRefreshPending): последним
    /// применяется самый свежий снимок строки.</summary>
    private void UpdateAttachRow(string documentId)
    {
        if (_closed) return;
        // Полный снимок секции в полёте: точечное обновление, применённое до
        // ApplyAttachRows, было бы затёрто снимком (его job-статус мог
        // устареть за время чтения). Документ откладываем — RefreshAttachFiles
        // доиграет эти строки поверх применённого списка.
        if (_attachSnapshotInFlight)
        {
            _rowRefreshPending.Add(documentId);
            return;
        }
        if (!_rowRefreshInFlight.Add(documentId))
        {
            _rowRefreshPending.Add(documentId); // доиграть после конца идущего снимка
            return;
        }
        _ = UpdateAttachRowAsync(documentId);
    }

    /// <summary>Один снимок строки документа: store-чтения и File.Exists — на
    /// пуле; применение — под проверкой поколения и текущей встречи. Состав
    /// («документ должен быть в списке» — запись есть и привязана к встрече —
    /// против «строка показана») проверяется по IsLinkedToMeeting; расхождение
    /// откладывается в _attachListRefreshNeeded и запускается из finally —
    /// вызов RefreshAttachFiles до снятия _rowRefreshInFlight возвращал бы этот
    /// же документ в _rowRefreshPending и зацикливал снимки бесконечно.</summary>
    private async Task UpdateAttachRowAsync(string documentId)
    {
        try
        {
            var store = App.Current.Attachments;
            var meetingId = _current?.Id;
            if (store == null || meetingId == null || _closed) return;
            // Базовую строку (кэш числа сегментов) читаем на UI до await.
            AttachFileVm? baseVm = null;
            foreach (var vm in AttachFiles)
                if (vm.Doc.DocumentId == documentId) { baseVm = vm; break; }
            var generation = _attachGeneration;
            var ct = _attachCts?.Token ?? default;
            var (snap, linked) = await Task.Run(async () =>
            {
                var doc = store.Get(documentId);
                if (doc == null) return ((AttachRowSnapshot?)null, false);
                var exists = File.Exists(store.GetSourcePath(doc));
                FactJob? job = null;
                try { job = store.LatestJobFor(documentId, meetingId); }
                catch { /* статус фактов — бест-эфорт */ }
                // Привязка к выбранной встрече определяет, должна ли строка
                // вообще быть в списке: джоба живёт и после Delete/отвязки
                // (fact_jobs не трогаются), а строка — только по связи.
                var linked = store.IsLinkedToMeeting(documentId, meetingId);
                var segCount = 0;
                if (doc.CharCount > 0 && doc.Warnings.Count == 0 && !doc.Deleted && exists)
                {
                    // Кэш числа сегментов валиден только для неизменившейся карточки.
                    if (baseVm != null
                        && doc.CharCount == baseVm.Doc.CharCount
                        && doc.Warnings.Count == baseVm.Doc.Warnings.Count
                        && doc.Deleted == baseVm.Doc.Deleted)
                    {
                        segCount = baseVm.SegCount;
                    }
                    else
                    {
                        segCount = (await store.LoadSegmentsAsync(documentId, ct)
                            .ConfigureAwait(false)).Count;
                    }
                }
                return ((AttachRowSnapshot?)new AttachRowSnapshot(doc, segCount, exists, job), linked);
            }).ConfigureAwait(true);

            // Устаревший снимок (новое поколение/смена встречи/закрытие) не применяем —
            // RefreshAttachFiles нового поколения уже идёт или строка устарела.
            if (_closed || generation != _attachGeneration || _current?.Id != meetingId)
                return;
            AttachFileVm? row = null;
            foreach (var vm in AttachFiles)
                if (vm.Doc.DocumentId == documentId) { row = vm; break; }
            // Состав списка разруливает полный снимок: документ должен быть в
            // списке ровно тогда, когда запись есть и она привязана к выбранной
            // встрече (linked). Присутствие строки (row != null) расходится с
            // этим → просим полный снимок ЧЕРЕЗ _attachListRefreshNeeded: прямой
            // вызов RefreshAttachFiles здесь (пока documentId ещё в
            // _rowRefreshInFlight) возвращал бы документ в _rowRefreshPending и
            // сам себя доигрывал — вечный цикл «снимок → доигровка → снимок»
            // для джобы по отвязанному/удалённому документу. finally запускает
            // снимок уже ПОСЛЕ снятия in-flight; идущий снимок той же встречи
            // не перезапускаем — его доигровка нас покроет.
            if ((snap != null && linked) != (row != null))
                _attachListRefreshNeeded = true;
            if (snap == null || row == null)
                return;
            row.Update(snap.Doc, BuildFileSubtitle(snap), snap.SegCount);
        }
        catch (OperationCanceledException) { /* смена встречи/закрытие */ }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"AttachFiles row update failed: {ex.Message}", "Attach");
        }
        finally
        {
            _rowRefreshInFlight.Remove(documentId);
            // За время снимка пришли новые события по этому документу —
            // пересчитываем ещё раз на свежих данных.
            if (_rowRefreshPending.Remove(documentId) && !_closed)
                UpdateAttachRow(documentId);
            // Полный снимок по просьбе точечных обновлений — только после
            // снятия in-flight, иначе RefreshAttachFiles вернул бы документ в
            // _rowRefreshPending и цикл «снимок → доигровка» не завершился бы.
            // Летящий снимок не перезапускаем: он уже везёт свежие данные.
            if (_attachListRefreshNeeded && !_closed && !_attachSnapshotInFlight)
                RefreshAttachFiles();
        }
    }

    /// <summary>
    /// Подпись строки файла по готовому снимку: размер + состояние («Удалён»/
    /// «Недоступен»/ошибка извлечения/«фрагментов: N») + статус факт-джобы
    /// (Queued→…), если она была. Чистая строковая сборка — IO уже в снимке.
    /// </summary>
    private string BuildFileSubtitle(AttachRowSnapshot snap)
    {
        var doc = snap.Doc;
        var parts = new List<string> { AttachmentPickerWindow.FormatSize(doc.SizeBytes) };
        if (doc.Deleted)
            parts.Add(L("L_Att_StDeleted"));
        else if (!snap.Exists)
            parts.Add(L("L_Att_StUnavailable"));
        else if (doc.CharCount == 0 && doc.Warnings.Count == 0)
            parts.Add(L("L_Att_NoText"));
        else if (doc.Warnings.Count > 0)
            // Warnings (OCR: N стр., пропуски слоя) — не ошибка чтения: документ
            // прочитан, статус должен совпадать с чипом оверлея, не «Не удалось».
            parts.Add(string.Format(L("L_Att_StWarnFmt"), string.Join("; ", doc.Warnings)));
        else
            parts.Add(string.Format(L("L_Att_SegmentsFmt"), snap.SegCount));
        if (snap.Job != null) parts.Add(FactJobLabel(snap.Job));
        return string.Join(" · ", parts);
    }

    /// <summary>Локализованная метка статуса fact-джобы (прогресс — теми же
    /// L_MC_FactJob_*-форматами, что и карточка в Центре памяти).</summary>
    private string FactJobLabel(FactJob job) => job.Status switch
    {
        FactJobStatus.Queued => L("L_Att_FactsQueued"),
        // «n% · обработано k из m · сохранено f» — аудит: проценты должны оставаться видимыми.
        FactJobStatus.Running when job.RequestsDone > 0 && job.RequestsPlanned > 0 =>
            string.Format(L("L_MC_FactJob_RunningFullFmt"),
                job.RequestsDone * 100 / job.RequestsPlanned,
                job.RequestsDone, job.RequestsPlanned, job.FactsSaved),
        FactJobStatus.Running when job.RequestsPlanned > 0 =>
            string.Format(L("L_MC_FactJob_PartFmt"), job.RequestsDone + 1, job.RequestsPlanned),
        FactJobStatus.Running => L("L_MC_FactJob_Starting"),
        FactJobStatus.PendingReview => string.Format(L("L_Att_FactsPendingReviewFmt"), job.FactsPending),
        FactJobStatus.Done => string.Format(L("L_Att_FactsDoneFmt"), job.FactsSaved),
        FactJobStatus.NoFacts => L("L_Att_FactsNoFacts"),
        FactJobStatus.Canceled => string.Format(L("L_MC_FactJob_StoppedFmt"),
            job.RequestsDone, job.RequestsPlanned, job.FactsSaved),
        FactJobStatus.Interrupted => L("L_Att_FactsInterrupted"),
        FactJobStatus.Error when job.RequestsDone > 0 =>
            string.Format(L("L_MC_FactJob_ErrorFullFmt"),
                job.RequestsDone + 1, job.RequestsPlanned, job.FactsSaved, job.Error ?? ""),
        _ => string.Format(L("L_Att_FactsErrorFmt"), job.Error ?? ""),
    };

    /// <summary>Сворачивание/разворачивание списка файлов (шапка-кнопка «Файлы (N)»).</summary>
    private void OnFilesToggle(object sender, RoutedEventArgs e)
        => FilesList.Visibility = FilesList.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Обновление статуса фактов из фонового воркера (событие App.FactJobChanged, UI-поток).
    /// Пересчитывается только строка затронутого документа — без полного снимка
    /// секции на каждое событие воркера.</summary>
    private void OnFactJobChanged(FactJob job)
    {
        if (_closed || _current == null || job.MeetingId != _current.Id) return;
        UpdateAttachRow(job.DocumentId);
    }

    /// <summary>Открыть локальную копию во внешнем приложении (ассоциация Windows).
    /// File.Exists — на пуле: даже stat файла не держит UI-поток.</summary>
    private async void OnAttachmentOpen(object sender, RoutedEventArgs e)
    {
        var doc = FindDoc(sender);
        var store = App.Current.Attachments;
        if (doc == null || store == null) return;
        try
        {
            var path = store.GetSourcePath(doc);
            if (!await Task.Run(() => File.Exists(path)).ConfigureAwait(true))
            { ShowError(L("L_Att_StUnavailable")); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>Сохранить копию документа пользователю (SaveFileDialog + побайтовое
    /// чтение стора). ReadSourceBytes читает файл целиком — на пуле, не на UI.</summary>
    private async void OnAttachmentSaveAs(object sender, RoutedEventArgs e)
    {
        var doc = FindDoc(sender);
        var store = App.Current.Attachments;
        if (doc == null || store == null) return;
        // Двойной клик не ставит второе сохранение на время await IO.
        var key = "save:" + doc.DocumentId;
        if (!_attachOpsInFlight.Add(key)) return;
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = doc.DisplayName };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var bytes = await Task.Run(() => store.ReadSourceBytes(doc)).ConfigureAwait(true);
                await File.WriteAllBytesAsync(dlg.FileName, bytes).ConfigureAwait(true);
                App.Current.Notifier?.ShowInfo(L("L_Att_Title"), L("L_Meet_FileSaved"));
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        finally { _attachOpsInFlight.Remove(key); }
    }

    /// <summary>Привязать документ прошлой встречи к живой (тот же LinkToMeeting, что и у пикера).</summary>
    private async void OnAttachmentUseInCurrent(object sender, RoutedEventArgs e)
    {
        var doc = FindDoc(sender);
        if (doc == null) return;
        // Двойной клик во время await link не должен ставить вторую связь.
        var key = "link:" + doc.DocumentId;
        if (!_attachOpsInFlight.Add(key)) return;
        // Своего тоста здесь нет: LinkAttachmentToCurrentMeeting сам показывает
        // PushOverlayToast(Success) после реальной линковки — второй тост из
        // окна был дублем и всплывал даже при тихом провале внутри App.
        try { await App.Current.LinkAttachmentToCurrentMeeting(doc.DocumentId, extractFacts: false); }
        finally { _attachOpsInFlight.Remove(key); }
    }

    /// <summary>Постановка fact-джобы по документу прошлой встречи (снимок памяти — в App).
    /// Дубли подавляются дважды: _factEnqueueInFlight на время проверки+await enqueue
    /// (двойной клик не ставит вторую джобу) и по активному статусу последней джобы.
    /// LatestJobFor — синхронный SQLite-запрос, читаем на пуле.</summary>
    private async void OnAttachmentExtractFacts(object sender, RoutedEventArgs e)
    {
        var doc = FindDoc(sender);
        if (doc == null) return;
        var meetingId = _current?.Id;
        // Строка на экране может быть от прошлой встречи, пока летит снимок
        // новой: джоба с id новой встречи для непривязанного документа — мусор.
        if (meetingId == null || _attachRowsMeetingId != meetingId) return;
        // Гард ставим до проверки статуса: второй клик не должен втиснуться
        // между чтением активного статуса и постановкой джобы.
        if (!_factEnqueueInFlight.Add(doc.DocumentId)) return;
        try
        {
            // Повторный клик по уже идущему заданию не плодит дубликаты очереди.
            var active = await Task.Run(() =>
                App.Current.Attachments?.LatestJobFor(doc.DocumentId, meetingId ?? ""))
                .ConfigureAwait(true);
            if (active is { Status: FactJobStatus.Queued or FactJobStatus.Running }) return;
            try
            {
                await App.Current.EnqueueAttachmentFactJob(doc.DocumentId, meetingId);
            }
            catch (Exception ex) { ShowError(ex.Message); return; }
            UpdateAttachRow(doc.DocumentId);
            // Тост «в очереди» — только если джоба реально встала: при выключенной
            // памяти EnqueueAttachmentFactJob показывает свой диалог и ничего не
            // ставит, а успешно исполнившаяся мгновенно джоба уже видна в подписи.
            var job = await Task.Run(() =>
                App.Current.Attachments?.LatestJobFor(doc.DocumentId, meetingId ?? ""))
                .ConfigureAwait(true);
            if (job is { Status: FactJobStatus.Queued or FactJobStatus.Running })
                App.Current.Notifier?.ShowInfo(L("L_Att_Title"), L("L_Att_FactsQueuedToast"));
        }
        finally { _factEnqueueInFlight.Remove(doc.DocumentId); }
    }

    /// <summary>
    /// Удаление локальной копии: сначала PreviewDelete (сколько связей/заданий затронет),
    /// подтверждение модальным диалогом, затем DeleteDocumentAsync (байты стираются,
    /// в истории остаётся метка Deleted).
    /// </summary>
    private async void OnAttachmentDelete(object sender, RoutedEventArgs e)
    {
        var doc = FindDoc(sender);
        var store = App.Current.Attachments;
        if (doc == null || store == null) return;
        // Двойной клик до показа модального диалога не должен ставить второе
        // PreviewDelete/второй диалог подтверждения.
        var key = "del:" + doc.DocumentId;
        if (!_attachOpsInFlight.Add(key)) return;
        try
        {
            // PreviewDeleteAsync/DeleteDocumentAsync внутри — синхронные
            // операции стора под _gate; весь IO уходит на пул (async-лямбда —
            // Task.Run разворачивает вложенную задачу сам).
            var preview = await Task.Run(async () => await store.PreviewDeleteAsync(doc.DocumentId)
                .ConfigureAwait(false)).ConfigureAwait(true);
            var answer = AppMessageBox.Show(this,
                string.Format(L("L_Att_ConfirmDeleteFmt"), doc.DisplayName, preview.LinkedMeetings, preview.FactsCount),
                L("L_Att_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            await Task.Run(async () => await store.DeleteDocumentAsync(doc.DocumentId)
                .ConfigureAwait(false)).ConfigureAwait(true);
        }
        catch (Exception ex) { ShowError(ex.Message); return; }
        finally { _attachOpsInFlight.Remove(key); }
        UpdateAttachRow(doc.DocumentId);
        App.Current.RefreshAttachmentChips();
    }

    /// <summary>Документ строки по Tag кнопки (Tag = Doc из шаблона — карточка
    /// уже в памяти, лишний store.Get на UI-потоке не нужен).</summary>
    private StoredDocument? FindDoc(object sender)
        => (sender as FrameworkElement)?.Tag as StoredDocument;

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        App.Current.FactJobChanged -= OnFactJobChanged;
        try { _cts?.Cancel(); } catch { /* уже закрыто */ }
        try { _transcriptCts?.Cancel(); } catch { /* уже закрыто */ }
        // Ожидающие навигации (поиск/источник) должны завершиться, а не висеть.
        try { _transcriptRenderedTcs.TrySetResult(false); } catch { /* уже закрыто */ }
        // Отложенное выделение поиска по транскрипту (SelectTranscriptMatch в
        // ожидании рендера) глушим сменой версии — проверка _closed там уже есть,
        // версия страхует путь, где renderTask завершился до _closed=true.
        _transcriptSearchVersion++;
        try { _transcriptCts?.Dispose(); } catch { /* уже закрыто */ }
        _searchDebounce.Stop();
        try { _searchCts?.Cancel(); } catch { /* уже закрыто */ }
        try { _searchCts?.Dispose(); } catch { /* уже закрыто */ }
        // Освобождаем только сервис, созданный этим окном. App.MeetingSearch
        // общий для чата, «Спросить память» и истории — его закрывает App.
        if (_ownsSearch) { try { _search.Dispose(); } catch { /* уже закрыто */ } }
        // Отменяет летящий снимок файлов и делает все поздние применения no-op.
        CancelAttachLoad();
        _imageGeneration++; // поздние плитки превью не должны залезать в закрытое окно
        base.OnClosed(e);
    }
}

/// <summary>Строка файла в секции «Файлы (N)»: документ + готовая подпись (размер/статус).</summary>
public sealed class AttachFileVm : INotifyPropertyChanged
{
    public AttachFileVm(StoredDocument doc, string subtitle, int segCount = 0)
    {
        _doc = doc;
        _subtitle = subtitle;
        SegCount = segCount;
    }

    /// <summary>Свежая карточка + подпись без пересоздания строки (ItemsControl
    /// не дёргается, состояние кнопок/скролл сохраняются).</summary>
    internal void Update(StoredDocument doc, string subtitle, int segCount)
    {
        if (doc != _doc) { _doc = doc; PropertyChanged?.Invoke(this, DocChangedArgs); }
        SegCount = segCount;
        Subtitle = subtitle;
    }

    private StoredDocument _doc;
    private string _subtitle;
    public StoredDocument Doc => _doc;
    /// <summary>Число сегментов на момент последнего снимка — кэш для подписи
    /// «фрагментов: N», обновляется вместе с Doc.</summary>
    public int SegCount { get; private set; }
    public string Subtitle
    {
        get => _subtitle;
        private set
        {
            if (value == _subtitle) return;
            _subtitle = value;
            PropertyChanged?.Invoke(this, SubtitleChangedArgs);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private static readonly PropertyChangedEventArgs DocChangedArgs = new(nameof(Doc));
    private static readonly PropertyChangedEventArgs SubtitleChangedArgs = new(nameof(Subtitle));
}

/// <summary>Сохранённый ответ ИИ из журнала встречи (роль «answer»): исходный
/// текст с разметкой — форматирование применяется при отрисовке.</summary>
public sealed class AiAnswerVm
{
    public AiAnswerVm(string time, string text)
    {
        Time = time;
        Text = text;
    }

    /// <summary>Метка времени реплики "HH:mm:ss".</summary>
    public string Time { get; }
    /// <summary>Текст ответа модели (с разметкой, как пришёл от LLM).</summary>
    public string Text { get; }
    /// <summary>Подпись карточки ответа: время и автор.</summary>
    public string Header => $"{Time} · {UiText.Tr("ИИ", "AI")}";
}

