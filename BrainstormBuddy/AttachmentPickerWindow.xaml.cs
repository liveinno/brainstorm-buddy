using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Native;

namespace BrainstormBuddy;

/// <summary>
/// Немодальное окно прикрепления документов (owned поверх оверлея — чат остаётся
/// доступен; App хранит единственный экземпляр и при повторном открытии поднимает
/// его): «Выбрать файлы...» -> очередь <see cref="AttachmentImportRunner"/>, строки
/// с размером и статусом извлечения, галочка «Извлечь факты в память» (по
/// умолчанию выкл, недоступна при извлечённом тексте &gt; 120 000 символов или
/// выключенной памяти), «Прикрепить (N)»/«Закрыть».
/// «Прикрепить» линкует завершённые документы к текущей встрече и вооружает runner
/// (поздние завершения долинковывает App). «Закрыть» (крестик/Esc) обработку не
/// отменяет: очередь живёт в runner'е и продолжается в фоне, повторное открытие
/// показывает тот же сеанс.
/// Строку можно убрать до Commit (кнопка ✕ или Del) — непривязанная копия
/// удаляется, уже привязанная отвязывается от текущей встречи.
/// Exclude-from-capture применяется ТОЛЬКО если оверлей скрыт со скриншотов
/// (<see cref="OverlayWindow.IsHiddenFromCapture"/>).
/// </summary>
public partial class AttachmentPickerWindow : Window
{
    // Лимиты - единый источник BrainstormBuddy.Core.Attachments.AttachmentLimits.
    /// <summary>Реальный лимит извлечения фактов на документ — конфигурируемый
    /// (Attachments.FactExtractMaxChars; 120k было жёстким ограничением UI).</summary>
    private static int MaxFactChars
    {
        get
        {
            var v = App.Current.Config.Attachments.FactExtractMaxChars;
            return v > 0 ? v : 120_000;
        }
    }

