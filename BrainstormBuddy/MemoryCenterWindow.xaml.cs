using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Services;
using BrainstormBuddy.Transcription;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace BrainstormBuddy;

/// <summary>
/// Memory Center (спека §4.5): отдельное окно просмотра и правки долговременной памяти.
/// Стор приходит снаружи через <see cref="Initialize"/> — окно НЕ владеет стором и не dispose'ит его.
/// Все обращения к стору на UI-потоке (методы синхронные, lock внутри SqliteMemoryStore).
/// </summary>
public partial class MemoryCenterWindow : Window
{
    private const string LogCategory = "Memory";

    private IMemoryStore? _store;
    private LoggingService? _log;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private readonly ObservableCollection<FactRow> _facts = new();
    private readonly ObservableCollection<FactRow> _history = new();
    private readonly ObservableCollection<MeetingRow> _meetings = new();
    private readonly ObservableCollection<ProjectRow> _projects = new();
    private readonly ObservableCollection<MemoryPerson> _people = new();
    private readonly ObservableCollection<JobRow> _jobs = new();
    /// <summary>Последний fact-job (извлечение фактов из вложения) для карточки на вкладке «Задачи».</summary>
    private FactJob? _factJob;

    private Dictionary<long, string> _projectNames = new();
    private Dictionary<long, string> _personNames = new();
    private Dictionary<long, string> _meetingTitles = new();
    private long? _activeProjectId;

    private MemoryProject _editingProject = new();
    /// <summary>Режим «создание проекта» (волна 05): редактор виден при SelectedItem==null; в базу не пишется.</summary>
    private bool _creatingProject;
    private MemoryPerson _editingPerson = new();

    /// <summary>
    /// Callback «открыть журнал встречи» — подключает App (W1): обычно
    /// <c>path => { App.ShowMeetingHistoryFile(path); return Task.CompletedTask; }</c>.
    /// Пока не задан, двойной клик по встрече показывает заглушку-подсказку.
    /// </summary>
    public Func<string, Task>? OpenJournal { get; set; }

    public MemoryCenterWindow()
    {
        InitializeComponent();
        FactsList.ItemsSource = _facts;
        HistoryList.ItemsSource = _history;
        MeetingsList.ItemsSource = _meetings;
        ProjectsList.ItemsSource = _projects;
        PeopleList.ItemsSource = _people;
        JobsList.ItemsSource = _jobs;
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RefreshFacts(); };
        // Адаптивная панель фильтров (волна 05): SizeChanged подписан здесь — элемент существует
        // после InitializeComponent (статическая разметка), к обработчику SizeChanged XAML не привязан.
        if (FiltersPanel is { } filtersPanel)
            filtersPanel.SizeChanged += OnFiltersPanelSizeChanged;
        UpdateProjectPanelState();   // стартовое состояние правой панели: «ничего не выбрано»
        Loaded += (_, _) =>
        {
            if (App.Current is { } app) app.FactJobChanged += OnFactJobChanged;
            RefreshCurrentTab();
            RefreshFactJobCard();
        };
        Closed += (_, _) =>
        {
            if (App.Current is { } app) app.FactJobChanged -= OnFactJobChanged;
        };
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;


    /// <summary>Подключить зависимости и наполнить окно. Можно вызывать повторно (rebind).</summary>
    public void Initialize(IMemoryStore store, IMemoryRecallService? recall, LoggingService log)
    {
        _store = store;
        _log = log;
        _ = recall; // recall резервируется под будущий hybrid-поиск; сейчас поиск — SearchFts (спека §4.5)
        DbPathText.Text = store.DbPath;
        RefreshCurrentTab();
    }
    private static string Lf(string key, params object[] args)
        => string.Format(CultureInfo.CurrentUICulture, L(key), args);

    private void Warn(string msg) => _log?.Warn(msg, LogCategory);
    private void Error(string msg, Exception ex) => _log?.Error(msg, ex, LogCategory);

    private void ShowInfo(string msg) =>
        AppMessageBox.Show(this, msg, L("L_MC_Title"), MessageBoxButton.OK, MessageBoxImage.Information);

    private static T? TagEnum<T>(ComboBox box) where T : struct, Enum =>
        box.SelectedItem is ComboBoxItem { Tag: string s } && Enum.TryParse<T>(s, out var v) ? v : null;

