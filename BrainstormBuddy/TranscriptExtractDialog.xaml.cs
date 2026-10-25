using System.Collections.ObjectModel;
using System.Windows;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Memory.Extraction;

namespace BrainstormBuddy;

/// <summary>
/// Диалог «Извлечь в память» из окна файловой транскрибации: правила извлечения
/// (необязательные, запоминаются в Config.Memory.TranscriptExtractRules) ->
/// TranscriptFactExtractor.ExtractAsync (чанкинг всего транскрипта, прогресс по
/// частям) -> список директив с чекбоксами -> TranscriptFactSaver.SaveAsync
/// (идемпотентно, привязка к исходной встрече) -> тост + AppMessageBox.
/// Память выключена (MemoryStore == null / Config.Memory.Enabled == false) -
/// показывает заглушку вместо запуска, не падает.
/// </summary>
public partial class TranscriptExtractDialog : Window
{
    /// <summary>Строка списка: директива + галочка «сохранять».</summary>
    public sealed class FactRow
    {
        public bool IsChecked { get; set; } = true;
        public string Text { get; init; } = "";
        public MemoryDirective Directive { get; init; } = null!;
    }

    private readonly string _transcript;
    private readonly long? _meetingId;
    private readonly ObservableCollection<FactRow> _facts = new();
    private CancellationTokenSource? _cts;

    // Чанки и упавшие части последнего прогона - для взвешенного прогресса и «Повторить ошибки».
    private List<TranscriptChunk> _chunks = new();
    private readonly HashSet<int> _succeededIdx = new();   // успешно обработанные части (все прогоны)
    private int _totalChars;                               // сумма символов всех частей
    private List<int> _failedParts = new();
    private string? _rules;

    // Таймаут одного запроса к модели (на часть). Общий лимит не ставим: число частей
    // зависит от длины транскрипта, остановка - через «Остановить»/закрытие окна.
    private static readonly TimeSpan PartTimeout = TimeSpan.FromSeconds(120);

    // Память выключена/ещё инициализируется (MemoryStore публикуется из фонового
    // EnsureMemoryServices) — проверяем ЖИВОЕ состояние, а не снимок конструктора:
    // иначе открытый в момент фоновой инициализации диалог навсегда блокировал
    // «Извлечь» и вводил в заблуждение заглушкой «память выключена» (баг №74).
    private static bool MemoryOff => App.Current.MemoryStore == null || !App.Current.Config.Memory.Enabled;

    /// <summary>Локализованная строка из Strings.{lang}.xaml по ключу.</summary>
    private string L(string key) => (string)FindResource(key);
    /// <summary>Локализованная строка с RU-фолбэком для ключей, ещё не внесённых в словари.</summary>
    private string T(string key, string ruFallback) => TryFindResource(key) as string ?? ruFallback;
    private bool _busy;

    public TranscriptExtractDialog(string transcript)
    {
        InitializeComponent();
        _transcript = transcript;
        // Факты привязываются к встрече, открытой в момент извлечения, а не к той,
        // что случайно станет активной к моменту «Сохранить» (спека §6).
        _meetingId = App.Current.CurrentMeetingId;
        FactsList.ItemsSource = _facts;
        RulesBox.Text = App.Current.Config.Memory.TranscriptExtractRules ?? "";
        UpdateRulesHint();

        // Память выключена → диалог не «падает», а показывает заглушку вместо запуска;
        // «Извлечь» не блокируем навсегда — стор может появиться, пока диалог открыт.
        if (MemoryOff)
        {
            MemoryOffPanel.Visibility = Visibility.Visible;
            StatusText.Text = "";
        }
    }

    private void OnRulesTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateRulesHint();

