using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private CancellationTokenSource? _cts;
    private MeetingInfo? _current;
    private bool _busy;

    /// <summary>Локализованная строка из Strings.{lang}.xaml по ключу.</summary>
    private string L(string key) => (string)FindResource(key);

    public MeetingHistoryWindow()
    {
        InitializeComponent();
        _store = new MeetingTranscriptStore(App.Current.AppDataDir);
        // Волна 04: путь хранения — служебной строкой 12/18 под описанием
        // пустого состояния (реальный путь вместо %APPDATA%-маски).
        StoragePath.Text = _store.Directory;
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

    private void LoadMeetings() => MeetingList.ItemsSource = _store.LoadAll();

    // ---- Выбор встречи ----
    private void OnMeetingSelect(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        if (MeetingList.SelectedItem is not MeetingInfo info) return;
        _current = info;
        EmptyPanel.Visibility = Visibility.Collapsed;
        var lines = _store.ReadLines(info.JournalPath);
        TranscriptBox.Text = lines.Count > 0 ? MeetingTranscriptStore.FormatTranscript(lines) : L("L_Meet_EmptyTranscript");
        SummaryBox.Text = info.Summary ?? "";
        SummaryBtn.IsEnabled = lines.Count > 0;
        ExtractBtn.IsEnabled = lines.Count > 0;
        DeleteBtn.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    // ---- Удаление ----
    private void OnDeleteMeeting(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // Кнопка действует на выбранную встречу; из контекстного меню — на ту,
        // по которой кликнули правой (WPF выделяет её при открытии меню).
        if (MeetingList.SelectedItem is not MeetingInfo info) return;
        _store.Delete(info.Id);
        if (_current?.Id == info.Id)
        {
            _current = null;
            TranscriptBox.Text = "";
            SummaryBox.Text = "";
            SummaryBtn.IsEnabled = false;
            ExtractBtn.IsEnabled = false;
            DeleteBtn.IsEnabled = false;
            EmptyPanel.Visibility = Visibility.Visible;
        }
        LoadMeetings();
    }

    // ---- Копирование / скачивание ----
    private void OnCopyTranscript(object sender, RoutedEventArgs e) => CopyText(TranscriptBox.Text, L("L_Meet_CopiedTranscript"));
    private void OnCopySummary(object sender, RoutedEventArgs e) => CopyText(SummaryBox.Text, L("L_Meet_CopiedSummary"));

    private void CopyText(string text, string ok)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); App.Current.Notifier?.ShowInfo(L("L_Meet_Title"), ok); }
        catch { /* буфер иногда занят другим процессом */ }
    }

    private void OnDownloadTranscript(object sender, RoutedEventArgs e) => SaveText(TranscriptBox.Text, "transcript");
    private void OnDownloadSummary(object sender, RoutedEventArgs e) => SaveText(SummaryBox.Text, "summary");

    private void SaveText(string text, string kind)
    {
        if (string.IsNullOrEmpty(text)) { ShowError(L("L_Meet_NothingToSave")); return; }
        var baseName = _current?.Id ?? "meeting";
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = L("L_Meet_FilterTxt"), FileName = $"{baseName}_{kind}.txt" };
        if (dlg.ShowDialog() == true)
        {
            try { File.WriteAllText(dlg.FileName, text); App.Current.Notifier?.ShowInfo(L("L_Meet_Title"), L("L_Meet_FileSaved")); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }

    // ---- Извлечение фактов транскрипта в долговременную память ----
    private void OnExtractToMemory(object sender, RoutedEventArgs e)
    {
        if (_busy || _current == null) return;
        var transcript = TranscriptBox.Text;
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
        string transcript = TranscriptBox.Text;
        if (string.IsNullOrWhiteSpace(transcript)) { ShowError(L("L_Meet_NoTranscript")); return; }

        SetBusy(true);
        Progress.IsIndeterminate = true;
        StatusText.Text = L("L_Meet_StGenerating");
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = _cts.Token;
        try
        {
            string summary = await MeetingSummarizer.SummarizeAsync(
                App.Current.ApiClient, transcript,
                s => Dispatcher.Invoke(() => StatusText.Text = s), ct,
                App.Current.Config.Advanced.SummaryPrompt);
            Progress.IsIndeterminate = false;
            if (string.IsNullOrWhiteSpace(summary))
            {
                StatusText.Text = L("L_Meet_StLlmNoAnswer");
                App.Current.Notifier?.ShowError(L("L_Meet_SummaryBtn"),
                    L("L_Meet_ErrLlmUnavailable"));
            }
            else
            {
                Progress.Value = Progress.Maximum;
                SummaryBox.Text = summary;
                StatusText.Text = L("L_Meet_StSummaryDone");
                _current.Summary = summary;
                _store.SaveInfo(_current);
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
            Progress.IsIndeterminate = false;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
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
        SummaryBtn.IsEnabled = !busy && _current != null && !string.IsNullOrWhiteSpace(TranscriptBox.Text);
        ExtractBtn.IsEnabled = !busy && _current != null && !string.IsNullOrWhiteSpace(TranscriptBox.Text);
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

    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* уже закрыто */ }
        base.OnClosed(e);
    }
}