    /// <summary>Ленивый refresh: грузим данные только видимой вкладки (как OnSettingsTabChanged).</summary>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs)) return;
        RefreshCurrentTab();
    }

    private void RefreshCurrentTab()
    {
        if (_store == null)
        {
            StatusBar.Text = L("L_MC_NoStore");
            return;
        }
        if (MainTabs.SelectedItem == FactsTab) RefreshFacts();
        else if (MainTabs.SelectedItem == MeetingsTab) RefreshMeetings();
        else if (MainTabs.SelectedItem == ProjectsTab) RefreshProjects();
        else if (MainTabs.SelectedItem == PeopleTab) RefreshPeople();
        else if (MainTabs.SelectedItem == JobsTab) RefreshJobs();
        RefreshStatusBar();
    }

    private void RefreshStatusBar()
    {
        if (_store == null) return;
        try
        {
            var s = _store.GetStats();
            // §MEMORY HEALTH: помимо счётчиков GetStats — активные/устаревшие/закреплённые.
            var all = _store.ListItems(status: null, limit: int.MaxValue);
            var active = all.Count(i => i.Status == MemoryItemStatus.Active);
            var superseded = all.Count(i => i.Status == MemoryItemStatus.Superseded);
            var pinned = all.Count(i => i.Pinned);
            StatusBar.Text = Lf("L_MC_StatusBarFmt", s.Items, s.Meetings, s.PendingJobs)
                + $"  ·  {L("L_MC_Status_Active")} {active}  ·  {L("L_MC_Status_Superseded")} {superseded}"
                + $"  ·  ★ {pinned}  ·  {FormatBytes(s.DbBytes)}";
        }
        catch (Exception ex) { Error("GetStats failed", ex); }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };

    private void RefreshLookups()
    {
        if (_store == null) return;
        _projectNames = _store.ListProjects(includeArchived: true).ToDictionary(p => p.Id, p => p.Name);
        _personNames = _store.ListPeople().ToDictionary(p => p.Id, p => p.Name);
        _meetingTitles = _store.ListMeetings(500).ToDictionary(m => m.Id, m => m.Title);
        _activeProjectId = (Application.Current as App)?.Config?.Memory?.ActiveProjectId;
    }

    private string ScopeBadge(MemoryItem it) => it.ScopeType switch
    {
        MemoryScopeType.Global => L("L_MC_Scope_Global"),
        MemoryScopeType.Project => $"{L("L_MC_Scope_Project")}: {Lookup(_projectNames, it.ScopeId)}",
        MemoryScopeType.Person => $"{L("L_MC_Scope_Person")}: {Lookup(_personNames, it.ScopeId)}",
        MemoryScopeType.Meeting => $"{L("L_MC_Scope_Meeting")}: {Lookup(_meetingTitles, it.ScopeId)}",
        _ => it.ScopeType.ToString(),
    };

    private static string Lookup(Dictionary<long, string> map, long? id)
        => id.HasValue && map.TryGetValue(id.Value, out var n) ? n : $"#{id}";

    private string MetaText(MemoryItem it)
    {
        var parts = new List<string>
        {
            it.UpdatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentUICulture),
            Lf("L_MC_ImportanceFmt", it.Importance),
        };
        if (it.SourceMeetingId is { } mid)
            parts.Add(Lf("L_MC_MeetingFmt", mid));
        if (!string.IsNullOrEmpty(it.Source) && it.Source != "extraction")
            parts.Add(it.Source);
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------ Факты -------------------

    private void OnFactFilterChanged(object sender, RoutedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnFactsSelectionChanged(object sender, SelectionChangedEventArgs e)
        => FactDeleteSelectedBtn.IsEnabled = FactsList.SelectedItems.Count > 0;

    private void OnFactSelectAll(object sender, RoutedEventArgs e)
        => FactsList.SelectAll();

    /// <summary>
    /// Адаптивная перестройка фильтров (волна 05, порог 600 DIP по ActualWidth панели):
    /// узко — каждый фильтр на всю строку панели; широко — фиксированные 160, как в XAML.
    /// Только layout: ни стора, ни данных не трогает, выбранные значения сохраняются.
    /// </summary>
    private void OnFiltersPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FiltersPanel is null || ScopeFilterCol is null || TypeFilterCol is null || StatusFilterCol is null)
            return;                                     // разметка могла ещё не собраться — выходим тихо
        var narrow = FiltersPanel.ActualWidth < 600;
        var width = narrow ? FiltersPanel.ActualWidth : 160;
        ScopeFilterCol.Width = width;
        TypeFilterCol.Width = width;
        StatusFilterCol.Width = width;
    }

    // Delete в списке = тот же путь, что кнопка «Удалить выбранные».
    private void OnFactsListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Delete && FactsList.SelectedItems.Count > 0)
        {
            OnFactDeleteSelected(sender, e);
            e.Handled = true;
        }
    }

    // Пакетное удаление фактов: без per-item окна, одно подтверждение со счётчиком.
    private void OnFactDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        var rows = FactsList.SelectedItems.OfType<FactRow>().ToList();
        if (rows.Count == 0) return;
        var q = Lf("L_MCA_ConfirmDeleteManyFmt", rows.Count);
        if (AppMessageBox.Show(this, q, L("L_MC_Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var deleted = 0;
        foreach (var row in rows)
        {
            try { _store.HardDeleteItem(row.Item.Id); deleted++; }
            catch (Exception ex) { Error($"HardDelete item #{row.Item.Id} failed", ex); }
        }
        _log?.Info($"Memory Center: {deleted}/{rows.Count} items deleted", LogCategory);
        RefreshFacts();
        RefreshStatusBar();
    }

    private void RefreshFacts()
    {
        if (_store == null) return;
        try
        {
            RefreshLookups();
            var scope = TagEnum<MemoryScopeType>(ScopeFilter);
            var type = TagEnum<MemoryItemType>(TypeFilter);
            var status = TagEnum<MemoryItemStatus>(StatusFilter);
            var query = SearchBox.Text.Trim();

            List<MemoryItem> items;
            if (query.Length > 0)
            {
                items = _store.SearchFts(query, 100)
                    .Select(h => h.Item)
                    .Where(i => (!scope.HasValue || i.ScopeType == scope)
                             && (!status.HasValue || i.Status == status)
                             && (!type.HasValue || i.Type == type))
                    .ToList();
            }
            else
            {
                items = _store.ListItems(scope, null, status, 500)
                    .Where(i => !type.HasValue || i.Type == type)
                    .ToList();
            }

            _facts.Clear();
            foreach (var it in items)
                _facts.Add(new FactRow(it, L($"L_MC_Type_{it.Type}"), ScopeBadge(it), MetaText(it), TagText(it)));
            FactsEmpty.Visibility = _facts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Error("RefreshFacts failed", ex); }
    }

    /// <summary>Теги факта для строки списка («#tag  #tag2»); сбой чтения тегов не должен ломать список.</summary>
    private string TagText(MemoryItem it)
    {
        if (_store == null) return "";
        try
        {
            var tags = _store.GetTags(it.Id);
            return tags.Count > 0 ? string.Join("  ", tags.Select(t => "#" + t)) : "";
        }
        catch (Exception ex) { Warn($"GetTags({it.Id}) failed: {ex.Message}"); return ""; }
    }

    private static FactRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as FactRow;

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        if (_store == null || RowOf(sender) is not { } row || sender is not ToggleButton tb) return;
        try
        {
            row.Item.Pinned = tb.IsChecked == true;
            _store.UpdateItem(row.Item);
        }
        catch (Exception ex) { Error($"Pin item #{row.Item.Id} failed", ex); }
    }

    private void OnFactEdit(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        row.EditText = row.Item.Content;
        row.IsEditing = true;
    }

    private void OnFactCancelEdit(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) row.IsEditing = false;
    }

    private void OnFactSave(object sender, RoutedEventArgs e)
    {
        if (_store == null || RowOf(sender) is not { } row) return;
        var content = row.EditText.Trim();
        if (content.Length == 0 || content == row.Item.Content) { row.IsEditing = false; return; }
        try
        {
            row.Item.Content = content;
            row.Item.Source = "user-edited";          // спека: ручная правка → user-edited (больший trust)
            row.Item.UpdatedAtUtc = DateTime.UtcNow;
            _store.UpdateItem(row.Item);
            row.IsEditing = false;
            RefreshFacts();
        }
        catch (Exception ex) { Error($"Edit item #{row.Item.Id} failed", ex); }
    }

    /// <summary>Ручное создание факта (manual review mode: спека «Memory items пользователь создаёт сам»).</summary>
    private void OnFactNew(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        var item = PromptNewFact();
        if (item == null) return;
        try
        {
            _store.InsertItem(item);
            _log?.Info($"Item #{item.Id} created manually from Memory Center", LogCategory);
            RefreshFacts();
            RefreshStatusBar();
        }
        catch (Exception ex) { ShowError("Insert item failed", ex); }
    }

    private void OnFactDelete(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) DoDelete(row);
    }

    /// <summary>Цепочка версий: назад (кто был superseded этой версией) + вперёд по superseded_by.</summary>
    private void OnFactHistory(object sender, RoutedEventArgs e)
    {
        if (_store == null || RowOf(sender) is not { } row) return;
        try
        {
            var all = _store.ListItems(status: null, limit: 2000);
            var chain = new List<MemoryItem>();
            var seen = new HashSet<long>();
            // вперёд по цепочке замещений
            for (var cur = row.Item; cur != null && seen.Add(cur.Id);)
            {
                chain.Add(cur);
                cur = cur.SupersededById is { } nid ? all.FirstOrDefault(i => i.Id == nid) : null;
            }
            // назад: версии, которые заместила текущая цепочка
            var anchor = chain[0];
            for (var cur = all.FirstOrDefault(i => i.SupersededById == anchor.Id); cur != null && seen.Add(cur.Id);)
            {
                chain.Insert(0, cur);
                anchor = cur;
            }

            _history.Clear();
            foreach (var it in chain)
                _history.Add(new FactRow(it, L($"L_MC_Type_{it.Type}"), ScopeBadge(it), MetaText(it), TagText(it)));
            var title = row.Item.Content.Length > 60 ? row.Item.Content[..60] + "…" : row.Item.Content;
            HistoryTitle.Text = Lf("L_MC_HistoryTitle", title);
            // §6.1 (аудит 2026-10-15): пустая история — не «тихое скрытие», а
            // видимое «Изменений пока нет»: одна версия = правок не было.
            var single = _history.Count <= 1;
            HistoryEmpty.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
            HistoryList.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
            HistoryPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { Error($"History for item #{row.Item.Id} failed", ex); }
    }

    private void OnHistoryClose(object sender, RoutedEventArgs e) => HistoryPanel.Visibility = Visibility.Collapsed;

    // -------------------------------------------- Действия над фактом ---------
    // Спека «MEMORY ITEM ACTIONS»: view source / edit / pin(★ в строке) /
    // invalidate / delete / move scope / change project / add tag.
    // Меню строится в коде — пункты «встреча текущего факта» и списки проектов динамические.

    /// <summary>Клик по кнопке «⋯» в строке: открыть то же контекстное меню факта.</summary>
    private void OnFactActions(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || RowOf(sender) is not { } row) return;
        var menu = BuildFactMenu(row);
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    /// <summary>Ленивая сборка меню на открытие (правый клик по строке факта).</summary>
    private void OnFactMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Grid grid || grid.DataContext is not FactRow row || _store == null) return;
        // пока идёт inline-редактирование — не перетираем штатное меню TextBox
        if (!row.IsEditing)
            grid.ContextMenu = BuildFactMenu(row);
    }

    private static MenuItem MItem(string header, RoutedEventHandler onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += onClick;
        return mi;
    }

    private ContextMenu BuildFactMenu(FactRow row)
    {
        var it = row.Item;
        var menu = new ContextMenu();

        menu.Items.Add(MItem(L("L_MCA_ViewSource"), (_, _) => DoViewSource(row)));
        menu.Items.Add(MItem(L("L_MCA_Edit"), (_, _) => DoEditFact(row)));
        if (it.Status == MemoryItemStatus.Pending)
            menu.Items.Add(MItem(L("L_MCA_Activate"), (_, _) => DoReactivate(row)));
        if (it.Status == MemoryItemStatus.Invalidated)
            menu.Items.Add(MItem(L("L_MCA_Reactivate"), (_, _) => DoReactivate(row)));
        else
            menu.Items.Add(MItem(L("L_MCA_Invalidate"), (_, _) => DoInvalidate(row)));
        menu.Items.Add(MItem(L("L_MCA_Delete"), (_, _) => DoDelete(row)));
        menu.Items.Add(new Separator());

        // Move scope: Global / Meeting(встреча-источник текущего факта) / Project ▸
        var move = new MenuItem { Header = L("L_MCA_MoveScope") };
        move.Items.Add(MItem(L("L_MCA_ScopeGlobal"), (_, _) => SetScope(row, MemoryScopeType.Global, null)));
        var meetingId = it.ScopeType == MemoryScopeType.Meeting ? it.ScopeId : it.SourceMeetingId;
        // §6.1: недоступная область не молча-серая — клик объясняет причину.
        move.Items.Add(MItem(L("L_MCA_ScopeMeeting"), meetingId != null
            ? (_, _) => SetScope(row, MemoryScopeType.Meeting, meetingId)
            : (_, _) => ShowInfo(L("L_MC_NoLinkedMeeting"))));
        var projSub = new MenuItem { Header = L("L_MCA_ScopeProject") };
        foreach (var p in _store?.ListProjects(includeArchived: true) ?? new List<MemoryProject>())
            projSub.Items.Add(MItem(p.Name, (_, _) => SetScope(row, MemoryScopeType.Project, p.Id)));
        if (projSub.Items.Count == 0)
            projSub.Items.Add(MItem(L("L_MC_NoProjects"), (_, _) => ShowInfo(L("L_MC_NoProjects"))));
        move.Items.Add(projSub);
        // §6.1: scope Person был недостижим из меню — список людей, как проектов.
        var personSub = new MenuItem { Header = L("L_MCA_ScopePerson") };
        foreach (var pe in _store?.ListPeople() ?? new List<MemoryPerson>())
            personSub.Items.Add(MItem(pe.Name, (_, _) => SetScope(row, MemoryScopeType.Person, pe.Id)));
        if (personSub.Items.Count == 0)
            personSub.Items.Add(MItem(L("L_MC_NoPeople"), (_, _) => ShowInfo(L("L_MC_NoPeople"))));
        move.Items.Add(personSub);
        menu.Items.Add(move);
        var change = new MenuItem { Header = L("L_MCA_ChangeProject") };
        foreach (var p in _store?.ListProjects(includeArchived: true) ?? new List<MemoryProject>())
            change.Items.Add(MItem(p.Name, (_, _) => SetScope(row, MemoryScopeType.Project, p.Id)));
        change.IsEnabled = change.Items.Count > 0;
        menu.Items.Add(change);

        menu.Items.Add(new Separator());
        menu.Items.Add(MItem(L("L_MCA_AddTag"), (_, _) => DoAddTag(row)));
        return menu;
    }

    /// <summary>Источник факта: сегмент транскрипта и его временной диапазон, иначе встреча.</summary>
    private void DoViewSource(FactRow row)
    {
        if (_store == null) return;
        try
        {
            var it = row.Item;
            var meeting = it.SourceMeetingId is { } mid ? _store.GetMeeting(mid) : null;
            MemorySegment? seg = null;
            var segIndex = -1;   // позиция реплики в журнале для «перейти к реплике»
            if (meeting != null && it.SourceSegmentId is { } sid)
            {
                var segs = _store.GetSegments(meeting.Id);
                for (var i = 0; i < segs.Count; i++)
                    if (segs[i].Id == sid || segs[i].Seq == sid) { seg = segs[i]; segIndex = i; break; }
            }

            // §6.2 provenance: источник документа (SourceDocId/SourceDocName/SourceAnchor —
            // стабильные id, живут после рестарта; путь к копии берём из attachments.db).
            StoredDocument? doc = null;
            if (!string.IsNullOrEmpty(it.SourceDocumentId) && App.Current?.Attachments != null)
            {
                try { doc = App.Current.Attachments.Get(it.SourceDocumentId); }
                catch { /* документ стёрт/база недоступна — показываем что есть */ }
            }
            // SourceDocName без DocId — legacy: показываем имя, но НЕ ищем
            // «похожий» файл (имя ≠ путь, подмена источника недопустима).

            var isManual = it.SourceMeetingId == null
                && string.IsNullOrEmpty(it.SourceDocumentId)
                && it.SourceSegmentId == null
                && (it.Source == "user" || it.Source == "user-edited" || it.Source == "imported");

            if (meeting == null && seg == null && doc == null && !isManual)
            {
                // Неполный legacy-источник: честно показываем, что сохранено,
                // вместо молчания или выдуманного имени файла/спикера.
                if (string.IsNullOrEmpty(it.SourceDocName) && string.IsNullOrEmpty(it.SourceAnchor)
                    && it.SourceStartMs == null && string.IsNullOrEmpty(it.Speaker))
                {
                    ShowInfo(L("L_MCA_NoSource"));
                    return;
                }
            }

            var started = meeting?.StartedAtUtc.ToLocalTime()
                .ToString("d MMM yyyy HH:mm", CultureInfo.CurrentUICulture)
                ?? (doc != null ? doc.DisplayName : $"#{it.SourceMeetingId}");
            var header = Lf("L_MCA_SourceFmt", started, seg?.Seq.ToString() ?? "—");

            var body = new StringBuilder();
            // Шапка provenance: как создан + когда. Origin отдельно от текущего scope.
            body.AppendLine(Lf("L_MCA_OriginFmt",
                string.IsNullOrEmpty(it.Source) ? "?" : it.Source,
                it.CreatedAtUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentUICulture)));
            if (isManual)
                body.AppendLine(L("L_MCA_ManualSource"));

            if (doc != null || !string.IsNullOrEmpty(it.SourceDocName))
            {
                var name = doc?.DisplayName ?? it.SourceDocName;
                body.AppendLine($"📄 {name}");
                // SourceAnchor факта из документа — DocAnchor.JSON (локатор +
                // диапазон символов + дословная цитата). Legacy «часть i/N» не
                // парсится — показываем как есть.
                if (AttachmentFactExtractor.DocAnchor.TryParse(it.SourceAnchor, out var parts))
                {
                    var loc = AttachmentFactExtractor.DocAnchor.Describe(parts);
                    if (!string.IsNullOrEmpty(loc)) body.AppendLine($"📍 {loc}");
                    if (!string.IsNullOrEmpty(parts.Quote)) body.AppendLine($"❝ {parts.Quote} ❞");
                }
                else if (!string.IsNullOrEmpty(it.SourceAnchor))
                    body.AppendLine($"· {it.SourceAnchor}");
                if (doc?.Deleted == true)
                    body.AppendLine(L("L_MCA_DocDeleted"));
            }
            if (seg != null)
            {
                if (seg.StartMs != null || seg.EndMs != null)
                    body.AppendLine($"[{FmtMs(seg.StartMs)} → {FmtMs(seg.EndMs)}]");
                if (!string.IsNullOrEmpty(seg.Speaker))
                    body.AppendLine($"🗣 {seg.Speaker}");
                body.AppendLine(seg.Text);
            }
            else
            {
                if (it.SourceStartMs != null || it.SourceEndMs != null)
                    body.AppendLine($"[{FmtMs(it.SourceStartMs)} → {FmtMs(it.SourceEndMs)}] {it.Speaker ?? ""}".Trim());
                if (meeting != null)
                    body.AppendLine(meeting.Summary.Length > 0 ? meeting.Summary : meeting.Title ?? "");
            }
            // Кнопки реальной навигации: файл документа и реплика во встрече.
            var actions = new List<(string Label, Action OnClick)>();
            if (doc != null && App.Current?.Attachments != null)
            {
                string path;
                try { path = App.Current.Attachments.GetSourcePath(doc); }
                catch { path = ""; }
                if (path.Length > 0 && File.Exists(path))
                    actions.Add((L("L_MCA_SrcOpenDoc"), () => OpenExternalFile(path)));
            }
            if (meeting != null && seg != null && segIndex >= 0 && meeting.JournalPath.Length > 0)
                actions.Add((L("L_MCA_SrcJumpMeeting"), () => JumpToMeetingUtterance(meeting, seg, segIndex)));
            ShowTextDialog(header, body.ToString().Trim(), actions.ToArray());
        }
        catch (Exception ex) { ShowError($"ViewSource item #{row.Item.Id} failed", ex); }
    }

    /// <summary>Открыть файл документа системным приложением (ассоциация ОС).</summary>
    private static void OpenExternalFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Current?.Logger?.Warn($"OpenExternalFile failed for {path}: {ex.Message}", "UI");
        }
    }

    /// <summary>Открыть историю встреч и выделить реплику факта (журнал
    /// meetings/&lt;id&gt;.jsonl, строка = позиция сегмента в mem_segment).</summary>
    private static void JumpToMeetingUtterance(MemoryMeeting meeting, MemorySegment seg, int lineIndex)
    {
        var hit = new MeetingSearchHit
        {
            MeetingId = Path.GetFileNameWithoutExtension(meeting.JournalPath),
            MeetingStartedAt = meeting.StartedAtUtc.ToLocalTime(),
            LineIndex = lineIndex,
            Ts = seg.StartMs is { } ms
                ? TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : "",
            Role = seg.Role,
            Speaker = seg.Speaker,
            LineText = seg.Text,
            Snippet = seg.Text.Length > 160 ? seg.Text[..160] + "…" : seg.Text,
        };
        App.Current?.ShowMeetingHistory(hit);
    }

    private static string FmtMs(long? ms)
        => ms == null ? "—" : TimeSpan.FromMilliseconds(ms.Value).ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>Правка текста + важности в диалоге; правка помечается user-edited, provenance сохраняется.</summary>
    private void DoEditFact(FactRow row)
    {
        if (_store == null) return;
        var edited = PromptEditFact(row.Item);
        if (edited == null) return;
        var (content, importance) = edited.Value;
        try
        {
            row.Item.Content = content;
            row.Item.Importance = importance;
            // §6.2: правка НЕ трогает Source — origin (extraction/attachment/user)
            // это provenance, её стирание ломает кнопку «источник». Last-edit —
            // это UpdatedAtUtc (бампится в сторе).
            _store.UpdateItem(row.Item);       // UpdatedAtUtc бампится в сторе
            RefreshFacts();
            RefreshStatusBar();
        }
        catch (Exception ex) { ShowError($"Edit item #{row.Item.Id} failed", ex); }
    }

    private void DoInvalidate(FactRow row)
    {
        if (_store == null) return;
        var preview = row.Item.Content.Length > 80 ? row.Item.Content[..80] + "…" : row.Item.Content;
        if (AppMessageBox.Show(this, Lf("L_MCA_ConfirmInvalidateFmt", preview), L("L_MC_Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            _store.SetStatus(row.Item.Id, MemoryItemStatus.Invalidated);
            _log?.Info($"Item #{row.Item.Id} invalidated from Memory Center", LogCategory);
            RefreshFacts();
            RefreshStatusBar();
        }
        catch (Exception ex) { ShowError($"Invalidate item #{row.Item.Id} failed", ex); }
    }

    private void DoReactivate(FactRow row)
    {
        if (_store == null) return;
        try
        {
            var it = row.Item;
            it.Status = MemoryItemStatus.Active;
            it.ValidUntilUtc = null;            // снова актуален — верхняя граница validity снимается
            it.SupersededById = null;           // факт вне цепочки замещений
            it.LastConfirmedAtUtc = DateTime.UtcNow;
            _store.UpdateItem(it);
            _log?.Info($"Item #{it.Id} reactivated from Memory Center", LogCategory);
            RefreshFacts();
            RefreshStatusBar();
        }
        catch (Exception ex) { ShowError($"Reactivate item #{row.Item.Id} failed", ex); }
    }

    private void DoDelete(FactRow row)
    {
        if (_store == null) return;
        var preview = row.Item.Content.Length > 140 ? row.Item.Content[..140] + "…" : row.Item.Content;
        if (AppMessageBox.Show(this, Lf("L_MCA_ConfirmDeleteFmt", preview), L("L_MC_Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            _store.HardDeleteItem(row.Item.Id);
            _log?.Info($"Item #{row.Item.Id} hard-deleted from Memory Center", LogCategory);
            RefreshFacts();
            RefreshStatusBar();
        }
        catch (Exception ex) { ShowError($"Delete item #{row.Item.Id} failed", ex); }
    }

    private void SetScope(FactRow row, MemoryScopeType type, long? scopeId)
    {
        if (_store == null) return;
        try
        {
            row.Item.ScopeType = type;
            row.Item.ScopeId = scopeId;
            // §6.2: Source — provenance (как создан), не last-edit — не перетираем.
            _store.UpdateItem(row.Item);
            _log?.Info($"Item #{row.Item.Id} scope → {type}:{scopeId}", LogCategory);
            RefreshFacts();
            RefreshStatusBar();
        }
        catch (Exception ex) { ShowError($"Move scope item #{row.Item.Id} failed", ex); }
    }

    private void DoAddTag(FactRow row)
    {
        if (_store == null) return;
        var tag = PromptTextInput(L("L_MCA_TagPrompt"), "");
        if (string.IsNullOrWhiteSpace(tag)) return;
        try
        {
            _store.AddTag(row.Item.Id, tag.Trim().TrimStart('#'));
            RefreshFacts();
        }
        catch (Exception ex) { ShowError($"AddTag item #{row.Item.Id} failed", ex); }
    }

    // ------------------------------------------------ Экспорт / импорт --------

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("L_MX_FilterJson"),
            FileName = $"memory-export-{DateTime.Now:yyyyMMdd-HHmm}",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var n = MemoryTransfer.Export(_store, dlg.FileName);
            _log?.Info($"Memory export: {n} items → {dlg.FileName}", LogCategory);
            ShowInfo(Lf("L_MX_ExportDoneFmt", n, dlg.FileName));
        }
        catch (Exception ex) { ShowError($"Export to '{dlg.FileName}' failed", ex); }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L("L_MX_FilterImport") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var (imported, skipped) = MemoryTransfer.Import(_store, dlg.FileName);
            _log?.Info($"Memory import: {imported} items from {dlg.FileName}, {skipped} skipped", LogCategory);
            ShowInfo(Lf("L_MX_ImportDoneFmt", imported, skipped));
            RefreshCurrentTab();
        }
        catch (InvalidDataException)
        {
            AppMessageBox.Show(this, L("L_MX_BadFile"), L("L_MC_Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError($"Import from '{dlg.FileName}' failed", ex); }
    }

    private void ShowError(string msg, Exception ex)
    {
        Warn($"{msg}: {ex}");
        AppMessageBox.Show(this, Lf("L_MX_ErrorFmt", ex.Message), L("L_MC_Title"),
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ------------------------------------------------ Диалоги (код-билд, как OverlayWindow.PromptTextInput) --

    private Window NewDialog(string title, double width, double height) => new()
    {
        Title = title,
        Width = width, Height = height,
        Owner = this,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.CanResizeWithGrip,
        Background = TryFindResource("BgBrush") as Brush,
        Foreground = TryFindResource("TextBrush") as Brush,
        FontFamily = FontFamily,
        FontSize = FontSize,
    };

    /// <summary>Модальный просмотр текста (источник факта): заголовок + прокручиваемый
    /// текст + кнопки действий (слева) + Close. Кнопки — реальная навигация:
    /// открыть файл документа / перейти к реплике во встрече.</summary>
    private void ShowTextDialog(string title, string text,
        params (string Label, Action OnClick)[] actions)
    {
        var dlg = NewDialog(title, 560, 340);
        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tb = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 10, 0, 10),
            FontSize = 15,
            Padding = new Thickness(14, 12, 14, 12),
            Background = TryFindResource("BgDarkerBrush") as Brush,
        };
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, onClick) in actions)
        {
            var btn = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), MinHeight = 26 };
            btn.Click += (_, _) => { dlg.DialogResult = true; onClick(); };
            left.Children.Add(btn);
        }
        var close = new Button
        {
            Content = L("L_MC_Close"), IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => dlg.DialogResult = true;
        footer.Children.Add(left);
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(tb, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(tb);
        grid.Children.Add(footer);
        dlg.Content = grid;
        dlg.ShowDialog();
    }

    /// <summary>Минимальный ввод строки (тег): текст + TextBox + OK/Cancel (Enter/Esc).</summary>
    private string? PromptTextInput(string prompt, string initial)
    {
        var dlg = NewDialog(L("L_MC_Title"), 360, 150);
        dlg.ResizeMode = ResizeMode.NoResize;
        dlg.SizeToContent = SizeToContent.Height; // фиксированная высота съедала кнопки (No58)
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Text = initial, Style = TryFindResource("InputText") as Style, Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(tb);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = L("L_Msg_Ok"), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var cancel = new Button { Content = L("L_MC_Btn_Cancel"), IsCancel = true };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        sp.Children.Add(row);
        dlg.Content = sp;
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; dlg.DialogResult = true; };
        dlg.ShowDialog();
        return result;
    }

    /// <summary>Диалог правки факта: текст (multiline) + важность (slider 0–100%). null → отмена.</summary>
    private (string Content, double Importance)? PromptEditFact(MemoryItem it)
    {
        var dlg = NewDialog(L("L_MCA_Edit"), 520, 300);
        var sp = new StackPanel { Margin = new Thickness(14) };
        var textBox = new TextBox
        {
            Text = it.Content,
            Style = TryFindResource("MultilineInputText") as Style,
            MinHeight = 110,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var impLabel = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };
        var impSlider = new Slider
        {
            Minimum = 0, Maximum = 1, Value = it.Importance,
            TickFrequency = 0.05, IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 0, 0, 12),
        };
        void UpdateLabel() => impLabel.Text = Lf("L_MC_ImportanceFmt", impSlider.Value);
        impSlider.ValueChanged += (_, _) => UpdateLabel();
        UpdateLabel();

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = L("L_MC_Btn_Save"), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var cancel = new Button { Content = L("L_MC_Btn_Cancel"), IsCancel = true };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        sp.Children.Add(textBox);
        sp.Children.Add(impLabel);
        sp.Children.Add(impSlider);
        sp.Children.Add(row);
        dlg.Content = sp;

        (string, double)? result = null;
        ok.Click += (_, _) =>
        {
            var text = textBox.Text.Trim();
            if (text.Length == 0) return;          // пустой факт не сохраняем — остаёмся в диалоге
            result = (text, Math.Round(impSlider.Value, 2));
            dlg.DialogResult = true;
        };
        dlg.ShowDialog();
        return result;
    }

    /// <summary>Диалог создания факта: контент + тип + скоуп (Global/проекты) + важность + pin. null → отмена.</summary>
    private MemoryItem? PromptNewFact()
    {
        var dlg = NewDialog(L("L_MC_NewFact"), 540, 430);
        var sp = new StackPanel { Margin = new Thickness(14) };
        var dimBrush = TryFindResource("TextDimBrush") as Brush;
        TextBlock DimLabel(string key) => new()
        {
            Text = L(key), Foreground = dimBrush, FontSize = 13, FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 2),
        };

        var contentBox = new TextBox
        {
            Style = TryFindResource("MultilineInputText") as Style,
            MinHeight = 110,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var typeBox = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var t in new[] { MemoryItemType.Fact, MemoryItemType.Decision, MemoryItemType.ActionItem, MemoryItemType.Preference })
            typeBox.Items.Add(new ComboBoxItem { Content = L($"L_MC_Type_{t}"), Tag = t });
        typeBox.SelectedIndex = 0;

        var scopeBox = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        scopeBox.Items.Add(new ComboBoxItem { Content = L("L_MC_Scope_Global") });   // Tag=null → Global
        foreach (var p in _store?.ListProjects() ?? new List<MemoryProject>())
            scopeBox.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        scopeBox.SelectedIndex = 0;

        var impLabel = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };
        var impSlider = new Slider
        {
            Minimum = 0, Maximum = 1, Value = 0.5,
            TickFrequency = 0.05, IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 0, 0, 8),
        };
        void UpdateLabel() => impLabel.Text = Lf("L_MC_ImportanceFmt", impSlider.Value);
        impSlider.ValueChanged += (_, _) => UpdateLabel();
        UpdateLabel();

        var pinnedCheck = new CheckBox
        {
            Content = L("L_MC_Field_Pinned"),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = L("L_MC_Btn_Save"), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var cancel = new Button { Content = L("L_MC_Btn_Cancel"), IsCancel = true };
        row.Children.Add(ok);
        row.Children.Add(cancel);

        sp.Children.Add(DimLabel("L_MC_Field_Content"));
        sp.Children.Add(contentBox);
        sp.Children.Add(DimLabel("L_MC_Filter_Type"));
        sp.Children.Add(typeBox);
        sp.Children.Add(DimLabel("L_MC_Filter_Scope"));
        sp.Children.Add(scopeBox);
        sp.Children.Add(impLabel);
        sp.Children.Add(impSlider);
        sp.Children.Add(pinnedCheck);
        sp.Children.Add(row);
        dlg.Content = sp;

        MemoryItem? result = null;
        ok.Click += (_, _) =>
        {
            var text = contentBox.Text.Trim();
            if (text.Length == 0) return;          // пустой факт не сохраняем — остаёмся в диалоге
            var scopeId = scopeBox.SelectedItem is ComboBoxItem { Tag: long pid } ? pid : (long?)null;
            result = new MemoryItem
            {
                Content = text,
                Type = typeBox.SelectedItem is ComboBoxItem { Tag: MemoryItemType t } ? t : MemoryItemType.Fact,
                ScopeType = scopeId.HasValue ? MemoryScopeType.Project : MemoryScopeType.Global,
                ScopeId = scopeId,
                Importance = Math.Round(impSlider.Value, 2),
                Confidence = 1.0,
                Pinned = pinnedCheck.IsChecked == true,
                Source = "user",
                Status = MemoryItemStatus.Active,
            };
            dlg.DialogResult = true;
        };
        dlg.ShowDialog();
        return result;
    }

    // ------------------------------------------------ Встречи -----------------

    private void RefreshMeetings()
    {
        if (_store == null) return;
        try
        {
            var items = _store.ListItems(status: null, limit: 2000);
            _meetings.Clear();
            foreach (var m in _store.ListMeetings(500))
            {
                var count = items.Count(i => i.SourceMeetingId == m.Id);
                var dur = m.EndedAtUtc is { } end && end > m.StartedAtUtc
                    ? $"{(end - m.StartedAtUtc).TotalMinutes:F0} min"
                    : "—";
                var meta = Lf("L_MC_Meeting_MetaFmt",
                    m.StartedAtUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentUICulture),
                    dur, m.MemoryMode, count);
                _meetings.Add(new MeetingRow(m, meta));
            }
            MeetingsEmpty.Visibility = _meetings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { Error("RefreshMeetings failed", ex); }
    }

    private async void OnMeetingOpen(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MeetingsList.SelectedItem is not MeetingRow row) return;
        if (string.IsNullOrEmpty(row.Meeting.JournalPath))
        {
            ShowInfo(L("L_MC_JournalMissing"));
            return;
        }
        if (OpenJournal == null)
        {
            Warn("OpenJournal callback not wired (App.ShowMeetingHistoryFile)");
            ShowInfo(L("L_MC_OpenJournalStub"));
            return;
        }
        try { await OpenJournal(row.Meeting.JournalPath); }
        catch (Exception ex) { Error($"OpenJournal '{row.Meeting.JournalPath}' failed", ex); }
    }

    // ------------------------------------------------ Проекты -----------------

    private void RefreshProjects()
    {
        if (_store == null) return;
        try
        {
            var includeArchived = ShowArchivedCheck.IsChecked == true;
            _projects.Clear();
            foreach (var p in _store.ListProjects(includeArchived))
                _projects.Add(new ProjectRow(p, p.Id == _activeProjectId));
            UpdateProjectPanelState();      // refresh вкладки мог снять выделение (Clear → событие)
        }
        catch (Exception ex) { Error("RefreshProjects failed", ex); }
    }

    private void OnProjectFilter(object sender, RoutedEventArgs e) => RefreshProjects();

    private void OnProjectSelect(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectsList.SelectedItem is ProjectRow row)
        {
            _creatingProject = false;                                  // выбор существующего проекта завершает режим «создание»
            _editingProject = row.Project;
            ProjectNameBox.Text = row.Project.Name;
            ProjectDescBox.Text = row.Project.Description;
            ProjectArchiveBtn.Content = L(row.Project.Archived ? "L_MC_Unarchive" : "L_MC_Archive");
            ProjectActiveBtn.IsEnabled = !row.Project.Archived && !row.IsActive;
            ProjectDeleteBtn.IsEnabled = true;
        }
        UpdateProjectPanelState();          // и на выборе, и на снятии выделения
    }

    /// <summary>Сброс редактора под «новый проект». Только поля UI — записи в базе нет (спека B23).</summary>
    private void ResetProjectEditor()
    {
        _editingProject = new MemoryProject();
        ProjectsList.SelectedItem = null;
        ProjectNameBox.Text = "";
        ProjectDescBox.Text = "";
        ProjectArchiveBtn.Content = L("L_MC_Archive");
        ProjectActiveBtn.IsEnabled = false;
        ProjectDeleteBtn.IsEnabled = false;
    }

    /// <summary>
    /// Три состояния правой панели (волна 05): пустая подсказка ↔ редактор.
    /// Редактор виден при «создании» (даже при SelectedItem==null) или при выбранном проекте.
    /// </summary>
    private void UpdateProjectPanelState()
    {
        if (ProjectEditor is null || ProjectEmptyPanel is null || ProjectsList is null) return;
        var showEditor = _creatingProject || ProjectsList.SelectedItem != null;
        ProjectEditor.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        ProjectEmptyPanel.Visibility = showEditor ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnProjectNew(object sender, RoutedEventArgs e)
    {
        _creatingProject = true;                // до SelectedItem=null: событие SelectionChanged не скроет редактор
        ResetProjectEditor();
        UpdateProjectPanelState();
        ProjectNameBox.Focus();
    }

    private void OnProjectSave(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        var name = ProjectNameBox.Text.Trim();
        if (name.Length == 0) return;
        try
        {
            _editingProject.Name = name;
            _editingProject.Description = ProjectDescBox.Text.Trim();
            _store.UpsertProject(_editingProject);
            _creatingProject = false;           // запись появилась только здесь — по команде сохранения
            RefreshProjects();
            UpdateProjectPanelState();
        }
        catch (Exception ex) { Error("UpsertProject failed", ex); }
    }

    private void OnProjectSetActive(object sender, RoutedEventArgs e)
    {
        if (_store == null || ProjectsList.SelectedItem is not ProjectRow row) return;
        try
        {
            _activeProjectId = row.Project.Id;
            if (Application.Current is App app)
            {
                app.Config.Memory.ActiveProjectId = row.Project.Id;
                app.SaveConfigSafe();
            }
            foreach (var p in _projects) p.IsActive = p.Project.Id == _activeProjectId;
            ProjectActiveBtn.IsEnabled = false;
        }
        catch (Exception ex) { Error("SetActiveProject failed", ex); }
    }

    private void OnProjectArchive(object sender, RoutedEventArgs e)
    {
        if (_store == null || ProjectsList.SelectedItem is not ProjectRow row) return;
        try
        {
            row.Project.Archived = !row.Project.Archived;
            _store.UpsertProject(row.Project);
            RefreshProjects();
            UpdateProjectPanelState();
        }
        catch (Exception ex) { Error($"Archive project #{row.Project.Id} failed", ex); }
    }

    private void OnProjectDelete(object sender, RoutedEventArgs e)
    {
        if (_store == null || ProjectsList.SelectedItem is not ProjectRow row) return;
        if (AppMessageBox.Show(this, Lf("L_MC_ConfirmDeleteProject", row.Project.Name), L("L_MC_Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            _store.DeleteProject(row.Project.Id);
            if (_activeProjectId == row.Project.Id)
            {
                _activeProjectId = null;
                if (Application.Current is App app)
                {
                    app.Config.Memory.ActiveProjectId = null;
                    app.SaveConfigSafe();
                }
            }
            ResetProjectEditor();           // после удаления — пустое состояние, не режим «создание»
            _creatingProject = false;
            RefreshProjects();
            UpdateProjectPanelState();
        }
        catch (Exception ex) { Error($"Delete project #{row.Project.Id} failed", ex); }
    }

    // ------------------------------------------------ Люди --------------------

    private void RefreshPeople()
    {
        if (_store == null) return;
        try
        {
            _people.Clear();
            foreach (var p in _store.ListPeople())
                _people.Add(p);
        }
        catch (Exception ex) { Error("RefreshPeople failed", ex); }
    }

    private void OnPersonSelect(object sender, SelectionChangedEventArgs e)
    {
        if (PeopleList.SelectedItem is not MemoryPerson p) return;
        _editingPerson = p;
        PersonNameBox.Text = p.Name;
        PersonNotesBox.Text = p.Notes;
        PersonDeleteBtn.IsEnabled = true;
    }

    private void OnPersonNew(object sender, RoutedEventArgs e)
    {
        _editingPerson = new MemoryPerson();
        PeopleList.SelectedItem = null;
        PersonNameBox.Text = "";
        PersonNotesBox.Text = "";
        PersonDeleteBtn.IsEnabled = false;
        PersonNameBox.Focus();
    }

    private void OnPersonSave(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        var name = PersonNameBox.Text.Trim();
        if (name.Length == 0) return;
        try
        {
            _editingPerson.Name = name;
            _editingPerson.Notes = PersonNotesBox.Text.Trim();
            _store.UpsertPerson(_editingPerson);
            RefreshPeople();
        }
        catch (Exception ex) { Error("UpsertPerson failed", ex); }
    }

    private void OnPersonDelete(object sender, RoutedEventArgs e)
    {
        if (_store == null || PeopleList.SelectedItem is not MemoryPerson p) return;
        if (AppMessageBox.Show(this, Lf("L_MC_ConfirmDeletePerson", p.Name), L("L_MC_Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            _store.DeletePerson(p.Id);
            OnPersonNew(sender, e);
            RefreshPeople();
        }
        catch (Exception ex) { Error($"Delete person #{p.Id} failed", ex); }
    }

    // ------------------------------------------------ Fact-job карточка -------

    /// <summary>App.FactJobChanged прилетает с фон-потока → маршалим на UI.</summary>
    private void OnFactJobChanged(FactJob job)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _factJob = job;
            RefreshFactJobCard();
        }));
    }

    /// <summary>
    /// Отрисовать карточку последнего fact-job'а на вкладке «Задачи»: живой %
    /// (RequestsDone/RequestsPlanned), имя файла, стоп-кнопка, текст состояния,
    /// причина ошибки. Пусто — карточка скрыта; Done/NoFacts/PendingReview скрывают её.
    /// </summary>
    private void RefreshFactJobCard()
    {
        var job = _factJob;
        if (job == null)
        {
            try { job = App.Current?.Attachments?.ListFactJobs().FirstOrDefault(); }
            catch { /* attachments.db недоступна — карточку просто прячем */ }
            _factJob = job;
        }

        if (job == null || job.Status is FactJobStatus.Done or FactJobStatus.NoFacts
                or FactJobStatus.PendingReview)
        {
            FactJobCard.Visibility = Visibility.Collapsed;
            return;
        }

        FactJobCard.Visibility = Visibility.Visible;

        // Имя файла — из attachments.db (FactJob хранит только DocumentId).
        var name = job.DocumentId;
        try
        {
            var doc = App.Current?.Attachments?.Get(job.DocumentId);
            if (!string.IsNullOrWhiteSpace(doc?.DisplayName)) name = doc!.DisplayName;
        }
        catch { /* best effort — остаётся id */ }
        FactJobTitle.Text = name;

        FactJobProgress.IsIndeterminate = job.Status == FactJobStatus.Running && job.RequestsPlanned <= 0;
        FactJobProgress.Value = job.RequestsPlanned > 0
            ? Math.Clamp((double)job.RequestsDone / job.RequestsPlanned, 0, 1)
            : 0;

        // Подпись статуса + доп.строка. Verify-mode → «На проверке: N» (не «сохранено»).
        FactJobStatusText.Text = job.Status switch
        {
            FactJobStatus.Queued => L("L_Att_FactsQueued"),
            FactJobStatus.Running when job.RequestsDone > 0 && job.RequestsPlanned > 0 =>
                // §6.1: процент + части + сохранённые факты («44% · обработано 4 из 9…»).
                Lf("L_MC_FactJob_RunningFullFmt",
                    job.RequestsDone * 100 / job.RequestsPlanned,
                    job.RequestsDone, job.RequestsPlanned, job.FactsSaved),
            // До первого ответа — «Обрабатываю часть 1 из N», не висим на «Запрос 0/N».
            FactJobStatus.Running when job.RequestsPlanned > 0 =>
                Lf("L_MC_FactJob_PartFmt", job.RequestsDone + 1, job.RequestsPlanned),
            FactJobStatus.Running => L("L_MC_FactJob_Starting"),
            FactJobStatus.Interrupted => L("L_Att_FactsInterrupted"),
            // §6.1: ошибка — какая часть не дошла + сколько фактов уже сохранено.
            FactJobStatus.Error => job.RequestsDone > 0
                ? Lf("L_MC_FactJob_ErrorFullFmt",
                    job.RequestsDone + 1, job.RequestsPlanned, job.FactsSaved, job.Error ?? "")
                : Lf("L_Att_FactsErrorFmt", job.Error ?? ""),
            FactJobStatus.Canceled => Lf("L_MC_FactJob_StoppedFmt",
                job.RequestsDone, job.RequestsPlanned, job.FactsSaved),
            _ => job.Status.ToString(),
        };

        // Verify-mode: промежуточные факты лежат «на проверке», а не «сохранено».
        if (job.Status == FactJobStatus.Running && job.FactsPending > 0)
            FactJobStatusText.Text += $" · {Lf("L_Att_FactsPendingReviewFmt", job.FactsPending)}";

        // Доп. строка ошибки — когда статус сам по себе её не показывает (причину держим видимой).
        var showError = !string.IsNullOrWhiteSpace(job.Error) && job.Status != FactJobStatus.Error;
        FactJobError.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
        FactJobError.Text = showError ? job.Error : "";

        // Остановить — для активных. Продолжить (Canceled) / Повторить (Error) — возобновляют
        // с RequestsDone: завершённые части повторно не отправляются, факты не дублируются.
        FactJobStopBtn.IsEnabled = job.Status is FactJobStatus.Queued
            or FactJobStatus.Running or FactJobStatus.Interrupted;
        FactJobStopBtn.Tag = job.Id;

        var canResume = job.Status is FactJobStatus.Canceled or FactJobStatus.Error;
        FactJobResumeBtn.Visibility = canResume ? Visibility.Visible : Visibility.Collapsed;
        if (canResume)
        {
            FactJobResumeBtn.Content = job.Status == FactJobStatus.Error
                ? L("L_MC_FactJob_Retry") : L("L_MC_FactJob_Resume");
            FactJobResumeBtn.IsEnabled = true;
            FactJobResumeBtn.Tag = job.Id;
        }
    }

    /// <summary>«Продолжить» (Canceled) / «Повторить» (Error): job → Queued, RequestsDone
    /// сохраняется — экстрактор доедет с неотправленной части (см. AttachmentFactExtractor).</summary>
    private void OnFactJobResume(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not long jobId) return;
        FactJobResumeBtn.IsEnabled = false;
        try
        {
            var app = App.Current;
            if (app == null) return;
            var job = app.Attachments?.ListFactJobs().FirstOrDefault(j => j.Id == jobId);
            if (job == null) return;
            var resumed = job with { Status = FactJobStatus.Queued, Error = null };
            app.Attachments?.UpdateFactJob(resumed);
            _factJob = resumed;   // карточка сразу показывает «В очереди»
            app.KickFactWorker();
            RefreshFactJobCard();
        }
        catch (Exception ex) { Error($"ResumeFactJob #{jobId} failed", ex); }
    }

    private async void OnFactJobStop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not long jobId) return;
        FactJobStopBtn.IsEnabled = false;
        try
        {
            var app = App.Current;
            if (app != null)
                await app.CancelFactJobAsync(jobId); // seam: App.CancelFactJobAsync (интегратор)
            RefreshFactJobCard();
        }
        catch (Exception ex) { Error($"CancelFactJob #{jobId} failed", ex); }
    }

    // ------------------------------------------------ Задачи (джобы) ----------

    private void RefreshJobs()
    {
        if (_store == null) return;
        try
        {
            _jobs.Clear();
            foreach (var j in _store.ListJobs(null, 200).OrderByDescending(j => j.Id))
            {
                var meta = $"#{j.Id} · {Lf("L_MC_Job_AttemptsFmt", j.Attempts)} · " +
                           j.CreatedAtUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentUICulture);
                if (j.RunAfterUtc is { } ra)
                    meta += $" · → {ra.ToLocalTime():HH:mm}";
                _jobs.Add(new JobRow(j, meta));
            }
            JobsEmpty.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { Error("RefreshJobs failed", ex); }
    }

    private void OnJobsRefresh(object sender, RoutedEventArgs e) { RefreshJobs(); RefreshStatusBar(); }

    private void OnJobRetry(object sender, RoutedEventArgs e)
    {
        if (_store == null || (sender as FrameworkElement)?.DataContext is not JobRow row) return;
        try
        {
            _store.RetryJob(row.Job.Id);
            _log?.Info($"Job #{row.Job.Id} ({row.Job.Kind}) requeued from Memory Center", LogCategory);
            RefreshJobs();
        }
        catch (Exception ex) { Error($"RetryJob #{row.Job.Id} failed", ex); }
    }

    private void OnJobsClearFailed(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        try
        {
            var n = _store.DeleteJobsByStatus("failed");
            _log?.Info($"Deleted {n} failed jobs from Memory Center", LogCategory);
            RefreshJobs();
            RefreshStatusBar();
        }
        catch (Exception ex) { Error("DeleteJobsByStatus failed", ex); }
    }

    // ------------------------------------------------ row VMs -----------------

    /// <summary>Строка факта: модель + предвычисленные локализованные подписи.</summary>
    public sealed class FactRow : INotifyPropertyChanged
    {
        public MemoryItem Item { get; }
        public string TypeName { get; }
        public string ScopeBadge { get; }
        public string MetaText { get; }
        public string Tags { get; }
        public bool HasTags => Tags.Length > 0;

        private bool _isEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set { _isEditing = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing))); }
        }

        private string _editText = "";
        public string EditText
        {
            get => _editText;
            set { _editText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditText))); }
        }

        public FactRow(MemoryItem item, string typeName, string scopeBadge, string meta, string tags = "")
        {
            Item = item;
            TypeName = typeName;
            ScopeBadge = scopeBadge;
            MetaText = meta;
            Tags = tags;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class MeetingRow
    {
        public MemoryMeeting Meeting { get; }
        public string MetaText { get; }
        public bool HasSummary => Meeting.Summary.Length > 0;
        public MeetingRow(MemoryMeeting m, string meta) { Meeting = m; MetaText = meta; }
    }

    public sealed class ProjectRow : INotifyPropertyChanged
    {
        public MemoryProject Project { get; }
        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); }
        }
        public ProjectRow(MemoryProject p, bool isActive) { Project = p; _isActive = isActive; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class JobRow
    {
        public MemoryJob Job { get; }
        public string MetaText { get; }
        public bool HasError => !string.IsNullOrEmpty(Job.Error);
        public bool CanRetry => Job.Status is "failed" or "running" or "pending";
        public JobRow(MemoryJob j, string meta) { Job = j; MetaText = meta; }
    }
}

