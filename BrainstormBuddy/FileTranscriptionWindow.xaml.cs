using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Media;
using BrainstormBuddy.Stt;
using BrainstormBuddy.Transcription;

namespace BrainstormBuddy;

/// <summary>
/// Окно локальной транскрибации медиафайлов встроенным GigaAM:
/// извлечение аудио (mp4/webm/…) → распознавание с тайм-кодами → AI-саммари.
/// История хранится в %APPDATA%\BrainstormBuddy\transcriptions.
/// </summary>
public partial class FileTranscriptionWindow : Window
{
    private static readonly string[] NeedsFfmpeg = { ".webm", ".ogg", ".opus", ".mkv", ".flac", ".oga" };

    private readonly TranscriptionHistoryStore _history;
    private readonly MediaAudioExtractor _extractor = new();
    private CancellationTokenSource? _cts;
    private string? _selectedPath;
    private FileTranscriptResult? _currentResult;
    private TranscriptionRecord? _currentRecord;
    private bool _busy;

    public FileTranscriptionWindow()
    {
        InitializeComponent();
        _history = new TranscriptionHistoryStore(App.Current.AppDataDir);
        LoadHistory();
        // По умолчанию — как глобальный движок (если выбран Whisper, окно тоже на Whisper).
        EngineCombo.SelectedValue = (App.Current.Config.Audio.SttEngine?.ToLowerInvariant() == "whisper") ? "whisper" : "gigaam";
        if (EngineCombo.SelectedIndex < 0) EngineCombo.SelectedIndex = 0;
        PopulateHardwareCombo();
    }

