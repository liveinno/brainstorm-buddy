using System.Collections.ObjectModel;
using System.Windows;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Memory;

namespace BrainstormBuddy;

/// <summary>
/// Диалог «Извлечь в память» из окна файловой транскрибации: правила извлечения
/// (необязательные, запоминаются в Config.Memory.TranscriptExtractRules) →
/// LLM (TranscriptFactExtractor.BuildPrompt → ApiClient.AskAsync → ParseOutput)
/// → список директив с чекбоксами → MemoryWriter.ApplyAsync → тост + AppMessageBox.
/// Память выключена (MemoryStore == null / Config.Memory.Enabled == false) —
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
    private readonly ObservableCollection<FactRow> _facts = new();
    private CancellationTokenSource? _cts;

    // Память выключена/ещё инициализируется (MemoryStore публикуется из фонового
    // EnsureMemoryServices) — проверяем ЖИВОЕ состояние, а не снимок конструктора:
    // иначе открытый в момент фоновой инициализации диалог навсегда блокировал
    // «Извлечь» и вводил в заблуждение заглушкой «память выключена» (баг №74).
    private static bool MemoryOff => App.Current.MemoryStore == null || !App.Current.Config.Memory.Enabled;
    /// <summary>Лимит транскрипта в промпте (символов): длинные файлы режем
    /// голова 60% + хвост 40%, иначе ответ режется/теряется контекст (бэклог №64).</summary>
    private const int TranscriptPromptLimit = 24_000;

    /// <summary>Локализованная строка из Strings.{lang}.xaml по ключу.</summary>
    private string L(string key) => (string)FindResource(key);
    private bool _busy;

    public TranscriptExtractDialog(string transcript)
    {
        InitializeComponent();
        _transcript = transcript;
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
        _cts?.Cancel();
        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ExtractBtn.IsEnabled = !busy;
        SaveFactsBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = !busy;
        RulesBox.IsEnabled = !busy;
        RememberRulesCheck.IsEnabled = !busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) BusyText.Text = L("L_Extr_Busy");
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
        SetBusy(true);
        _facts.Clear();
        FactsList.Visibility = Visibility.Collapsed;
        FactsHeader.Visibility = Visibility.Collapsed;
        SaveFactsBtn.Visibility = Visibility.Collapsed;
        ExtractBtn.IsDefault = true;
        SaveFactsBtn.IsDefault = false;
        StatusText.Text = "";
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = _cts.Token;
        try
        {
            PersistRulesIfAsked();
            var rules = string.IsNullOrWhiteSpace(RulesBox.Text) ? null : RulesBox.Text.Trim();
            // Длинный транскрипт режем с двух сторон (голова 60% + хвост 40%):
            // факты чаще всего в начале и в финале встречи (бэклог №64).
            var transcript = _transcript;
            if (transcript.Length > TranscriptPromptLimit)
            {
                var headLen = TranscriptPromptLimit * 6 / 10;
                var tailLen = TranscriptPromptLimit - headLen;
                transcript = transcript[..headLen] +
                    $"\n\n[…пропущено {transcript.Length - TranscriptPromptLimit} символов середины…]\n\n" +
                    transcript[^tailLen..];
                App.Current.Logger?.Warn(
                    $"TranscriptExtract: transcript truncated {_transcript.Length} → {transcript.Length} chars", "UI");
            }
            var prompt = TranscriptFactExtractor.BuildPrompt(transcript, rules);
            var r = await App.Current.ApiClient.AskAsync(
                "Извлеки факты из транскрибации по инструкциям выше.",
                prompt, 3000, new List<ChatMessage>(), ct);
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(r.Error))
            {
                StatusText.Text = L("L_Extr_StLlmUnavailable") + r.Error;
                return;
            }
            var raw = r.Content ?? "";
            var dirs = TranscriptFactExtractor.ParseOutput(raw);
            if (dirs.Count == 0)
            {
                // Сырой ответ — в лог: следующий разбор «пусто» идёт по логу, а не вслепую.
                App.Current.Logger?.Debug(
                    $"TranscriptExtract: no directives, raw output: {raw[..Math.Min(500, raw.Length)]}", "UI");
            StatusText.Text = string.IsNullOrWhiteSpace(raw)
                    ? L("L_Extr_StNoFacts")
                    : L("L_Extr_StNoDirectives");
                return;
            }
            foreach (var d in dirs)
                _facts.Add(new FactRow { Directive = d, Text = Describe(d) });
            FactsHeader.Visibility = Visibility.Visible;
            FactsList.Visibility = Visibility.Visible;
            SaveFactsBtn.Visibility = Visibility.Visible;
            ExtractBtn.IsDefault = false;
            SaveFactsBtn.IsDefault = true; // Enter после разбора = «Сохранить выбранные»
            StatusText.Text = string.Format(L("L_Extr_StExtractedFmt"), _facts.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("L_Extr_StExtractCancelled");
        }
        catch (Exception ex)
        {
            StatusText.Text = L("L_Extr_StErrorPrefix") + ex.Message;
            App.Current.Logger?.Warn($"TranscriptExtract: {ex.Message}", "UI");
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
        BusyText.Text = L("L_Extr_StSaving");
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = _cts.Token;
        try
        {
            var writer = new MemoryWriter(store, App.Current.Logger);
            IReadOnlyList<string> applied = await writer.ApplyAsync(selected, App.Current.CurrentMeetingId, ct);
            if (applied.Count > 0)
            {
                App.Current.ShowMemoryAddedToast(applied);
                AppMessageBox.Show(this, string.Format(L("L_Extr_SavedFmt"), applied.Count), L("L_Extr_MsgTitle"),
                                MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            else
            {
                StatusText.Text = L("L_Extr_StNothingSaved");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("L_Extr_StSaveCancelled");
        }
        catch (Exception ex)
        {
            StatusText.Text = L("L_Extr_StSaveFailedPrefix") + ex.Message;
            App.Current.Logger?.Warn($"TranscriptExtract save: {ex.Message}", "UI");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // Человекочитаемая строка директивы в списке.
    private string Describe(MemoryDirective d) => d.Kind switch
    {
        MemoryDirectiveKind.Add => "＋ " + d.Old,
        MemoryDirectiveKind.Update => "✎ " + d.Old + "  →  " + d.New,
        MemoryDirectiveKind.Forget => string.Format(L("L_Extr_ForgetFmt"), d.Old),
        _ => d.Old
    };

    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* уже закрыто */ }
        base.OnClosed(e);
    }
}