    private void UpdateRulesHint()
        => RulesHint.Visibility = string.IsNullOrEmpty(RulesBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            // «Остановить»: рвём запрос и очередь частей, окно не закрываем -
            // готовые кандидаты остаются в списке, ошибка/итог - в блоке состояния.
            try { _cts?.Cancel(); } catch { /* уже disposed */ }
            return;
        }
        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ExtractBtn.IsEnabled = !busy;
        SaveFactsBtn.IsEnabled = !busy;
        RulesBox.IsEnabled = !busy;
        RememberRulesCheck.IsEnabled = !busy;
        RetryBtn.IsEnabled = !busy;
        // Во время работы «Отмена» превращается в «Остановить» и остаётся доступной.
        CancelBtn.Content = busy ? T("L_Extr_StopBtn", "Остановить") : L("L_Extr_CancelBtn");
    }

    private void PersistRulesIfAsked()
    {
        if (RememberRulesCheck.IsChecked != true) return;
        var rules = RulesBox.Text.Trim();
        if (App.Current.Config.Memory.TranscriptExtractRules != rules)
        {
            App.Current.Config.Memory.TranscriptExtractRules = rules;
            App.Current.SaveConfigSafe();
        }
    }

    // Вызов модели для одной части: готовый промпт -> content (null = пусто).
    // Canceled без внешней отмены = таймаут запроса -> понятная ошибка части.
    private static async Task<string> CallLlmAsync(string prompt, CancellationToken ct)
    {
        var r = await App.Current.ApiClient.AskAsync(
            "Извлеки факты из транскрибации по инструкциям выше.",
            prompt, App.Current.Config.Memory.ExtractMaxTokens, new List<ChatMessage>(), ct,
            background: true, requestTimeout: PartTimeout);
        if (r.Canceled)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("модель не ответила вовремя (таймаут запроса)");
        }
        if (!string.IsNullOrWhiteSpace(r.Error))
            throw new InvalidOperationException(r.Error);
        return r.Content ?? "";
    }

    private void ShowCandidates()
    {
        var has = _facts.Count > 0;
        FactsHeader.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        FactsList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SaveFactsBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ExtractBtn.IsDefault = !has;
        SaveFactsBtn.IsDefault = has;   // Enter после разбора = «Сохранить выбранные»
    }

    // Взвешенный процент = успешно обработанные символы / все символы транскрипта.
    // p.DoneChars - символы частей, закрытых в ТЕКУЩЕМ прогоне; _succeededIdx - база прошлых прогонов.
    private void ShowProgress(ExtractProgress p)
    {
        StatePanel.Visibility = Visibility.Visible;
        StateBar.Visibility = Visibility.Visible;
        var baseChars = _succeededIdx.Sum(i => _chunks[i].CharCount);
        StateBar.Value = _totalChars > 0
            ? Math.Min(100, 100.0 * (baseChars + p.DoneChars) / _totalChars)
            : 0;
        StateText.Text = string.Format(T("L_Extr_StRunningFmt",
            "Обработано частей: {0} из {1} · фактов: {2} · {3}"),
            p.PartsDone, p.TotalParts, p.CandidatesFound, p.Elapsed.ToString(@"mm\:ss"));
        RetryBtn.Visibility = Visibility.Collapsed;
    }

    // Итоговый статус прогона в блоке состояния (не закрывается сам - спека §6).
    private void ShowOutcome(ExtractionRunResult result)
    {
        StatePanel.Visibility = Visibility.Visible;
        StateBar.Visibility = Visibility.Collapsed;
        var failed = _failedParts;
        var doneTotal = _chunks.Count - failed.Count;
        string? retryLabel = null;
        string text;
        switch (result.Outcome)
        {
            case ExtractionOutcome.NoFactsFound:
                text = L("L_Extr_StNoFacts");
                break;
            case ExtractionOutcome.Cancelled:
                text = T("L_Extr_StStoppedFmt",
                    "Остановлено. Обработано частей: {0} из {1}. Готовые кандидаты сохранены в списке.");
                text = string.Format(text, doneTotal, _chunks.Count);
                break;
            case ExtractionOutcome.EmptyModelResponse:
            case ExtractionOutcome.MalformedResponse:
            case ExtractionOutcome.ServiceError:
                var reason = result.Outcome == ExtractionOutcome.ServiceError
                    ? L("L_Extr_StLlmUnavailable") + (result.Error ?? "")
                    : result.Outcome == ExtractionOutcome.EmptyModelResponse
                        ? T("L_Extr_ErrEmpty", "пустой ответ модели")
                        : T("L_Extr_ErrMalformed", "непригодный ответ модели (без директив)");
                text = string.Format(T("L_Extr_StFailedFmt",
                    "⚠ Не удалось обработать {0} из {1} частей: {2}"),
                    failed.Count, _chunks.Count, reason);
                retryLabel = T("L_Extr_RetryBtn", "Повторить ошибки");
                break;
            case ExtractionOutcome.Partial:
                text = string.Format(T("L_Extr_StPartialFmt",
                    "⚠ Частично: обработано {0} из {1} частей, не удалось {2}."),
                    doneTotal, _chunks.Count, failed.Count);
                if (!string.IsNullOrEmpty(result.Error)) text += " " + result.Error;
                retryLabel = T("L_Extr_RetryBtn", "Повторить ошибки");
                break;
            default: // CandidatesFound
                text = _facts.Count > 0
                    ? string.Format(T("L_Extr_StFoundFmt", "Найдено {0} фактов. Выберите, что сохранить."), _facts.Count)
                    : L("L_Extr_StNoFacts");
                if (failed.Count > 0)
                {
                    text += " " + string.Format(T("L_Extr_StPartialTailFmt", "Не обработано частей: {0}."), failed.Count);
                    retryLabel = T("L_Extr_RetryBtn", "Повторить ошибки");
                }
                break;
        }
        StateText.Text = text;
        if (retryLabel != null)
        {
            RetryBtn.Content = retryLabel;
            RetryBtn.Visibility = Visibility.Visible;
        }
        else RetryBtn.Visibility = Visibility.Collapsed;
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // Живая проверка памяти: стор мог появиться уже после открытия диалога
        // (EnsureMemoryServices работает в фоне) — тогда снимаем заглушку.
        if (MemoryOff)
        {
            StatusText.Text = L("L_Extr_MemoryOffSt");
            return;
        }
        MemoryOffPanel.Visibility = Visibility.Collapsed;
        _facts.Clear();
        ShowCandidates();
        StatusText.Text = "";
        _failedParts = new List<int>();
        PersistRulesIfAsked();
        _rules = string.IsNullOrWhiteSpace(RulesBox.Text) ? null : RulesBox.Text.Trim();
        _chunks = TranscriptFactExtractor.ChunkTranscript(_transcript);
        _totalChars = _chunks.Sum(c => c.CharCount);
        _succeededIdx.Clear();
        if (_chunks.Count == 0)
        {
            StatePanel.Visibility = Visibility.Visible;
            StateText.Text = L("L_Extr_StNoFacts");
            return;
        }
        await RunExtractionAsync(partIndices: null);
    }

    private async void OnRetryFailed(object sender, RoutedEventArgs e)
    {
        if (_busy || _failedParts.Count == 0 || _chunks.Count == 0) return;
        await RunExtractionAsync(_failedParts);
    }

    // Общий прогон: полный (partIndices=null) или только упавшие части.
    private async Task RunExtractionAsync(IReadOnlyCollection<int>? partIndices)
    {
        SetBusy(true);
        StatePanel.Visibility = Visibility.Visible;
        StateBar.Visibility = Visibility.Visible;
        StateBar.Value = 0;
        RetryBtn.Visibility = Visibility.Collapsed;
        var runOrder = partIndices == null
            ? _chunks.Select(c => c.Index).ToList()
            : _chunks.Where(c => partIndices.Contains(c.Index)).Select(c => c.Index).ToList();
        var progress = new Progress<ExtractProgress>(ShowProgress);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // §6.4: интенсивность извлечения из конфига (low/medium/high).
        var intensity = App.Current?.Config?.Memory?.ExtractionIntensity;
        try
        {
            var result = partIndices == null
                ? await TranscriptFactExtractor.ExtractAsync(_transcript, _rules, CallLlmAsync,
                    progress, ct, intensity)
                : await TranscriptFactExtractor.ExtractPartsAsync(_chunks, _rules, CallLlmAsync,
                    partIndices, progress, ct, intensity);

            // Новые кандидаты повторного прогона дописываются к готовым, дубли текста отсекаем.
            var seen = new HashSet<string>(_facts.Select(f => TranscriptFactExtractor.NormalizeFact(f.Directive.Old)),
                StringComparer.Ordinal);
            foreach (var c in result.Candidates)
            {
                if (!seen.Add(TranscriptFactExtractor.NormalizeFact(c.Text))) continue;
                _facts.Add(new FactRow { Directive = c.Directive, Text = Describe(c) });
            }
            // Отмечаем успешные части прогона (для взвешенной шкалы при повторах):
            // первые PartsDone+Failed репортов - завершённые; упавшие не засчитываем.
            var completed = result.PartsDone + result.FailedParts.Count;
            if (result.Outcome != ExtractionOutcome.Cancelled)
            {
                _succeededIdx.UnionWith(runOrder.Take(completed));
                _succeededIdx.ExceptWith(result.FailedParts);
            }
            _failedParts = result.FailedParts.ToList();
            ShowCandidates();
            ShowOutcome(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ShowOutcome(new ExtractionRunResult(ExtractionOutcome.Cancelled,
                Array.Empty<FactCandidate>(), _failedParts, 0, null));
        }
        catch (OperationCanceledException)
        {
            // Отмена не от юзера (внутренний таймаут стрима) - показываем как ошибку части.
            ShowOutcome(new ExtractionRunResult(ExtractionOutcome.ServiceError,
                Array.Empty<FactCandidate>(), _failedParts, 0,
                T("L_Extr_ErrTimeout", "модель не ответила вовремя")));
        }
        catch (Exception ex)
        {
            ShowOutcome(new ExtractionRunResult(ExtractionOutcome.ServiceError,
                Array.Empty<FactCandidate>(), _failedParts, 0, ex.Message));
            App.Current?.Logger?.Warn($"TranscriptExtract: {ex.Message}", "UI");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void OnSaveFacts(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var store = App.Current.MemoryStore;
        var selected = _facts.Where(f => f.IsChecked).Select(f => f.Directive).ToList();
        if (selected.Count == 0) { StatusText.Text = L("L_Extr_StNothingSelected"); return; }
        if (store == null) { StatusText.Text = L("L_Extr_MemoryOffSt"); return; }

        SetBusy(true);
        StatePanel.Visibility = Visibility.Visible;
        StateBar.Visibility = Visibility.Collapsed;
        RetryBtn.Visibility = Visibility.Collapsed;
        StateText.Text = L("L_Extr_StSaving");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            // Идемпотентно: дубли по тексту отсекаются внутри сейвера; встреча - исходная.
            var result = await TranscriptFactSaver.SaveAsync(store, selected, _meetingId, ct);
            if (result.Added > 0)
            {
                App.Current.ShowMemoryAddedToast(new List<string> { string.Format(L("L_Extr_SavedFmt"), result.Added) });
                StateText.Text = string.Format(L("L_Extr_SavedFmt"), result.Added)
                    + (result.SkippedDuplicates > 0
                        ? " " + string.Format(T("L_Extr_StDupsFmt", "Дубликатов пропущено: {0}."), result.SkippedDuplicates)
                        : "");
                AppMessageBox.Show(this, StateText.Text, L("L_Extr_MsgTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            else
            {
                StateText.Text = L("L_Extr_StNothingSaved");
            }
        }
        catch (OperationCanceledException)
        {
            StateText.Text = L("L_Extr_StSaveCancelled");
        }
        catch (Exception ex)
        {
            StateText.Text = L("L_Extr_StSaveFailedPrefix") + ex.Message;
            App.Current.Logger?.Warn($"TranscriptExtract save: {ex.Message}", "UI");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // Человекочитаемая строка кандидата в списке (с таймкодом-якорем части, если есть).
    private string Describe(FactCandidate c) =>
        (c.TimeAnchor != null ? $"[≈{c.TimeAnchor}] " : "") + "＋ " + c.Directive.Old;

    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* уже закрыто */ }
        base.OnClosed(e);
    }
}
