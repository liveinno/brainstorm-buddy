using System.Windows;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;
using BrainstormBuddy.Memory;
// ImplicitUsings+WinForms делают KeyEventArgs двусмысленным (CS0104) — фиксируем WPF-вариант.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace BrainstormBuddy;

/// <summary>
/// «Спросить память» (спека ASK MEMORY / MEMORY ANSWERS): работает и без активного
/// звонка. Два режима — быстрый поиск (список <see cref="MemoryRecallHit"/> со
/// score/типом/scope/provenance) и LLM-ответ (recall → <c>&lt;memory_context&gt;</c>
/// → <see cref="IApiClient.AskAsync"/>; ответ отличает факты памяти от выводов).
/// Живёт самодостаточно: сервисы передаются через <see cref="Initialize"/>,
/// окно создаёт/показывает вызывающий (W1).
/// Esc — закрыть; Enter в поле вопроса — выполнить запрос.
/// </summary>
public partial class AskMemoryWindow : Window
{
    private const string LogCategory = "Memory";
    private const int AnswerMaxTokens = 1024;

    /// <summary>Ответ строится ТОЛЬКО на сохранённой памяти; без фактов — честный отказ.</summary>
    private const string AskSystemPrompt =
        "Ты — модуль памяти ассистента. Отвечай на вопрос, опираясь ТОЛЬКО на блок " +
        "<memory_context> ниже — это сохранённые факты из прошлых встреч. " +
        "Чётко различай: «факт из памяти» vs «твой вывод». Если фактов недостаточно — " +
        "честно ответь: «В сохранённой памяти этого нет». Не выдумывай ответ, " +
        "указывай источник (тип факта, встреча/дата, если есть). " +
        "Отвечай кратко и на языке вопроса.";

    private IMemoryRecallService? _recall;
    private IApiClient? _api;
    private IMemoryStore? _store;
    private MemoryConfig _cfg = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    // Кэши имён для scope-бейджей (Project/Person/Meeting → читаемая подпись).
    private Dictionary<long, string>? _projectNames;
    private Dictionary<long, string>? _personNames;
    private Dictionary<long, string>? _meetingNames;

    public AskMemoryWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Подключить сервисы памяти. Вызывается один раз после создания окна.
    /// <paramref name="api"/> = null → режим «Ответ» задизаблен с тултипом
    /// (LLM-ответ без IApiClient невозможен, поиск по-прежнему работает).
    /// </summary>
    public void Initialize(IMemoryRecallService recall, IApiClient? api,
        IMemoryStore store, MemoryConfig cfg)
    {
        _recall = recall ?? throw new ArgumentNullException(nameof(recall));
        _api = api;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cfg = cfg ?? new MemoryConfig();

        if (_api is null)
        {
            ModeAnswer.IsEnabled = false;
            ModeAnswer.ToolTip = TryFindResource("L_AM_TT_AnswerDisabled");
            if (ModeAnswer.IsChecked == true)
                ModeSearch.IsChecked = true;
        }
    }

    // ---------------- ввод / режимы ----------------