/// <summary>Inverse of BooleanToVisibilityConverter (true → Collapsed).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>
/// Цвет чипа по типу memory item (theme brushes — смена темы подхватывается).
/// ConverterParameter: "bg" — приглушённый фон плашки, "fg" — текст (всегда белый на цвете), иначе сама кисть.
/// </summary>
public sealed class MemoryTypeBrushConverter : IValueConverter
{
    private static readonly Dictionary<MemoryItemType, string> Map = new()
    {
        [MemoryItemType.Decision] = "AccentBrush",
        [MemoryItemType.ActionItem] = "WarnBrush",
        [MemoryItemType.Fact] = "TextMutedBrush",
        [MemoryItemType.Preference] = "SuccessBrush",
        [MemoryItemType.Constraint] = "AccentSoftBrush",
        [MemoryItemType.Deadline] = "ErrorBrush",
        [MemoryItemType.OpenQuestion] = "AccentSoftBrush",
        [MemoryItemType.Risk] = "ErrorBrush",
        [MemoryItemType.Idea] = "BoltBrush",
        [MemoryItemType.Correction] = "AccentBrush",
        [MemoryItemType.PersonFact] = "AccentSoftBrush",
        [MemoryItemType.ProjectFact] = "AccentBrush",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is MemoryItemType t && Map.TryGetValue(t, out var k) ? k : "TextMutedBrush";
        var brush = Application.Current?.TryFindResource(key) as Brush
                    ?? System.Windows.Media.Brushes.Gray;
        var mode = parameter as string;
        if (mode == "fg")
        {
            // на пастельных warn/bolt-цветах читается тёмный текст, на остальных — белый
            return key is "WarnBrush" or "BoltBrush"
                ? Application.Current?.TryFindResource("BgBrush") as Brush ?? System.Windows.Media.Brushes.Black
                : System.Windows.Media.Brushes.White;
        }
        if (mode == "bg")
        {
            var clone = brush.Clone();
            clone.Opacity = 0.18;
            return clone;
        }
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Цвет чипа по статусу mem_job: pending/running/completed/failed.</summary>
public sealed class MemoryJobStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "pending" => "AccentSoftBrush",
            "running" => "WarnBrush",
            "completed" => "SuccessBrush",
            "failed" => "ErrorBrush",
            _ => "TextMutedBrush",
        };
        var brush = Application.Current?.TryFindResource(key) as Brush
                    ?? System.Windows.Media.Brushes.Gray;
        var mode = parameter as string;
        if (mode == "fg")
            return key == "WarnBrush"
                ? Application.Current?.TryFindResource("BgBrush") as Brush ?? System.Windows.Media.Brushes.Black
                : System.Windows.Media.Brushes.White;
        if (mode == "bg")
        {
            var clone = brush.Clone();
            clone.Opacity = 0.18;
            return clone;
        }
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
