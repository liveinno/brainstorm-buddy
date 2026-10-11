using System.Windows;
using System.Windows.Controls;
using BrainstormBuddy.Memory;
// ImplicitUsings+WinForms делают KeyEventArgs двусмысленным (CS0104) — фиксируем WPF-вариант.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace BrainstormBuddy;

/// <summary>
/// «Перед встречей» (спека PRE-CALL BRIEF): показывается ДО старта звонка.
/// Строится из Memory (не из всей истории транскриптов): актуальные факты
/// (recall + свежие Active items), открытые вопросы/дедлайны, editable-агентда.
/// Вызывающий (W1) читает <see cref="AgendaText"/>, <see cref="UseMemoryChecked"/>,
/// <see cref="WriteMemoryChecked"/> после DialogResult=true.
/// Esc/«Пропустить» — закрытие без старта памяти (DialogResult=false).
/// </summary>
public partial class PreCallBriefWindow : Window
{
    private const string LogCategory = "Memory";
    private const int FactLimit = 10;
    private const int OpenLimit = 10;
    // Пороги адаптивной раскладки (спека 05 §B01), в DIP по ActualWidth области.
    private const double FactRowWideThreshold = 440;   // бейдж слева / над текстом
    private const double OptionsWideThreshold = 540;   // опции памяти в одну строку / столбиком

    // Типы, которые не относятся к «фактам» секции (у них своя секция/роль).
    private static readonly HashSet<MemoryItemType> NonFactTypes =
        [MemoryItemType.OpenQuestion, MemoryItemType.Deadline, MemoryItemType.Idea];

    private IMemoryStore? _store;
    private IMemoryRecallService? _recall;
    private long? _projectId;
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public PreCallBriefWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Подключить сервисы и загрузить бриф. <paramref name="projectId"/> —
    /// выбранный проект встречи (null → только Global scope). Вызывать ДО Show/ShowDialog.
    /// </summary>
    public void Initialize(IMemoryStore store, IMemoryRecallService recall, long? projectId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recall = recall ?? throw new ArgumentNullException(nameof(recall));
        _projectId = projectId;
    }

    /// <summary>
    /// true, если окно закрыто кнопкой «Начать встречу». Немодальный Show() не даёт
    /// DialogResult — вызывающий различает «Начать»/«Пропустить»/Esc по этому флагу.
    /// </summary>
    public bool Started { get; private set; }

    /// <summary>Агентда, набранная пользователем (читать после «Начать встречу»).</summary>
    public string AgendaText => AgendaBox?.Text.Trim() ?? "";

    /// <summary>Пер-встречный тогл «читать память» (set — начальное значение из конфига).</summary>
    public bool UseMemoryChecked
    {
        get => UseMemoryCheck.IsChecked == true;
        set => UseMemoryCheck.IsChecked = value;
    }