    private void OnQuestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = AskAsync();
    }

    private void OnAsk(object sender, RoutedEventArgs e) => _ = AskAsync();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private bool AnswerMode => ModeAnswer.IsChecked == true && _api is not null;

    private async Task AskAsync()
    {
        if (_busy || _recall is null) return;
        var query = QuestionBox.Text.Trim();
        if (query.Length == 0)
        {
            SetStatus(T("L_AM_TT_Question"), error: false);
            return;
        }

        _busy = true;
        AskBtn.IsEnabled = false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        AnswerPanel.Visibility = Visibility.Collapsed;
        HitsHeader.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        ShowBusy(on: true);
        SetStatus("", error: false);

        try
        {
            var hits = await _recall.RecallAsync(
                new MemoryRecallQuery(query, MaxItems: _cfg.RecallMaxItems,
                    MaxTokens: _cfg.RecallMaxTokens),
                ct);

            if (AnswerMode)
                await ShowLlmAnswerAsync(query, hits, ct);
            else
                ShowHits(hits);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Current?.Logger?.Warn($"AskMemory failed: {ex.Message}", LogCategory);
            ShowHits([]);
            SetStatus(string.Format(T("L_AM_ErrorFmt"), ex.Message), error: true);
        }
        finally
        {
            _busy = false;
            AskBtn.IsEnabled = true;
            ShowBusy(on: false);
        }
    }

    // ---------------- режим «Поиск»: top hits со score/типом/scope ----------------

    private void ShowHits(List<MemoryRecallHit> hits)
    {
        HitsList.ItemsSource = hits.Select(ToRow).ToList();
        HitsHeader.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(hits.Count == 0 ? ""
            : string.Format(T("L_AM_HitsFmt"), hits.Count), error: false);
    }

    // ---------------- режим «Ответ»: <memory_context> + вопрос → LLM ----------------

    private async Task ShowLlmAnswerAsync(string query, List<MemoryRecallHit> hits,
        CancellationToken ct)
    {
        HitsList.ItemsSource = hits.Select(ToRow).ToList();
        HitsHeader.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnswerPanel.Visibility = Visibility.Visible;

        var context = hits.Count > 0 && _recall is not null
            ? _recall.BuildContextBlock(hits, _cfg.RecallMaxTokens)
            : "";

        // Спека MEMORY ANSWERS: без источников не вызываем LLM — честный отказ.
        if (string.IsNullOrEmpty(context))
        {
            AnswerText.Text = T("L_AM_Empty");
            SetStatus("", error: false);
            return;
        }

        BusyText.Text = T("L_St_Thinking");
        ShowBusy(on: true);
        AnswerText.Text = "";

        var userText = context + "\n\nВопрос: " + query;
        var res = await _api!.AskAsync(userText, AskSystemPrompt, AnswerMaxTokens,
            new List<ChatMessage>(), ct,
            onDelta: delta => Dispatcher.InvokeAsync(() => AnswerText.Text += delta));

        if (res.Canceled) return;
        if (res.Error is { } err)
        {
            AnswerText.Text = "";
            SetStatus(string.Format(T("L_AM_ErrorFmt"), err), error: true);
            return;
        }
        // Если провайдер не стримил — ответ приходит целиком в Content.
        if (!string.IsNullOrEmpty(res.Content))
            AnswerText.Text = res.Content;
        else if (AnswerText.Text.Length == 0 || res.IsSilent)
            AnswerText.Text = T("L_ModelNoAnswer");

        SetStatus(string.Format(T("L_AM_HitsFmt"), hits.Count), error: false);
    }

    // ---------------- отображение hits ----------------

    private HitRow ToRow(MemoryRecallHit hit) => new(this, hit);

    private string T(string key) => App.Current?.T(key) ?? key;

    private void ShowBusy(bool on)
    {
        BusyPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        if (TryFindResource(error ? "ErrorBrush" : "TextDimBrush") is Brush b)
            StatusText.Foreground = b;
    }

    private string ScopeLabel(MemoryItem it) => it.ScopeType switch
    {
        MemoryScopeType.Global => T("L_AM_Scope_Global"),
        MemoryScopeType.Project => NameOf(_projectNames, it.ScopeId, LoadProjects)
            ?? string.Format(T("L_AM_Scope_ProjectFmt"), it.ScopeId),
        MemoryScopeType.Person => NameOf(_personNames, it.ScopeId, LoadPeople)
            ?? string.Format(T("L_AM_Scope_PersonFmt"), it.ScopeId),
        MemoryScopeType.Meeting => NameOf(_meetingNames, it.ScopeId, LoadMeetings)
            ?? string.Format(T("L_AM_Scope_MeetingFmt"), it.ScopeId),
        _ => it.ScopeType.ToString(),
    };

    private static string? NameOf(Dictionary<long, string>? cache, long? id,
        Func<Dictionary<long, string>> load)
        => id is { } v ? (cache ?? load()).GetValueOrDefault(v) : null;

    private Dictionary<long, string> LoadProjects()
    {
        try { _projectNames ??= _store?.ListProjects(includeArchived: true)
            .ToDictionary(p => p.Id, p => p.Name) ?? new(); }
        catch { _projectNames ??= new(); }
        return _projectNames;
    }

    private Dictionary<long, string> LoadPeople()
    {
        try { _personNames ??= _store?.ListPeople()
            .ToDictionary(p => p.Id, p => p.Name) ?? new(); }
        catch { _personNames ??= new(); }
        return _personNames;
    }

    private Dictionary<long, string> LoadMeetings()
    {
        try
        {
            _meetingNames ??= _store?.ListMeetings()
                .ToDictionary(m => m.Id, m => m.Title.Length > 0 ? m.Title : $"#{m.Id}") ?? new();
        }
        catch { _meetingNames ??= new(); }
        return _meetingNames;
    }

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

    private string StatusLabel(MemoryItemStatus st) => st switch
    {
        MemoryItemStatus.Superseded => T("L_AM_Status_Superseded"),
        MemoryItemStatus.Invalidated => T("L_AM_Status_Invalidated"),
        _ => T("L_AM_Status_Pending"),
    };

    /// <summary>Мета-строка hit: why-теги ранкера + provenance (встреча/спикер) + статус.</summary>
    private string MetaOf(MemoryRecallHit hit)
    {
        var it = hit.Item;
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(hit.Why))
            parts.Add(hit.Why);
        if (it.SourceMeetingId is { } mid)
            parts.Add(string.Format(T("L_AM_Fmt_Meeting"), mid));
        if (!string.IsNullOrEmpty(it.Speaker))
            parts.Add(it.Speaker);
        if (it.Status != MemoryItemStatus.Active)
            parts.Add(StatusLabel(it.Status));
        return string.Join(" · ", parts);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnClosed(e);
    }

    /// <summary>Строка результата поиска: чипы типа/scope, score, контент, мета.</summary>
    private sealed class HitRow
    {
        public HitRow(AskMemoryWindow w, MemoryRecallHit hit)
        {
            TypeLabel = w.TypeLabel(hit.Item.Type);
            ScopeLabel = w.ScopeLabel(hit.Item);
            ScoreText = hit.Score.ToString("0.00");
            Content = hit.Item.Content;
            MetaText = w.MetaOf(hit);
        }
        public string TypeLabel { get; }
        public string ScopeLabel { get; }
        public string ScoreText { get; }
        public string Content { get; }
        public string MetaText { get; }
    }
}