    private string EngineChoice() => (EngineCombo?.SelectedValue as string) ?? "gigaam";

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e) => UpdateEngineChip();

    // Список оборудования файловой транскрибации: Авто / CPU / все GPU из системы.
    // Tag = "auto|-1" / "cpu|-1" / "gpu|<dxgi-index>", восстановление — из конфига.
    private void PopulateHardwareCombo()
    {
        HardwareCombo.Items.Clear();
        HardwareCombo.Items.Add(new ComboBoxItem { Content = "Авто (как в настройках)", Tag = "auto|-1" });
        HardwareCombo.Items.Add(new ComboBoxItem { Content = "CPU", Tag = "cpu|-1" });
        try
        {
            foreach (var g in BrainstormBuddy.Native.GpuEnumerator.List())
                HardwareCombo.Items.Add(new ComboBoxItem { Content = $"GPU: #{g.Index} {g.Name}", Tag = $"gpu|{g.Index}" });
        }
        catch { /* перечисление GPU best-effort: без GPU остаются Авто/CPU */ }

        var cfg = App.Current.Config.Audio;
        var accel = (cfg.FileSttAccel ?? "auto").ToLowerInvariant();
        var wanted = accel == "gpu" ? $"gpu|{cfg.FileSttGpuDevice}" : $"{accel}|-1";
        HardwareCombo.SelectedValue = wanted;
        // Сохранённый GPU мог исчезнуть из системы (отключили eGPU и т.п.) → откат на Авто.
        if (HardwareCombo.SelectedIndex < 0) HardwareCombo.SelectedIndex = 0;
    }

    private void OnHardwareChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || HardwareCombo.SelectedValue is not string tag) return;
        var parts = tag.Split('|');
        var cfg = App.Current.Config.Audio;
        cfg.FileSttAccel = parts[0];
        cfg.FileSttGpuDevice = parts.Length > 1 && int.TryParse(parts[1], out var d) ? d : -1;
        App.Current.SaveConfigSafe(); // выбор переживает перезапуск, как кнопки оверлея
        App.Current.Logger.Info($"FileTranscription: оборудование → {cfg.FileSttAccel} (gpu={cfg.FileSttGpuDevice})", "UI");
        UpdateEngineChip();
    }

    // Человеческая подпись выбранного железа для чипа под комбо.
    private string HardwareNote()
    {
        var cfg = App.Current.Config.Audio;
        return (cfg.FileSttAccel ?? "auto").ToLowerInvariant() switch
        {
            "cpu" => "CPU",
            "gpu" => $"GPU #{(cfg.FileSttGpuDevice >= 0 ? cfg.FileSttGpuDevice.ToString() : "авто")}",
            _ => "авто"
        };
    }

    private void UpdateEngineChip()
    {
        if (EngineText == null) return;
        bool ff = MediaAudioExtractor.TryFindFfmpeg(out _);
        string ffNote = ff ? "ffmpeg ✓" : "webm: нужен ffmpeg";
        if (EngineChoice() == "whisper")
        {
            bool have = App.Current.ResolveWhisperModel() != null;
            EngineText.Text = (have ? "Whisper готов" : "⚠ модель Whisper не скачана (Настройки → Локальный STT)") + $" · {HardwareNote()} · " + ffNote;
        }
        else
        {
            EngineText.Text = $"GigaAM — быстрый, русский · {HardwareNote()} · " + ffNote;
        }
    }

    private void LoadHistory() => HistoryList.ItemsSource = _history.LoadAll();

    // ---- Выбор файла ----
    private void OnSelectFile(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите аудио или видео файл",
            Filter = "Медиа (аудио/видео)|*.mp4;*.webm;*.mkv;*.mov;*.avi;*.m4a;*.mp3;*.wav;*.aac;*.ogg;*.opus;*.flac|Все файлы|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _selectedPath = dlg.FileName;
            FileNameText.Text = Path.GetFileName(_selectedPath);
            FileNameText.Foreground = (Brush)FindResource("TextBrightBrush");
            TranscribeBtn.IsEnabled = true;
            UpdateFfmpegBanner();
        }
    }

    // Показывает жёлтую плашку, если выбран webm/mkv-подобный файл, а ffmpeg не найден.
    private void UpdateFfmpegBanner()
    {
        bool need = !string.IsNullOrEmpty(_selectedPath)
                    && NeedsFfmpeg.Contains(Path.GetExtension(_selectedPath).ToLowerInvariant())
                    && !MediaAudioExtractor.TryFindFfmpeg(out _);
        FfmpegBanner.Visibility = need ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnDownloadFfmpeg(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        FfmpegDownloadBtn.IsEnabled = false;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = "Скачивание ffmpeg…";
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = _cts.Token;
        try
        {
            var installer = new FfmpegInstaller();
            var progress = new Progress<double>(f => Dispatcher.Invoke(() =>
            {
                Progress.Value = f;
                StatusText.Text = $"Скачивание ffmpeg… {f * 100:F0}%";
            }));
            await installer.InstallAsync(MediaAudioExtractor.DownloadedFfmpegDir, progress, ct);
            StatusText.Text = "ffmpeg установлен ✓ — webm теперь поддерживается.";
            FfmpegBanner.Visibility = Visibility.Collapsed;
            UpdateEngineChip();
            App.Current.Notifier?.ShowInfo("Транскрибация файла", "ffmpeg установлен");
        }
        catch (OperationCanceledException) { StatusText.Text = "Скачивание ffmpeg отменено."; }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось скачать ffmpeg.";
            ShowError("Не удалось скачать ffmpeg: " + ex.Message +
                      "\n\nБез интернета: переустановите приложение с галочкой «ffmpeg», " +
                      "либо положите ffmpeg.exe в:\n" + MediaAudioExtractor.DownloadedFfmpegDir);
        }
        finally
        {
            FfmpegDownloadBtn.IsEnabled = true;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---- Транскрибация ----
    private async void OnTranscribe(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrEmpty(_selectedPath)) return;
        if (!File.Exists(_selectedPath)) { ShowError("Файл не найден (возможно, перемещён)."); return; }

        if (NeedsFfmpeg.Contains(Path.GetExtension(_selectedPath).ToLowerInvariant())
            && !MediaAudioExtractor.TryFindFfmpeg(out _))
        {
            UpdateFfmpegBanner();
            ShowError("Для этого формата (webm/mkv/opus) нужен ffmpeg. Нажмите «Скачать ffmpeg (~80 МБ)» в жёлтой плашке.");
            return;
        }

        var choice = EngineChoice();
        var transcriber = App.Current.ResolveFileTranscriber(choice, out var err);
        if (transcriber == null)
        {
            if (choice == "whisper")
            {
                UpdateEngineChip();
                App.Current.Notifier?.ShowError("Whisper", err ?? "Модель Whisper не найдена. Скачайте её в Настройки → Локальный STT.");
            }
            else ShowError(err ?? "Встроенный движок недоступен.");
            return;
        }

        var path = _selectedPath;
        SetBusy(true);
        Progress.IsIndeterminate = true;
        StatusText.Text = choice == "whisper" ? "Извлечение аудио… (Whisper грузится ~10с при первом запуске)" : "Извлечение аудио…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var result = await Task.Run(() =>
            {
                var audio = _extractor.Extract(path, ct);
                Dispatcher.Invoke(() =>
                {
                    Progress.IsIndeterminate = false;
                    Progress.Value = 0;
                    StatusText.Text = "Распознавание…";
                });
                return transcriber.Transcribe(audio.Samples, audio.Duration, audio.Method,
                    (frac, status) => Dispatcher.Invoke(() =>
                    {
                        Progress.Value = frac;
                        StatusText.Text = $"Распознавание · {status}";
                    }), ct);
            }, ct);

            _currentResult = result;
            TranscriptBox.Text = result.Segments.Count > 0 ? result.TimestampedText : "(речь не распознана)";
            SummaryBox.Text = "";
            SummaryBtn.IsEnabled = result.Segments.Count > 0;

            var rec = new TranscriptionRecord
            {
                SourcePath = path,
                FileName = Path.GetFileName(path),
                DurationSeconds = result.Duration.TotalSeconds,
                ExtractMethod = result.ExtractMethod,
                TimestampedText = result.TimestampedText,
                PlainText = result.PlainText,
                Summary = ""
            };
            _history.Save(rec);
            _currentRecord = rec;
            LoadHistory();
            StatusText.Text = $"Готово · {transcriber.EngineName} · сегментов: {result.Segments.Count} · {FileTranscriptResult.Fmt(result.Duration)}";
        }
        catch (OperationCanceledException) { StatusText.Text = "Отменено."; }
        catch (Exception ex) { ShowError(ex.Message); StatusText.Text = "Ошибка."; }
        finally
        {
            transcriber.Dispose();
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---- AI-саммари ----
    private async void OnGenerateSummary(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // В саммари подаём текст С ТАЙМ-КОДАМИ — чтобы в протоколе были тайм-коды.
        string transcript = _currentResult?.TimestampedText ?? _currentRecord?.TimestampedText ?? "";
        if (string.IsNullOrWhiteSpace(transcript)) { ShowError("Нет транскрипта для саммари."); return; }

        SetBusy(true);
        Progress.IsIndeterminate = true;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = "Генерация протокола через LLM…";
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = _cts.Token;
        try
        {
            string summary = await SummarizeAsync(transcript, ct);
            Progress.IsIndeterminate = false;            // <- баг: бегунок продолжал крутиться
            if (string.IsNullOrWhiteSpace(summary))
            {
                StatusText.Text = "LLM не ответил.";
                App.Current.Notifier?.ShowError("AI-саммари",
                    "LLM недоступен или не ответил. Проверьте, что Ollama запущен (или облачный провайдер доступен): Настройки → LLM.");
            }
            else
            {
                Progress.Value = Progress.Maximum;
                SummaryBox.Text = summary;
                StatusText.Text = "Саммари готово.";
                if (_currentRecord != null)
                {
                    _currentRecord.Summary = summary;
                    _history.Save(_currentRecord);
                    LoadHistory();
                }
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Саммари отменено."; }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка саммари.";
            App.Current.Notifier?.ShowError("AI-саммари", "Не удалось сделать саммари: " + ex.Message);
        }
        finally
        {
            Progress.IsIndeterminate = false;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Составляет ПРОТОКОЛ ВСТРЕЧИ через LLM. Вход — текст с тайм-кодами. Длинный текст —
    /// map-reduce (части → конспекты с тайм-кодами → единый протокол). Качество зависит
    /// от модели LLM: маленькая локальная (3B) даёт грубее, чем 7B/облако.
    /// </summary>
    private async Task<string> SummarizeAsync(string transcript, CancellationToken ct)
    {
        var api = App.Current.ApiClient;
        const int chunkChars = 6000;

        const string protocolSys =
            "Ты составляешь ПРОТОКОЛ ВСТРЕЧИ по транскрибации (реплики с тайм-кодами [мм:сс]). " +
            "Опирайся ТОЛЬКО на текст, ничего не выдумывай. Если имя/название/компания НЕ звучит в " +
            "тексте — НЕ придумывай их, указывай роль (например «интервьюер», «кандидат»). " +
            "Не пересказывай реплики дословно — обобщай по смыслу. Формат ответа (Markdown, по-русски):\n\n" +
            "## Протокол встречи\n" +
            "**Тема:** <определи по содержанию>  \n" +
            "### Участники и роли\n<кто говорит и их роли, если понятно из реплик>\n" +
            "### Ход встречи\n<по темам; каждый пункт начинай с тайм-кода [мм:сс]; сохраняй цифры, названия, факты>\n" +
            "### Договорённости и решения\n<конкретные договорённости>\n" +
            "### Открытые вопросы\n<что осталось нерешённым>\n\n" +
            "Пиши конкретно и по делу, без общих фраз.";

        if (transcript.Length <= chunkChars)
        {
            var r = await api.AskAsync("Составь протокол встречи по транскрибации выше.",
                protocolSys + "\n\nТРАНСКРИБАЦИЯ:\n" + transcript, 1500, new List<ChatMessage>(), ct);
            return (r.Content ?? "").Trim();
        }

        // MAP: из каждого фрагмента вытащить факты, СОХРАНЯЯ тайм-коды.
        var chunks = SplitChars(transcript, chunkChars);
        var partials = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int idx = i;
            Dispatcher.Invoke(() => StatusText.Text = $"Протокол · разбор части {idx + 1}/{chunks.Count}…");
            var r = await api.AskAsync(
                "Выпиши из фрагмента ключевое, СОХРАНЯЯ тайм-коды [мм:сс]: темы, факты, цифры, имена/роли, " +
                "решения и договорённости. Списком, только по тексту, без воды.",
                "Ты обрабатываешь фрагмент транскрибации встречи.\n\n" + chunks[i],
                600, new List<ChatMessage>(), ct);
            if (!string.IsNullOrWhiteSpace(r.Content)) partials.Add(r.Content!.Trim());
        }
        if (partials.Count == 0) return "";

        // REDUCE: собрать протокол из конспектов частей.
        Dispatcher.Invoke(() => StatusText.Text = "Протокол · сведение…");
        var combined = string.Join("\n", partials);
        var final = await api.AskAsync(
            "Собери из этих конспектов частей ЕДИНЫЙ протокол встречи строго по формату выше.",
            protocolSys + "\n\nКОНСПЕКТЫ ЧАСТЕЙ (с тайм-кодами):\n" + combined, 1800, new List<ChatMessage>(), ct);
        return string.IsNullOrWhiteSpace(final.Content) ? combined : final.Content!.Trim();
    }

    private static List<string> SplitChars(string s, int size)
    {
        var list = new List<string>();
        for (int i = 0; i < s.Length; i += size)
            list.Add(s.Substring(i, Math.Min(size, s.Length - i)));
        return list;
    }

    // ---- Копирование / скачивание ----
    private void OnCopyTranscript(object sender, RoutedEventArgs e) => CopyText(TranscriptBox.Text, "Транскрипт скопирован");
    private void OnCopySummary(object sender, RoutedEventArgs e) => CopyText(SummaryBox.Text, "Саммари скопировано");

    private void CopyText(string text, string ok)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); App.Current.Notifier?.ShowInfo("Транскрибация файла", ok); }
        catch { /* буфер иногда занят другим процессом */ }
    }

    private void OnDownloadTranscript(object sender, RoutedEventArgs e) => SaveText(TranscriptBox.Text, "transcript");
    private void OnDownloadSummary(object sender, RoutedEventArgs e) => SaveText(SummaryBox.Text, "summary");

    private void SaveText(string text, string kind)
    {
        if (string.IsNullOrEmpty(text)) { ShowError("Пока нечего сохранять."); return; }
        var baseName = Path.GetFileNameWithoutExtension(_currentRecord?.FileName ?? "file");
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Текстовый файл|*.txt", FileName = $"{baseName}_{kind}.txt" };
        if (dlg.ShowDialog() == true)
        {
            try { File.WriteAllText(dlg.FileName, text); App.Current.Notifier?.ShowInfo("Транскрибация файла", "Файл сохранён"); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }

    // ---- История ----
    private void OnHistorySelect(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        if (HistoryList.SelectedItem is not TranscriptionRecord rec) return;
        _currentRecord = rec;
        _currentResult = null;
        _selectedPath = rec.SourcePath;
        bool exists = File.Exists(rec.SourcePath);
        FileNameText.Text = rec.FileName + (exists ? "" : "  (файл перемещён)");
        FileNameText.Foreground = (Brush)FindResource("TextBrightBrush");
        TranscribeBtn.IsEnabled = exists;
        TranscriptBox.Text = string.IsNullOrEmpty(rec.TimestampedText) ? "(пусто)" : rec.TimestampedText;
        SummaryBox.Text = rec.Summary ?? "";
        SummaryBtn.IsEnabled = !string.IsNullOrWhiteSpace(rec.PlainText);
        ProgressPanel.Visibility = Visibility.Collapsed;
        UpdateFfmpegBanner();
    }

    private void OnDeleteHistory(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not TranscriptionRecord rec) return;
        _history.Delete(rec.Id);
        if (_currentRecord?.Id == rec.Id) _currentRecord = null;
        LoadHistory();
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
        TranscribeBtn.IsEnabled = !busy && !string.IsNullOrEmpty(_selectedPath) && File.Exists(_selectedPath ?? "");
        CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.IsEnabled = !busy;
        if (busy) SummaryBtn.IsEnabled = false;
    }

    private void ShowError(string msg)
    {
        App.Current.Logger?.Warn($"FileTranscription: {msg}", "UI");
        // Ошибку показываем прямо в окне (в теме приложения), без системного win-диалога.
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