    /// <summary>Пер-встречный тогл «запоминать встречу» (set — начальное значение из конфига).</summary>
    public bool WriteMemoryChecked
    {
        get => WriteMemoryCheck.IsChecked == true;
        set => WriteMemoryCheck.IsChecked = value;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_loaded) return;
        _loaded = true;
        await LoadBriefAsync();
    }

    // ---------------- загрузка брифа ----------------

    private async Task LoadBriefAsync()
    {
        if (_store is null || _recall is null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SubTitle.Text = T("L_PB_Loading");
        try
        {
            var project = _projectId is { } pid
                ? _store.ListProjects(includeArchived: true).FirstOrDefault(p => p.Id == pid)
                : null;
            SubTitle.Text = BuildSubtitle(project);

            var facts = await LoadFactsAsync(ct);
            FactsList.ItemsSource = facts.Select(it => ToRow(it, "AccentSoftBrush")).ToList();
            FactsEmpty.Visibility = facts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var open = LoadOpenItems();
            OpenList.ItemsSource = open.Select(it => ToRow(it, "WarnBrush")).ToList();
            OpenEmpty.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Current?.Logger?.Warn($"PreCallBrief load failed: {ex.Message}", LogCategory);
            SubTitle.Text = string.Format(T("L_PB_ErrorFmt"), ex.Message);
        }
    }

    /// <summary>
    /// «Актуальные факты»: top-N recall общим запросом (Global + активный проект).
    /// FTS не умеет пустой запрос — поэтому recall-добивка: пустой ответ recall
    /// добирается свежими Active items напрямую из store (importance/recency).
    /// </summary>
    private async Task<List<MemoryItem>> LoadFactsAsync(CancellationToken ct)
    {
        var result = new List<MemoryItem>(FactLimit);
        try
        {
            var q = new MemoryRecallQuery(
                Text: "",
                Scope: _projectId is null ? MemoryScopeType.Global : MemoryScopeType.Project,
                ScopeId: _projectId,
                MaxItems: FactLimit);
            foreach (var h in await _recall!.RecallAsync(q, ct))
                if (!NonFactTypes.Contains(h.Item.Type))
                    result.Add(h.Item);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* recall недоступен — ниже добор из store */ }

        if (result.Count < FactLimit)
        {
            var seen = new HashSet<long>(result.Select(i => i.Id));
            foreach (var it in ScopedActiveItems()
                         .Where(i => !NonFactTypes.Contains(i.Type))
                         .OrderByDescending(i => i.Pinned)
                         .ThenByDescending(i => i.Importance)
                         .ThenByDescending(i => i.UpdatedAtUtc))
            {
                if (seen.Add(it.Id)) result.Add(it);
                if (result.Count >= FactLimit) break;
            }
        }
        return result;
    }

    /// <summary>Открытые вопросы/дедлайны: Active OpenQuestion|Deadline по scope встречи.</summary>
    private List<MemoryItem> LoadOpenItems()
        => ScopedActiveItems()
            .Where(i => i.Type is MemoryItemType.OpenQuestion or MemoryItemType.Deadline)
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.Importance)
            .ThenByDescending(i => i.UpdatedAtUtc)
            .Take(OpenLimit)
            .ToList();

    /// <summary>Active items в scope встречи: Global + (если задан) активный проект.</summary>
    private IEnumerable<MemoryItem> ScopedActiveItems()
    {
        foreach (var it in _store!.ListItems(MemoryScopeType.Global, null,
                     MemoryItemStatus.Active))
            yield return it;
        if (_projectId is { } pid)
            foreach (var it in _store.ListItems(MemoryScopeType.Project, pid,
                         MemoryItemStatus.Active))
                yield return it;
    }

    private MemoryMeeting? LastMeeting()
    {
        try
        {
            return _store!.ListMeetings(50)
                .Where(m => _projectId is null || m.ProjectId == _projectId)
                .OrderByDescending(m => m.StartedAtUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private string BuildSubtitle(MemoryProject? project)
    {
        var last = LastMeeting();
        var lastText = last is null
            ? T("L_PB_NoMeetings")
            : string.Format(T("L_PB_LastMeetingFmt"), RelativeDays(last.StartedAtUtc));
        return project is null
            ? lastText
            : string.Format(T("L_PB_ProjectFmt"), project.Name) + " · " + lastText;
    }

    private string RelativeDays(DateTime utc)
    {
        var days = (int)Math.Max(0, (DateTime.UtcNow - utc).TotalDays);
        return days switch
        {
            0 => T("L_PB_Today"),
            1 => T("L_PB_Yesterday"),
            _ => string.Format(T("L_PB_DaysAgoFmt"), days),
        };
    }

    // ---------------- строки/кнопки ----------------

    private BriefRow ToRow(MemoryItem it, string badgeBrushKey) => new(this, it, badgeBrushKey);

    private string TypeLabel(MemoryItemType type) => type switch
    {
        MemoryItemType.Decision => T("L_AM_Type_Decision"),
        MemoryItemType.ActionItem => T("L_AM_Type_ActionItem"),
        MemoryItemType.Fact => T("L_AM_Type_Fact"),
        MemoryItemType.Preference => T("L_AM_Type_Preference"),
        MemoryItemType.Constraint => T("L_AM_Type_Constraint"),
        MemoryItemType.Deadline => T("L_AM_Type_Deadline"),
        MemoryItemType.OpenQuestion => T("L_AM_Type_OpenQuestion"),
        MemoryItemType.Risk => T("L_AM_Type_Risk"),
        MemoryItemType.Idea => T("L_AM_Type_Idea"),
        MemoryItemType.Correction => T("L_AM_Type_Correction"),
        MemoryItemType.PersonFact => T("L_AM_Type_PersonFact"),
        MemoryItemType.ProjectFact => T("L_AM_Type_ProjectFact"),
        _ => type.ToString(),
    };

    private string T(string key) => App.Current?.T(key) ?? key;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnSkip(sender, e);
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish(start: false);
    private void OnStart(object sender, RoutedEventArgs e) => Finish(start: true);

    /// <summary>
    /// «Начать встречу» → DialogResult=true, «Пропустить»/Esc → false.
    /// При немодальном Show() DialogResult недоступен — вызывающий смотрит Closed.
    /// </summary>
    private void Finish(bool start)
    {
        Started = start;
        try { DialogResult = start; }
        catch (InvalidOperationException) { /* показано немодально — без DialogResult */ }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnClosed(e);
    }

    /// <summary>Строка брифа: тип-чип + контент. BadgeForeground — кисть чипа секции.</summary>
    private sealed class BriefRow
    {
        public BriefRow(PreCallBriefWindow w, MemoryItem it, string badgeBrushKey)
        {
            TypeLabel = w.TypeLabel(it.Type);
            Content = it.Content;
            BadgeForeground = w.TryFindResource(badgeBrushKey) as Brush
                              ?? w.TryFindResource("TextBrush") as Brush
                              ?? Brushes.LightGray;
        }
        public string TypeLabel { get; }
        public string Content { get; }
        public Brush BadgeForeground { get; }
    }

    // ---------------- адаптивная раскладка (спека 05 §B01) ----------------

    /// <summary>
    /// Строка факта: от 440 DIP бейдж слева (Auto|*), текст справа через 10;
    /// уже — бейдж над текстом через 6 (колонка 1 сплющена в 0, строки Auto|Auto).
    /// Переключаем колонки/строки существующего Grid — строка не пересоздаётся.
    /// </summary>
    private void OnBriefRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid row || row.Children.Count < 2) return;
        bool wide = e.NewSize.Width >= FactRowWideThreshold;
        bool isWide = Grid.GetColumn(row.Children[1]) == 1;
        if (wide == isWide) return;
        var badge = (FrameworkElement)row.Children[0];
        var text = (FrameworkElement)row.Children[1];
        if (wide)
        {
            row.ColumnDefinitions[0].Width = GridLength.Auto;
            row.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(badge, 0);
            Grid.SetColumn(text, 1);
            Grid.SetRow(text, 0);
            badge.Margin = new Thickness(0, 0, 10, 0);
            text.Margin = new Thickness(0);
        }
        else
        {
            row.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            row.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumn(text, 0);
            Grid.SetRow(badge, 0);
            Grid.SetRow(text, 1);
            badge.Margin = new Thickness(0, 0, 0, 6);
            text.Margin = new Thickness(0);
        }
    }

    /// <summary>
    /// Опции памяти: от 540 DIP в одну строку (Auto|*); уже — столбиком через 8,
    /// у второго чекбокса без левого отступа (общий шаблон CheckBox 03A).
    /// </summary>
    private void OnOptionsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= OptionsWideThreshold;
        bool isWide = Grid.GetColumn(WriteMemoryCheck) == 1;
        if (wide == isWide) return;
        if (wide)
        {
            OptionsPanel.ColumnDefinitions[0].Width = GridLength.Auto;
            OptionsPanel.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(WriteMemoryCheck, 1);
            Grid.SetRow(WriteMemoryCheck, 0);
            UseMemoryCheck.Margin = new Thickness(0, 4, 0, 4);
            WriteMemoryCheck.Margin = new Thickness(16, 4, 0, 4);
        }
        else
        {
            OptionsPanel.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            OptionsPanel.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumn(WriteMemoryCheck, 0);
            Grid.SetRow(WriteMemoryCheck, 1);
            UseMemoryCheck.Margin = new Thickness(0, 4, 0, 4);
            WriteMemoryCheck.Margin = new Thickness(0, 8, 0, 4);
        }
    }
}
