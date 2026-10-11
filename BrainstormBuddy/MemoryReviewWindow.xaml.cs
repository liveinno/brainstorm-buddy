using System.Windows;
using System.Windows.Threading;
using BrainstormBuddy.Memory;
// ImplicitUsings+WinForms делают KeyEventArgs двусмысленным (CS0104) — фиксируем WPF-вариант.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace BrainstormBuddy;

/// <summary>
/// «Проверка памяти после встречи» (спека MEMORY REVIEW MODES → Review):
/// после extract-джоба показывает Pending-items встречи с чекбоксами (☑ по умолчанию).
/// «Сохранить выбранное» — выбранные → Active, невыбранные отклоняются;
/// «Сохранить все» — все → Active; «Отклонить всё» — все hard-delete.
/// Авто-закрытие через 30 с = «Сохранить выбранное». Esc/крестик — закрыть без
/// изменений (items остаются Pending, доступны в Memory Center).
/// Отклонение pending-item'а, заменившего существующий факт, возвращает
/// superseded-оригинал в Active.
/// </summary>
public partial class MemoryReviewWindow : Window
{
    private const string LogCategory = "Memory";
    private const int AutoCloseSeconds = 30;

    private IMemoryStore? _store;
    private long _meetingId;
    private List<ReviewRow> _rows = new();
    private DispatcherTimer? _timer;
    private int _secondsLeft = AutoCloseSeconds;
    private bool _applied;   // итог применён — Esc/X после этого уже ничего не меняет

    /// <summary>true, если по закрытии был применён Save/Discard (для статуса в вызывающем коде).</summary>
    public bool Completed => _applied;

    public MemoryReviewWindow()
    {
        InitializeComponent();
    }

    /// <summary>Подключить стор и встречу. Вызывать ДО Show().</summary>
    public void Initialize(IMemoryStore store, long meetingId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _meetingId = meetingId;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        LoadItems();
        if (_rows.Count > 0)
            StartCountdown();
    }

    // ---------------- загрузка ----------------

    private void LoadItems()
    {
        if (_store is null) return;
        try
        {
            var meeting = _store.GetMeeting(_meetingId);
            SubTitle.Text = string.Format(T("L_MR_SubFmt"), meeting?.Title ?? $"#{_meetingId}");

            _rows = _store.ListItems(status: MemoryItemStatus.Pending, limit: 1000)
                .Where(i => i.SourceMeetingId == _meetingId)
                .OrderByDescending(i => i.Importance)
                .Select(i => new ReviewRow(i, TypeLabel(i.Type)))
                .ToList();
            ItemsList.ItemsSource = _rows;
            var empty = _rows.Count == 0;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            SaveSelectedBtn.IsEnabled = SaveAllBtn.IsEnabled = DiscardAllBtn.IsEnabled = !empty;
        }
        catch (Exception ex)
        {
            App.Current?.Logger?.Warn($"MemoryReview load failed: {ex.Message}", LogCategory);
            StatusText.Text = string.Format(T("L_MR_ErrorFmt"), ex.Message);
        }
    }

    // ---------------- авто-закрытие ----------------

    private void StartCountdown()
    {
        CountdownText.Text = string.Format(T("L_MR_ClosesFmt"), _secondsLeft);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            if (_secondsLeft <= 0)
            {
                _timer.Stop();
                Apply(saveAll: false);   // таймаут = «Сохранить выбранное»
            }
            else
            {
                CountdownText.Text = string.Format(T("L_MR_ClosesFmt"), _secondsLeft);
            }
        };
        _timer.Start();
    }

    // ---------------- применение ----------------

    private void OnSaveSelected(object sender, RoutedEventArgs e) => Apply(saveAll: false);
    private void OnSaveAll(object sender, RoutedEventArgs e) => Apply(saveAll: true);
    private void OnDiscardAll(object sender, RoutedEventArgs e) => Apply(discardAll: true);

    /// <summary>
    /// Применить выбор: checked (или все при saveAll) → Active, остальные отклоняются;
    /// discardAll → отклонить всё. Записывает DoneFmt в лог и тост, закрывает окно.
    /// </summary>
    private void Apply(bool saveAll = false, bool discardAll = false)
    {
        if (_applied || _store is null) return;
        _applied = true;
        _timer?.Stop();

        int saved = 0, discarded = 0;
        try
        {
            foreach (var row in _rows)
            {
                var keep = saveAll || (!discardAll && row.Selected);
                if (keep)
                {
                    _store.SetStatus(row.Item.Id, MemoryItemStatus.Active);
                    saved++;
                }
                else
                {
                    DiscardItem(row.Item);
                    discarded++;
                }
            }
        }
        catch (Exception ex)
        {
            App.Current?.Logger?.Warn($"MemoryReview apply failed: {ex.Message}", LogCategory);
        }

        var done = string.Format(T("L_MR_DoneFmt"), saved, discarded);
        App.Current?.Logger?.Info($"Memory review meeting {_meetingId}: {done}", LogCategory);
        try { App.Current?.Notifier?.ShowInfo(T("L_MR_Title"), done); }
        catch { /* тост не критичен */ }
        Close();
    }

    /// <summary>
    /// Отклонить pending-item: hard-delete + если он заменял существующий факт —
    /// вернуть superseded-оригинал в Active (с очисткой SupersededById через UpdateItem).
    /// </summary>
    private void DiscardItem(MemoryItem it)
    {
        foreach (var old in _store!.ListItems(status: MemoryItemStatus.Superseded, limit: 1000))
        {
            if (old.SupersededById != it.Id) continue;
            old.Status = MemoryItemStatus.Active;
            old.SupersededById = null;
            _store.UpdateItem(old);
        }
        _store.HardDeleteItem(it.Id);
    }

    // ---------------- строки/кнопки ----------------

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
            Close();   // без применения — items остаются Pending
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnClosed(e);
    }

    /// <summary>Строка ревью: чекбокс + тип-чип + контент.</summary>
    private sealed class ReviewRow
    {
        public ReviewRow(MemoryItem item, string typeLabel)
        {
            Item = item;
            TypeLabel = typeLabel;
        }
        public MemoryItem Item { get; }
        public string TypeLabel { get; }
        public string Content => Item.Content;
        public bool Selected { get; set; } = true;   // ☑ по умолчанию (спека)
    }
}