    private sealed class Row : INotifyPropertyChanged
    {
        public Row(int key, string name, long size)
        {
            Key = key; DisplayName = name; SizeBytes = size;
            var ext = Path.GetExtension(name);
            Meta = string.IsNullOrEmpty(ext)
                ? FormatSize(size)
                : $"{ext.TrimStart('.').ToUpperInvariant()} · {FormatSize(size)}";
        }
        public int Key { get; }
        public string DisplayName { get; }
        public long SizeBytes { get; }
        public string AutomationId => $"AttachmentRow.Key{Key}";
        /// <summary>«PDF · 1.4 МБ» (вторая строка).</summary>
        public string Meta { get; }
        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnChanged(nameof(Status)); }
        }
        /// <summary>Состояние для счётчика «Готово N из M» и доступности Commit.</summary>
        public PickerItemState State { get; set; } = PickerItemState.Processing;
        public string? DocumentId;
        public bool Done;
        /// <summary>Кнопка «Остановить» у строки (чтение в работе/очереди).</summary>
        public bool ShowStop
        {
            get => _showStop;
            set { _showStop = value; OnChanged(nameof(ShowStop)); }
        }
        private bool _showStop;
        /// <summary>Кнопка «Повторить» у строки (остановлено / устранимая ошибка).</summary>
        public bool ShowRetry
        {
            get => _showRetry;
            set { _showRetry = value; OnChanged(nameof(ShowRetry)); }
        }
        private bool _showRetry;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly AttachmentImportRunner _runner;
    /// <summary>Собственный dedup-колбэк runner'а хранится полем: сравнение с
    /// method group по ссылке не сработало бы (новая делегат-обёртка), и окно
    /// осталось бы жить в runner'е после закрытия.</summary>
    private readonly Func<string, string?, bool> _linkedCheck;
    /// <summary>Колбэк App, действовавший до открытия окна: возвращается при
    /// закрытии, чтобы фоновая очередь не теряла дедуп после закрытия пикера.</summary>
    private readonly Func<string, string?, bool>? _previousLinkedCheck;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly Dictionary<int, Row> _byKey = new();
    private readonly HashSet<string> _completedDocIds = new(StringComparer.Ordinal);
    private readonly HashSet<int> _askedEncoding = new();
    /// <summary>Окно закрыто: отложенные из фонового потока обновления runner'а
    /// игнорируются (иначе поздний ENCODING-апдейт мог бы открыть диалог поверх
    /// уже закрытого пикера). Пишется/читается только в UI-потоке.</summary>
    private bool _closed;

    public AttachmentPickerWindow(AttachmentImportRunner runner, OverlayWindow? overlay)
    {
        InitializeComponent();
        _runner = runner;
        FileList.ItemsSource = _rows;
        // Окно ограничено рабочей областью монитора; на малом экране прокручивается середина.
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        // Пользовательская геометрия: только валидный конечный прямоугольник;
        // старый конфиг без PickerWindow оставляет CenterOwner и размер XAML.
        var pickerGeometry = App.Current.Config.Attachments.PickerWindow;
        if (WindowGeometryHelper.TryGetRestoreRect(
                pickerGeometry, MinWidth, MinHeight, MaxWidth, MaxHeight, out var pickerRect))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = pickerRect.Width;
            Height = pickerRect.Height;
            Left = pickerRect.Left;
            Top = pickerRect.Top;
        }
        if (overlay != null && overlay.IsVisible)
        {
            Owner = overlay;
            Topmost = overlay.Topmost;
        }
        _runner.ItemUpdated += OnItemUpdated;
        // Дедуп по контенту: документ, уже привязанный к встрече, не извлекаем повторно.
        _linkedCheck = IsLinkedToQueuedMeeting;
        _previousLinkedCheck = _runner.IsAlreadyLinked;
        _runner.IsAlreadyLinked = _linkedCheck;
        Closed += (_, _) =>
        {
            _closed = true;
            _runner.ItemUpdated -= OnItemUpdated;
            // Возвращаем колбэк App (а не null): очередь живёт после закрытия окна,
            // и дедуп нужен её фоновым пунктам. ReferenceEquals по сохранённому
            // делегату — только если его не перехватило другое окно.
            if (ReferenceEquals(_runner.IsAlreadyLinked, _linkedCheck))
                _runner.IsAlreadyLinked = _previousLinkedCheck;
        };
        // Заголовок окна и внутренний заголовок — один и тот же текст спеки §4.
        HeaderText.Text = T("L_Att_DlgTitle", L("L_Att_Title"));
        Title = HeaderText.Text;
        FormatsCaption.Text = T("L_Att_FormatsCaption",
            "PDF, DOCX, XLSX, PPTX, TXT, MD · до 10 файлов · до 25 МБ каждый");
        ModelNote.Text = Tf("L_Att_ModelNoteFmt",
            "Оригиналы хранятся на этом компьютере. Для ответов и извлечения фактов текст передаётся выбранной модели: {0}",
            CurrentModelName());
        FactsScopeNote.Text = FactsScopeText();
        // Галочка фактов персистит с заданием: показываем последнее пожелание
        // пользователя (снимок пойдёт в CommitSession и проживёт до долинковки).
        FactsCheck.IsChecked = App.Current.AttachmentFactsRequested;
        // Режим чтения PDF: комбо отражает Config.Attachments.PdfMode; при смене
        // режима runner перечитывает затронутые пункты (ReprocessForMode).
        PdfModeCombo.SelectedIndex =
            string.Equals(App.Current.Config.Attachments.PdfMode, "full",
                StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        // Параллельность OCR (Audio.AttachmentsOcrParallelism): auto/1/2/4.
        OcrParallelCaption.Text = T("L_Att_OcrParallel", "OCR-потоки:");
        OcrParAuto.Content = T("L_Att_OcrParallelAuto", "авто");
        var par = App.Current.Config.Audio.AttachmentsOcrParallelism ?? "auto";
        foreach (ComboBoxItem ci in OcrParallelCombo.Items)
            if (string.Equals(ci.Tag as string, par, StringComparison.OrdinalIgnoreCase))
            { OcrParallelCombo.SelectedItem = ci; break; }
        if (OcrParallelCombo.SelectedIndex < 0) OcrParallelCombo.SelectedIndex = 0;
        RefreshFactsAvailability();
        // Восстановление сессии: окно можно было закрыть на лету — пункты и
        // статусы живы в runner'е, перерисовываем каждый через OnItemUpdated.
        foreach (var item in _runner.BeginSession())
        {
            var row = new Row(item.Key, item.DisplayName, item.SizeBytes)
            {
                Status = L("L_Att_StQueued"),
                DocumentId = item.DocumentId,
            };
            _byKey[item.Key] = row;
            _rows.Add(row);
            OnItemUpdated(item);
        }
        RefreshEmpty();
        RefreshTally();
    }

    private string L(string key) => (string)TryFindResource(key) ?? key;
    private string Lf(string key, params object[] a) => string.Format(L(key), a);
    // Текст с литеральным фолбэком — ключ добавляет родитель в Strings.*.xaml.
    private string T(string key, string fallback) => (string)TryFindResource(key) ?? fallback;
    private string Tf(string key, string fallback, params object[] a) =>
        string.Format(T(key, fallback), a);

    /// <summary>Документ уже привязан к встрече, на которую клали файл
    /// (запомнена при enqueue — переключение встречи в процессе не влияет).</summary>
    private bool IsLinkedToQueuedMeeting(string documentId, string? meetingId)
    {
        var store = App.Current.Attachments;
        if (store == null) return false;
        var target = meetingId ?? App.Current.EnsureMeetingForAttachments();
        return target != null && store.ListMeetingIdsFor(documentId).Contains(target);
    }

    private string CurrentModelName()
    {
        var api = App.Current.Config.Api;
        var provider = api.ProviderId;
        var model = api.ChatModel;
        if (provider.Length == 0) return model.Length == 0 ? "—" : model;
        return model.Length == 0 ? provider : $"{provider} / {model}";
    }

    private string FactsScopeText()
    {
        try
        {
            var pid = App.Current.Config.Memory.ActiveProjectId;
            if (pid.HasValue && App.Current.MemoryStore != null)
            {
                var name = App.Current.MemoryStore.ListProjects()
                    .FirstOrDefault(p => p.Id == pid.Value)?.Name;
                if (!string.IsNullOrEmpty(name))
                    return Tf("L_Att_FactsScopeProjectFmt",
                        "После прикрепления факты будут извлечены по настройкам памяти — в проект «{0}»", name);
            }
        }
        catch
        {
            // Не смогли прочитать список проектов — показываем глобальный фолбэк.
        }
        return T("L_Att_FactsScopeGlobal",
            "После прикрепления факты будут извлечены по настройкам памяти — в глобальную память встречи");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Окно владельца (оверлей) скрыто со скриншотов → прячем и диалог.
        if (Owner is OverlayWindow ov && ov.IsHiddenFromCapture)
            WindowHelper.ApplyExcludeFromCapture(this);
    }

    // ---- Выбор файлов ----

    private void OnChooseFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = L("L_Att_FilesFilter"),
            Title = L("L_Att_Title"),
        };
        if (dlg.ShowDialog(this) != true) return;

        var errors = new List<string>();
        // Лимит батча — по файлам текущей сессии диалога; лимит активных —
        // прилинкованные к встрече + готовые/обрабатывающиеся в этой сессии.
        // Строки с ошибкой в счёт не входят (прикреплены не будут).
        long batchBytes = _rows.Sum(r => r.State != PickerItemState.NotAttachable ? r.SizeBytes : 0);
        var meetingId = App.Current.EnsureMeetingForAttachments();
        // Выжившие после Commit строки сессии уже входят в CountMeetingDocuments:
        // не считаем их второй раз, иначе лимит 10 срабатывает на пяти файлах.
        var linkedIds = new HashSet<string>(
            (meetingId != null
                ? App.Current.Attachments?.ListMeetingDocuments(meetingId)
                : null)?.Select(a => a.Doc.DocumentId) ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        int active = linkedIds.Count
            + _rows.Count(r => r.State != PickerItemState.NotAttachable
                && (r.DocumentId is null || !linkedIds.Contains(r.DocumentId)));
        foreach (var path in dlg.FileNames)
        {
            if (active >= AttachmentLimits.MaxActiveDocumentsPerMeeting)
            {
                errors.Add(Lf("L_Att_TooManyFmt", AttachmentLimits.MaxActiveDocumentsPerMeeting));
                break;
            }
            FileInfo fi;
            try { fi = new FileInfo(path); }
            catch { errors.Add(Lf("L_Att_ErrFileFmt", Path.GetFileName(path), "?")); continue; }
            if (!fi.Exists) { errors.Add(Lf("L_Att_ErrFileFmt", fi.Name, L("L_Att_ErrNoFile"))); continue; }
            if (fi.Length > AttachmentLimits.MaxFileBytes)
            {
                errors.Add(Lf("L_Att_ErrFileFmt", fi.Name,
                    Lf("L_Att_ErrTooBigFmt", FormatSize(AttachmentLimits.MaxFileBytes))));
                continue;
            }
            if (batchBytes + fi.Length > AttachmentLimits.MaxImportBytes)
            {
                errors.Add(Lf("L_Att_ErrFileFmt", fi.Name, L("L_Att_ErrBatchLimit")));
                continue;
            }
            batchBytes += fi.Length;
            if (AddFile(fi.FullName, meetingId)) active++;
        }
        if (errors.Count > 0) ShowError(string.Join("\n", errors));
        else HideError();
    }

    /// <summary>Смена режима чтения PDF — пишем в конфиг и просим runner
    /// перечитать затронутые пункты (§5.1): fast-прогоны с пропущенными страницами
    /// и OCR-устранимые отказы встают заново с новым режимом (без дублей
    /// документа и фактов — сегменты переиспользуются). «Готово» не трогается.</summary>
    private void OnPdfModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PdfModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;
        var a = App.Current.Config.Attachments;
        if (string.Equals(a.PdfMode, tag, StringComparison.OrdinalIgnoreCase)) return;
        a.PdfMode = tag;
        var mode = string.Equals(tag, "full", StringComparison.OrdinalIgnoreCase)
            ? PdfReadMode.Full : PdfReadMode.Fast;
        var n = _runner.ReprocessForMode(mode, ParallelismFromConfig(), a.PdfOcrMaxPages);
        if (n > 0) ShowError(Tf("L_Att_RequeuedFmt", "Режим изменён — {0} документ(ов) перечитывается заново", n));
        else HideError();
    }

    /// <summary>Параллельность OCR (Audio.AttachmentsOcrParallelism): auto/1/2/4.</summary>
    private void OnOcrParallelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OcrParallelCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            { App.Current.Config.Audio.AttachmentsOcrParallelism = tag; App.Current.SaveConfigSafe(); }
    }

    private static int ParallelismFromConfig() =>
        PdfReadOptions.ParseParallelism(App.Current.Config.Audio.AttachmentsOcrParallelism);

    /// <summary>Режим чтения PDF из Config.Attachments + параллельность из Audio —
    /// фиксируется в момент импорта файла. KeepPartialOnCancel: «Остановить обработку»
    /// сохраняет уже прочитанные страницы — повтор дочитывает с них (§5.2).</summary>
    private static PdfReadOptions CurrentPdfOptions()
    {
        var a = App.Current.Config.Attachments;
        return PdfReadOptions.FromConfig(a.PdfMode, a.PdfOcrMaxPages,
            App.Current.Config.Audio.AttachmentsOcrParallelism)
            with { KeepPartialOnCancel = true };
    }

    private bool AddFile(string path, string? meetingId)
    {
        int key = _runner.Enqueue(path, CurrentPdfOptions(), meetingId);
        if (key < 0 || _byKey.ContainsKey(key)) return false;
        var fi = new FileInfo(path);
        var row = new Row(key, fi.Name, fi.Exists ? fi.Length : 0) { Status = L("L_Att_StQueued") };
        _byKey[key] = row;
        _rows.Add(row);
        RefreshEmpty();
        RefreshTally();
        return true;
    }

    // ---- Статусы из runner ----

    private void OnItemUpdated(AttachmentImportRunner.ImportItem item)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => OnItemUpdated(item)); return; }
        if (_closed) return;
        if (!_byKey.TryGetValue(item.Key, out var row))
        {
            // Пункт появился вне диалога (или снят и «воскрес» из старого снимка).
            if (item.ExtractFinished || item.Error != null || item.AlreadyLinked) return;
            row = new Row(item.Key, item.DisplayName, item.SizeBytes);
            _byKey[item.Key] = row;
            _rows.Add(row);
            RefreshEmpty();
        }
        row.DocumentId ??= item.DocumentId;
        // Пункт поставлен на перечитку (смена режима/RetryItem) — статус меняем
        // явно, чтобы было видно, какие строки переобрабатываются.
        if (item.Reprocessing)
        {
            row.Done = false;
            row.State = PickerItemState.Processing;
            row.Status = T("L_Att_StReprocessing", "Перечитываю с новыми настройками…");
            row.ShowStop = true;
            row.ShowRetry = false;
            RefreshTally();
            return;
        }
        if (item.StoppedByUser)
        {
            // «Остановлено» — частичный результат сохранён в документе,
            // «Повторить» продолжит с обработанных страниц (ReuseSegments).
            row.Done = true;
            row.State = PickerItemState.Ready;
            row.Status = T("L_Att_StStopped", "Остановлено — часть страниц обработана")
                + StatsSuffix(item.DocStats);
            row.ShowStop = false;
            row.ShowRetry = true;
            if (item.DocumentId != null) _completedDocIds.Add(item.DocumentId);
            RefreshFactsAvailability();
            RefreshTally();
            return;
        }
        if (item.Error != null && item.Error.StartsWith("ENCODING:", StringComparison.Ordinal))
        {
            // Кодировка неоднозначна → предлагаем явный выбор и повторяем с ней.
            var payload = item.Error.Substring("ENCODING:".Length);
            var enc = payload.Split('|')[0];
            row.Done = true;
            row.State = PickerItemState.NotAttachable;
            row.Status = L("L_Att_StEncoding");
            if (_askedEncoding.Add(item.Key))
            {
                var answer = AppMessageBox.Show(this,
                    Lf("L_Att_EncodingAskFmt", row.DisplayName, enc),
                    L("L_Att_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                App.Current.Logger.Info(
                    $"Attach: кодировка '{row.DisplayName}' key={item.Key} ответ={answer}", "Attach");
                if (answer == MessageBoxResult.Yes)
                {
                    row.Done = false;
                    row.State = PickerItemState.Processing;
                    row.Status = L("L_Att_StQueued");
                    _runner.RetryWithEncoding(item.Key, enc);
                }
            }
            RefreshTally();
            return;
        }
        if (item.Error != null)
        {
            row.Done = true;
            row.State = PickerItemState.NotAttachable;
            row.Status = item.ErrorSeeLog
                ? Tf("L_Att_StFailedLogFmt", "Не удалось прочитать: {0} — подробности в логе", item.Error)
                : Lf("L_Att_StFailedFmt", item.Error);
            // Ошибка извлечения: документ не прикрепляется (спека §5).
            row.ShowStop = false;
            row.ShowRetry = item.RetryableWithOcr;
            if (item.DocumentId != null) _completedDocIds.Remove(item.DocumentId);
            RefreshFactsAvailability();
            RefreshTally();
            return;
        }
        if (item.AlreadyLinked)
        {
            // Контент-дубликат уже привязанного документа — не импортируем повторно.
            row.Done = true;
            row.State = PickerItemState.AlreadyLinked;
            row.Status = T("L_Att_StAlreadyLinked", "Уже прикреплён к этой встрече");
            RefreshTally();
            return;
        }
        if (item.ImportFinished && !item.ExtractFinished)
        {
            row.State = PickerItemState.Processing;
            // Живой прогресс чтения: «стр. X/Y» для PDF — Done = число ЗАВЕРШЁННЫХ
            // страниц (параллельный OCR может заканчивать их не по порядку).
            row.Status = item.ProgressTotal > 0
                ? Tf("L_Att_StReadingPageFmt", "Читаю документ… стр. {0}/{1}",
                     Math.Min(item.ProgressDone, item.ProgressTotal), item.ProgressTotal)
                : T("L_Att_StReading", "Читаю документ…");
            row.ShowStop = true;
            row.ShowRetry = false;
        }
        else if (item.ExtractFinished)
        {
            row.Done = true;
            row.State = PickerItemState.Ready;
            row.ShowStop = false;
            // Пропущенные страницы без слоя — подсказка «Повторить» в full-режиме.
            row.ShowRetry = item.SkippedNoText > 0;
            // Итоговая строка: «Готов · OCR: N стр.…» при предупреждениях +
            // пометрика страниц (OCR — успех, не ошибка; пустые видны числом).
            row.Status = (item.Warnings is { Count: > 0 }
                ? Tf("L_Att_StWarnFmt", "Готов · {0}", string.Join("; ", item.Warnings))
                : L("L_Att_StReady")) + StatsSuffix(item.DocStats);
            if (item.DocumentId != null) _completedDocIds.Add(item.DocumentId);
            RefreshFactsAvailability();
        }
        else
        {
            row.State = PickerItemState.Processing;
            row.Status = L("L_Att_StQueued");
        }
        RefreshTally();
    }

    /// <summary>Галочка фактов недоступна только без памяти (сессионно).
    /// Документ длиннее <see cref="MaxFactChars"/> больше НЕ гасит извлечение для
    /// остальных: он помечается в заметке, его fact-джоба упадёт по per-doc лимиту
    /// («разделите файл»), остальные документы извлекутся как обычно.</summary>
    private void RefreshFactsAvailability()
    {
        var tooLong = new List<string>();
        if (App.Current.MemoryStore == null)
        {
            FactsCheck.IsEnabled = false;
            FactsCheck.IsChecked = false;
            FactsNote.Text = L("L_Att_ErrNoMemory");
            FactsNote.Visibility = Visibility.Visible;
            return;
        }
        var store = App.Current.Attachments;
        if (store != null)
        {
            foreach (var docId in _completedDocIds)
            {
                var doc = store.Get(docId);
                if (doc?.CharCount > MaxFactChars)
                    tooLong.Add($"{doc.DisplayName} ({doc.CharCount:N0} симв.)");
            }
        }
        FactsCheck.IsEnabled = true;
        if (tooLong.Count > 0)
        {
            FactsNote.Text = tooLong.Count == 1
                ? Lf("L_Att_FactsTooLongSkipFmt", tooLong[0], MaxFactChars)
                : Lf("L_Att_FactsTooLongSkipManyFmt", tooLong.Count, MaxFactChars,
                    string.Join("; ", tooLong));
            FactsNote.Visibility = Visibility.Visible;
        }
        else
        {
            FactsNote.Text = "";
            FactsNote.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Счётчик «Готово N из M» + кнопка «Прикрепить (N)» (спека §4/§5).</summary>
    private void RefreshTally()
    {
        var tally = AttachmentPickerSummary.Tally(_rows.Select(r => r.State));
        if (tally.Total == 0)
        {
            ReadyCounter.Visibility = Visibility.Collapsed;
        }
        else
        {
            var text = tally.HasUnattachable
                ? Tf("L_Att_ReadyCountFmt", "Готово {0} из {1}", tally.ReadyToAttach, tally.Total)
                  + T("L_Att_ReadyTail", " — остальные не будут прикреплены")
                : Tf("L_Att_ReadyCountFmt", "Готово {0} из {1}", tally.ReadyToAttach, tally.Total);
            ReadyCounter.Text = text;
            ReadyCounter.Visibility = Visibility.Visible;
        }
        ApplyBtn.IsEnabled = tally.CanCommit;
        ApplyBtnText.Text = Tf("L_Att_ApplyFmt", "Прикрепить ({0})", tally.ReadyToAttach);
    }

    private void RefreshEmpty()
        => EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ShowError(string msg) { ErrorText.Text = msg; ErrorText.Visibility = Visibility.Visible; }
    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    // ---- Действия строки: «Убрать», «Остановить», «Повторить» ----

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int key && _byKey.TryGetValue(key, out var row))
            RemoveRow(row);
    }

    private void OnFileListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Delete && FileList.SelectedItem is Row row)
        {
            RemoveRow(row);
            e.Handled = true;
        }
    }

    /// <summary>«Остановить» строку: чтение прерывается, обработанные страницы
    /// сохраняются — строка получает статус «Остановлено» и кнопку «Повторить».</summary>
    private void OnStopRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int key)
            _runner.StopItem(key);
    }

    /// <summary>«Повторить» строку: продолжение с сохранённых страниц
    /// (ReuseSegments) текущими настройками режима/OCR.</summary>
    private void OnRetryRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int key)
            _runner.RetryItem(key, CurrentPdfOptions());
    }

    /// <summary>
    /// Снять выбранный документ до Commit: runner снимает пункт из очереди/снимков
    /// и чистит сироту; документ, привязанный к встрече ДО этой сессии, отвязываем
    /// от текущей встречи — локальная копия остаётся (спека §3).
    /// </summary>
    private void RemoveRow(Row row)
    {
        var last = _runner.Remove(row.Key);
        var docId = last?.DocumentId ?? row.DocumentId;
        if (docId != null) _completedDocIds.Remove(docId);
        _byKey.Remove(row.Key);
        _rows.Remove(row);
        if (docId != null && row.State == PickerItemState.AlreadyLinked)
            _ = UnlinkFromCurrentMeetingAsync(docId);
        RefreshEmpty();
        RefreshFactsAvailability();
        RefreshTally();
    }

    private async Task UnlinkFromCurrentMeetingAsync(string documentId)
    {
        try
        {
            var store = App.Current.Attachments;
            var meetingId = App.Current.MeetingJournal?.CurrentId;
            if (store == null || meetingId == null) return;
            await store.UnlinkFromMeetingAsync(documentId, meetingId, CancellationToken.None);
            App.Current.RefreshAttachmentChips();
        }
        catch (Exception ex)
        {
            App.Current.Logger.Warn($"Attach: unlink {documentId} failed: {ex.Message}", "Attach");
        }
    }

    // ---- Кнопки ----

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // Commit — единая точка в runner'е: зафиксированные при enqueue встречи
        // и флаг фактов уезжают обработчику App (CommitSessionHandler), который
        // линкует и ставит fact-job ровно один раз — в т.ч. для документов,
        // доехавших уже после закрытия окна. Окно просто закрывается.
        App.Current.AttachmentFactsRequested = FactsCheck.IsChecked == true && FactsCheck.IsEnabled;
        _runner.CommitSession(App.Current.AttachmentFactsRequested);
        Close();
    }

    /// <summary>«Закрыть» (и крестик/Esc): окно закрывается, сессия продолжает
    /// работать в фоне — прогресс виден карточкой в чате, повторное открытие
    /// показывает тот же сеанс с живым состоянием (BeginSession).</summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>«Остановить обработку»: прерывает чтение очереди и всех файлов
    /// в работе, СОХРАНЯЯ обработанные страницы. Повтор — «Повторить» у строки;
    /// документ не дублируется. Окно остаётся открытым (видно новый статус).</summary>
    private void OnStopAll(object sender, RoutedEventArgs e)
    {
        _runner.StopAll();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Закрытие окна НЕ отменяет обработку (аудит §5.2): сессия живёт в
        // runner'е. Полный отказ с очисткой сирот — CancelSession из App при
        // явном отказе пользователя от всей операции.
        // Геометрия: свёрнутое/невалидное окно не пишем; закрытие после
        // перетаскивания/ресайза сохраняет Left/Top/Width/Height.
        App.Current.Config.Attachments.PickerWindow ??= new WindowGeometryConfig();
        if (WindowGeometryHelper.TryCapture(
                this, App.Current.Config.Attachments.PickerWindow, MinWidth, MinHeight))
            App.Current.SaveConfigSafe();
        base.OnClosing(e);
    }

    /// <summary>
    /// Пометрика страниц PDF к статусу: « · стр. 120/360 · OCR 2 · пусто 3».
    /// Успешный OCR — не ошибка; неполный прогон виден по Processed/Total.
    /// </summary>
    private string StatsSuffix(BrainstormBuddy.Core.Attachments.DocumentPageStats? ps)
    {
        if (ps is not { HasData: true }) return "";
        var s = Tf("L_Att_StPagesFmt", " · стр. {0}/{1}", ps.Processed, ps.Total);
        if (ps.Ocr > 0) s += Tf("L_Att_StOcrFmt", " · OCR {0}", ps.Ocr);
        if (ps.Empty > 0) s += Tf("L_Att_StEmptyFmt", " · пусто {0}", ps.Empty);
        return s;
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.#} МБ";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} КБ";
        return $"{bytes} Б";
    }
}
