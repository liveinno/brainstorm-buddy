using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Controls;
using BrainstormBuddy.Markdown;
using BrainstormBuddy.Media;
using BrainstormBuddy.Stt;
using BrainstormBuddy.Transcription;

namespace BrainstormBuddy;

/// <summary>
/// Окно локальной транскрибации медиафайлов встроенным GigaAM:
/// извлечение аудио (mp4/webm/…) → распознавание с тайм-кодами → AI-саммари.
/// Поддерживает мульти-выбор: файлы обрабатываются ПОСЛЕДОВАТЕЛЬНО (движок один),
/// каждый — отдельной карточкой со своим текстом и кнопками.
/// История хранится в %APPDATA%\BrainstormBuddy\transcriptions — по записи на файл.
/// </summary>
public partial class FileTranscriptionWindow : Window
{
    private static readonly string[] NeedsFfmpeg = { ".webm", ".ogg", ".opus", ".mkv", ".flac", ".oga" };

    /// <summary>
    /// Дефолтный системный промпт AI-саммари (протокол встречи) — алиас на
    /// <see cref="BrainstormBuddy.Transcription.MeetingSummarizer.DefaultSummaryPrompt"/>:
    /// единый источник дефолта для обоих путей саммари (история встреч + файл).
    /// Пустой Advanced.SummaryPrompt в конфиге → используется этот текст (2.18.0).
    /// </summary>
    public const string DefaultSummaryPrompt = BrainstormBuddy.Transcription.MeetingSummarizer.DefaultSummaryPrompt;

    private readonly TranscriptionHistoryStore _history;
    private readonly MediaAudioExtractor _extractor = new();
    private readonly ObservableCollection<QueueFileItem> _resultItems = new();
    private readonly List<string> _selectedFiles = new();
    /// <summary>Markdown-компоненты в карточках результатов, уже подписанные
    /// на клик по ссылке (подписка идёт по мере появления карточек).</summary>
    private readonly List<MarkdownView> _linkedViews = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public FileTranscriptionWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _resultItems;
        // Карточки результатов создаются шаблоном ItemsControl — контейнеры с
        // markdown-резюме появляются после лейаута, поэтому ссылки в них
        // подписываемся по мере появления (повторы отсекаются).
        ResultsList.LayoutUpdated += (_, _) => HookMarkdownLinks(ResultsList);
        _history = new TranscriptionHistoryStore(App.Current.AppDataDir);
        LoadHistory();
        // По умолчанию — как глобальный движок (если выбран Whisper, окно тоже на Whisper).
        EngineCombo.SelectedValue = (App.Current.Config.Audio.SttEngine?.ToLowerInvariant() == "whisper") ? "whisper" : "gigaam";
        if (EngineCombo.SelectedIndex < 0) EngineCombo.SelectedIndex = 0;
        PopulateHardwareCombo();
    }

    private string EngineChoice() => (EngineCombo?.SelectedValue as string) ?? "gigaam";
    // Локализованная строка UI (ресурсы приложения): ключ → текст текущего языка.
    private string L(string key) => (string)FindResource(key);


    private void OnEngineChanged(object sender, SelectionChangedEventArgs e) => UpdateEngineChip();

    // Список оборудования файловой транскрибации: Авто / CPU / все GPU из системы.
    // Tag = "auto|-1" / "cpu|-1" / "gpu|<dxgi-index>", восстановление — из конфига.
    private void PopulateHardwareCombo()
    {
        HardwareCombo.Items.Clear();
        HardwareCombo.Items.Add(new ComboBoxItem { Content = L("L_FileTr_HwAuto"), Tag = "auto|-1" });
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
            "gpu" => cfg.FileSttGpuDevice >= 0
                ? string.Format(L("L_FileTr_HwGpuFmt"), cfg.FileSttGpuDevice)
                : $"GPU ({L("L_FileTr_HwAutoWord")})",
            _ => L("L_FileTr_HwAutoWord")
        };
    }

    // Фактическая версия GigaAM, с которой будет работать файловая транскрибация:
    // "native" → жёстко v2; "native3"/пусто → v3 при наличии файлов, иначе откат на v2
    // (та же логика, что в App.CreateGigaamEngineForVersion — без загрузки движка).
    private static string GigaamVersionTag()
    {
        var eng = App.Current.Config.Audio.SttEngine?.ToLowerInvariant();
        return (eng != "native" && App.Current.HasGigaamV3Model()) ? "v3" : "v2";
    }

    private void UpdateEngineChip()
    {
        if (EngineText == null) return;
        string ver = GigaamVersionTag();
        // Подпись в комбо следует за фактической версией — меняем текст айтема, Tag не трогаем.
        if (GigaamItem != null) GigaamItem.Content = string.Format(L("L_FileTr_GigaamItemFmt"), ver);
        bool ff = MediaAudioExtractor.TryFindFfmpeg(out _);
        string ffNote = ff ? "ffmpeg ✓" : L("L_FileTr_FfmpegNeed");
        if (EngineChoice() == "whisper")
        {
            bool have = App.Current.ResolveWhisperModel() != null;
            EngineText.Text = (have ? L("L_FileTr_WhisperReady") : L("L_FileTr_WhisperNoModel"))
                              + $" · {HardwareNote()} · " + ffNote;
        }
        else
        {
            bool haveModel = ver == "v3" || App.Current.GetGigaamModelPath() != null;
            EngineText.Text = (haveModel ? string.Format(L("L_FileTr_GigaamReadyFmt"), ver)
                                         : L("L_FileTr_GigaamNoModel"))
                              + $" · {HardwareNote()} · " + ffNote;
        }
    }

    private void LoadHistory() => HistoryList.ItemsSource = _history.LoadAll();

    // ---- Выбор файлов (мульти-выбор → очередь) ----
    private void OnSelectFile(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("L_FileTr_OpenDlgTitle"),
            Multiselect = true,
            Filter = L("L_FileTr_OpenDlgFilter")
        };
        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;

        // Выбор новой пачки заменяет список результатов (как раньше заменялся результат одного файла).
        _resultItems.Clear();
        _selectedFiles.Clear();
        foreach (var p in dlg.FileNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            _selectedFiles.Add(p);
            _resultItems.Add(new QueueFileItem(p));
        }

        FileNameText.Text = dlg.FileNames.Length == 1
            ? Path.GetFileName(dlg.FileNames[0])
            : string.Format(L("L_FileTr_SelectedCountFmt"), dlg.FileNames.Length);
        FileNameText.Foreground = (Brush)FindResource("TextBrightBrush");
        ProgressPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
        ResultsHint.Visibility = Visibility.Collapsed;
        HistoryList.SelectedItem = null;
        UpdateStartButton();
        UpdateFfmpegBanner();
        App.Current.Logger.Info($"FileTranscription: выбрано файлов: {dlg.FileNames.Length}", "UI");
    }

    // «Старт» активен, пока в списке есть файлы, ждущие очереди (в т.ч. после частичной
    // отмены — можно доиграть остаток), либо когда на экране карточка из истории,
    // чей файл ещё на месте — её можно распознать заново.
    private void UpdateStartButton() =>
        TranscribeBtn.IsEnabled = !_busy
            && _resultItems.Any(i => i.Status == QueueFileStatus.Queued
                                     || (i.FromHistory && File.Exists(i.FilePath)));

    // Показывает жёлтую плашку, если хоть один выбранный файл требует ffmpeg, а он не найден.
    private void UpdateFfmpegBanner()
    {
        bool need = _selectedFiles.Any(p => NeedsFfmpeg.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    && !MediaAudioExtractor.TryFindFfmpeg(out _);
        FfmpegBanner.Visibility = need ? Visibility.Visible : Visibility.Collapsed;
    }

    // «Новый файл»: полный сброс — очередь, карточки и выбранные файлы.
    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _resultItems.Clear();
        _selectedFiles.Clear();
        FileNameText.Text = L("L_FileTr_NoFiles");
        FileNameText.Foreground = (Brush)FindResource("TextMutedBrush");
        ProgressPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
        ResultsHint.Visibility = Visibility.Visible;
        HistoryList.SelectedItem = null;
        UpdateStartButton();
        UpdateFfmpegBanner();
        App.Current.Logger.Info("FileTranscription: сброс состояния (кнопка «Новый файл»)", "UI");
    }

    private async void OnDownloadFfmpeg(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        FfmpegDownloadBtn.IsEnabled = false;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = L("L_FileTr_FfmpegDownloading");
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = _cts.Token;
        try
        {
            var installer = new FfmpegInstaller();
            var progress = new Progress<double>(f => Dispatcher.Invoke(() =>
            {
                Progress.Value = f;
                StatusText.Text = string.Format(L("L_FileTr_FfmpegDownloadingFmt"), f * 100);
            }));
            await installer.InstallAsync(MediaAudioExtractor.DownloadedFfmpegDir, progress, ct);
            StatusText.Text = L("L_FileTr_FfmpegDone");
            FfmpegBanner.Visibility = Visibility.Collapsed;
            UpdateEngineChip();
            App.Current.Notifier?.ShowInfo(L("L_FileTr_ToastTitle"), L("L_FileTr_FfmpegDoneShort"));
        }
        catch (OperationCanceledException) { StatusText.Text = L("L_FileTr_FfmpegCanceled"); }
        catch (Exception ex)
        {
            StatusText.Text = L("L_FileTr_FfmpegFailed");
            ShowError(string.Format(L("L_FileTr_FfmpegFailedFmt"), ex.Message, MediaAudioExtractor.DownloadedFfmpegDir));
        }
        finally
        {
            FfmpegDownloadBtn.IsEnabled = true;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---- Транскрибация: ПОСЛЕДОВАТЕЛЬНАЯ очередь (модель одна → файлы по одному) ----
    private async void OnTranscribe(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // Что запускать: все карточки «в очереди» (свежая пачка или остаток после отмены),
        // плюс карточка из истории — единственная, у которой статус Done до старта.
        var batch = _resultItems.Where(i => i.Status == QueueFileStatus.Queued || i.FromHistory).ToList();
        if (batch.Count == 0) { ShowError(L("L_FileTr_SelectFirst")); return; }
        if (batch.All(i => !File.Exists(i.FilePath))) { ShowError(L("L_FileTr_FilesNotFound")); return; }

        var choice = EngineChoice();
        var transcriber = App.Current.ResolveFileTranscriber(choice, out var err);
        if (transcriber == null)
        {
            if (choice == "whisper")
            {
                UpdateEngineChip();
                App.Current.Notifier?.ShowError("Whisper", err ?? L("L_FileTr_WhisperNoModelToast"));
            }
            else ShowError(err ?? L("L_FileTr_EngineUnavailable"));
            return;
        }

        SetBusy(true);
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        bool cancelled = false;
        int done = 0, failed = 0;
        try
        {
            // Движок резолвится ОДИН раз на всю очередь (модель грузится один раз),
            // файлы идут строго по очереди — параллельно нельзя.
            for (int i = 0; i < batch.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = batch[i];
                var path = item.FilePath;
                item.Status = QueueFileStatus.Active;
                item.ProgressText = choice == "whisper"
                    ? L("L_FileTr_ExtractingWhisper")
                    : L("L_FileTr_Extracting");
                StatusText.Text = string.Format(L("L_FileTr_FileOfFmt"), i + 1, batch.Count, item.FileName);
                try
                {
                    if (!File.Exists(path))
                        throw new FileNotFoundException(L("L_FileTr_FileNotFound"), path);

                    var result = await Task.Run(() =>
                    {
                        var audio = _extractor.Extract(path, ct);
                        Dispatcher.Invoke(() => item.ProgressText = L("L_FileTr_Recognizing"));
                        return transcriber.Transcribe(audio.Samples, audio.Duration, audio.Method,
                            (frac, status) => Dispatcher.Invoke(() =>
                            {
                                item.ProgressText = string.Format(L("L_FileTr_RecognizingFmt"), status);
                                // Общий прогресс = доля готовых файлов + доля текущего.
                                Progress.Value = Math.Min(1.0, (i + frac) / batch.Count);
                            }), ct);
                    }, ct);

                    item.TranscriptText = result.Segments.Count > 0 ? result.TimestampedText : L("L_FileTr_NoSpeech");
                    item.HasTranscript = result.Segments.Count > 0;
                    item.Status = QueueFileStatus.Done;
                    item.ProgressText = string.Format(L("L_FileTr_DoneFmt"), result.Segments.Count, FileTranscriptResult.Fmt(result.Duration));

                    // Каждый файл — отдельная запись истории (своё саммари потом допишется в неё же).
                    var rec = new TranscriptionRecord
                    {
                        SourcePath = path,
                        FileName = item.FileName,
                        DurationSeconds = result.Duration.TotalSeconds,
                        ExtractMethod = result.ExtractMethod,
                        TimestampedText = result.TimestampedText,
                        PlainText = result.PlainText,
                        Summary = ""
                    };
                    _history.Save(rec);
                    item.Record = rec;
                    item.FromHistory = false;
                    LoadHistory(); // файл появляется в истории сразу по готовности
                    done++;
                }
                catch (OperationCanceledException)
                {
                    item.Status = QueueFileStatus.Queued;
                    item.ProgressText = L("L_FileTr_Stopped");
                    throw; // останавливает всю очередь
                }
                catch (Exception ex)
                {
                    // Ошибка одного файла НЕ роняет очередь — идём к следующему.
                    item.Status = QueueFileStatus.Error;
                    item.ProgressText = "⚠ " + ex.Message;
                    failed++;
                    App.Current.Logger.Warn($"FileTranscription: '{item.FileName}': {ex.Message}", "UI");
                }
                Progress.Value = (double)(i + 1) / batch.Count;
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            string engineName;
            try { engineName = transcriber.EngineName; } catch { engineName = ""; }
            transcriber.Dispose();
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            Progress.IsIndeterminate = false;
            int total = done + failed;
            StatusText.Text = cancelled
                ? string.Format(L("L_FileTr_QueueStoppedFmt"), done, failed, batch.Count - total)
                : string.Format(L("L_FileTr_QueueDoneFmt"), done, batch.Count)
                  + (failed > 0 ? string.Format(L("L_FileTr_ErrorsSuffixFmt"), failed) : "")
                  + (string.IsNullOrEmpty(engineName) ? "" : $" · {engineName}");
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---- AI-саммари (на карточке конкретного файла) ----
    private async void OnItemSummary(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement fe || fe.DataContext is not QueueFileItem item) return;
        // В саммари подаём текст С ТАЙМ-КОДАМИ — чтобы в протоколе были тайм-коды.
        string transcript = item.TranscriptText;
        if (string.IsNullOrWhiteSpace(transcript) || !item.HasTranscript)
        {
            item.ProgressText = L("L_FileTr_NoTranscriptForSummary");
            return;
        }

        SetBusy(true);
        item.SummaryBusy = true;
        Progress.IsIndeterminate = true;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = string.Format(L("L_FileTr_SummaryGeneratingFmt"), item.FileName);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        item.SummaryCts = _cts;
        var ct = _cts.Token;
        // Причина провала от сервиса (§5): «LLM не ответил» без причины не отличает
        // исчерпанный бюджет/тишину модели от сетевой ошибки.
        string? failure = null;
        try
        {
            string summary = await SummarizeAsync(item, transcript, ct, reason => failure = reason);
            Progress.IsIndeterminate = false;            // <- баг: бегунок продолжал крутиться
            if (string.IsNullOrWhiteSpace(summary))
            {
                StatusText.Text = L("L_FileTr_LlmNoAnswer");
                item.ProgressText = string.IsNullOrWhiteSpace(failure)
                    ? L("L_FileTr_SummaryNoResult")
                    : string.Format(L("L_FileTr_SummaryErrorFmt"), failure);
                App.Current.Notifier?.ShowError(L("L_FileTr_AiSummaryTitle"),
                    string.IsNullOrWhiteSpace(failure)
                        ? L("L_FileTr_LlmUnavailable")
                        : string.Format(L("L_FileTr_SummaryFailedFmt"), failure));
            }
            else
            {
                Progress.Value = Progress.Maximum;
                item.SummaryText = summary;
                item.ProgressText = L("L_FileTr_SummaryDone");
                StatusText.Text = string.Format(L("L_FileTr_SummaryDoneFileFmt"), item.FileName);
                if (item.Record != null)
                {
                    item.Record.Summary = summary;
                    item.Record.SummaryCheckpointJson = null;
                    await Task.Run(() => _history.Save(item.Record));
                    LoadHistory();
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("L_FileTr_SummaryCanceled");
            item.ProgressText = L("L_FileTr_SummaryCanceled");
        }
        catch (Exception ex)
        {
            StatusText.Text = L("L_FileTr_SummaryError");
            item.ProgressText = string.Format(L("L_FileTr_SummaryErrorFmt"), ex.Message);
            App.Current.Notifier?.ShowError(L("L_FileTr_AiSummaryTitle"), string.Format(L("L_FileTr_SummaryFailedFmt"), ex.Message));
        }
        finally
        {
            Progress.IsIndeterminate = false;
            item.SummaryCts = null;
            item.SummaryBusy = false;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnItemSummaryCancel(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QueueFileItem item)
            item.SummaryCts?.Cancel();
    }

    // ---- Извлечение фактов транскрипта в долговременную память ----
    private void OnItemExtractToMemory(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not QueueFileItem item) return;
        var transcript = item.TranscriptText;
        if (string.IsNullOrWhiteSpace(transcript) || !item.HasTranscript)
        {
            item.ProgressText = L("L_FileTr_TranscribeFirst");
            App.Current.Notifier?.ShowInfo(L("L_FileTr_ExtractToastTitle"), L("L_FileTr_TranscribeFirstShort"));
            return;
        }
        var dlg = new TranscriptExtractDialog(transcript) { Owner = this };
        dlg.ShowDialog();
    }

    /// <summary>
    /// Составляет ПРОТОКОЛ ВСТРЕЧИ через LLM: тонкая адаптация общего
    /// <see cref="BrainstormBuddy.Transcription.MeetingSummarizer"/> к UI файловой
    /// транскрибации (статус, расход токенов, причина ошибки). Вход — текст с
    /// тайм-кодами; длинный текст сервис сам режет на MAP-части и сводит REDUCE.
    /// </summary>
    private Task<string> SummarizeAsync(QueueFileItem item, string transcript, CancellationToken ct,
        Action<string>? onFailure) =>
        // Вся логика протокола — в общем сервисе: MAP-бюджеты с расчётом на
        // reasoning, ограниченные продолжения, сохранение успешных частей и
        // запрет «успешного» протокола с молча пропущенной частью. Местная копия
        // с бюджетом 600 теряла части длинной встречи молча (§5).
        BrainstormBuddy.Transcription.MeetingSummarizer.SummarizeAsync(
            App.Current.ApiClient, transcript,
            s => Dispatcher.InvokeAsync(() =>
            {
                if (!_busy) return;
                StatusText.Text = s;
                item.ProgressText = s;
            }),
            ct, App.Current.Config.Advanced.SummaryPrompt,
            onPart: (done, total) => Dispatcher.InvokeAsync(() =>
            {
                Progress.IsIndeterminate = total <= 1;
                Progress.Maximum = total;
                Progress.Value = Math.Min(done, total);
            }),
            onUsage: r => App.Current.AccountBackgroundUsage(r),
            onFailure: onFailure,
            checkpointJson: item.Record?.SummaryCheckpointJson,
            onCheckpoint: checkpoint => Task.Run(() =>
            {
                if (item.Record is not { } record) return;
                record.SummaryCheckpointJson = checkpoint;
                _history.Save(record);
            }));

    // ---- Копирование / скачивание (на карточке конкретного файла) ----
    private void OnItemCopyTranscript(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QueueFileItem item)
            CopyText(item.TranscriptText, L("L_FileTr_TranscriptCopied"));
    }

    private void OnItemCopySummary(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QueueFileItem item)
            // Копируем ВИДИМЫЙ текст резюме (без markdown-маркеров).
            CopyText(PlainText(item.SummaryText), L("L_FileTr_SummaryCopied"));
    }

    /// <summary>Видимый текст без разметки: то, что пользователь видит и
    /// копирует из карточки резюме. Разбор не бросает исключений, при
    /// неудаче отдаём исходный текст.</summary>
    private static string PlainText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        try { return MarkdownSupport.ToPlainText(MarkdownSupport.Parse(raw)); }
        catch { return raw; }
    }

    /// <summary>Подписать markdown-компоненты в дереве на клик по ссылке
    /// (только безопасные схемы http/https/mailto).</summary>
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

    private void CopyText(string text, string ok)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); App.Current.Notifier?.ShowInfo(L("L_FileTr_ToastTitle"), ok); }
        catch { /* буфер иногда занят другим процессом */ }
    }

    private void OnItemSaveTranscript(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QueueFileItem item)
            SaveText(item.TranscriptText, item.FileName, "transcript");
    }

    private void OnItemSaveSummary(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QueueFileItem item)
            SaveText(item.SummaryText, item.FileName, "summary");
    }

    private void SaveText(string text, string baseName, string kind)
    {
        if (string.IsNullOrEmpty(text)) { ShowError(L("L_FileTr_NothingToSave")); return; }
        var name = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(baseName) ? "file" : baseName);
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = L("L_FileTr_SaveDlgFilter"), FileName = $"{name}_{kind}.txt" };
        if (dlg.ShowDialog() == true)
        {
            try { File.WriteAllText(dlg.FileName, text); App.Current.Notifier?.ShowInfo(L("L_FileTr_ToastTitle"), L("L_FileTr_FileSaved")); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }

    // ---- История ----
    private void OnHistorySelect(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        if (HistoryList.SelectedItem is not TranscriptionRecord rec) return;

        // Запись истории показываем той же карточкой результата — одна на файл.
        _resultItems.Clear();
        _selectedFiles.Clear();
        bool exists = File.Exists(rec.SourcePath);
        if (exists) _selectedFiles.Add(rec.SourcePath);
        var item = new QueueFileItem(rec.SourcePath)
        {
            FileName = rec.FileName,
            Status = QueueFileStatus.Done,
            FromHistory = true,
            Record = rec,
            TranscriptText = string.IsNullOrEmpty(rec.TimestampedText) ? L("L_FileTr_EmptyTranscript") : rec.TimestampedText,
            HasTranscript = !string.IsNullOrWhiteSpace(rec.PlainText),
            SummaryText = rec.Summary ?? "",
            ProgressText = string.Format(L("L_FileTr_FromHistoryFmt"), rec.CreatedAt, FileTranscriptResult.Fmt(TimeSpan.FromSeconds(rec.DurationSeconds)))
                           + (exists ? "" : L("L_FileTr_FileMovedSuffix"))
        };
        _resultItems.Add(item);

        FileNameText.Text = rec.FileName + (exists ? "" : L("L_FileTr_FileMovedParen"));
        FileNameText.Foreground = (Brush)FindResource("TextBrightBrush");
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultsHint.Visibility = Visibility.Collapsed;
        UpdateStartButton();
        UpdateFfmpegBanner();
    }

    private void OnDeleteHistory(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (HistoryList.SelectedItem is not TranscriptionRecord rec) return;
        _history.Delete(rec.Id);
        // Карточка этого файла на экране больше не привязана к записи — иначе
        // саммари на ней «воскресило» бы удалённый файл истории.
        foreach (var item in _resultItems)
            if (item.Record?.Id == rec.Id) item.Record = null;
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
        SelectFileBtn.IsEnabled = !busy;
        ResetBtn.IsEnabled = !busy;
        CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.IsEnabled = !busy;
        UpdateStartButton();
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
        try { foreach (var i in _resultItems) i.SummaryCts?.Cancel(); } catch { /* уже закрыто */ }
        base.OnClosed(e);
    }
}

/// <summary>Состояние карточки файла в очереди транскрибации.</summary>
public enum QueueFileStatus { Queued, Active, Done, Error }

/// <summary>
/// Элемент списка результатов: один файл очереди (или запись истории).
/// Хранит статус, транскрипт и саммари конкретного файла — карточки независимы.
/// </summary>
public sealed class QueueFileItem : INotifyPropertyChanged
{
    public QueueFileItem(string path)
    {
        FilePath = path;
        FileName = Path.GetFileName(path);
    }

    /// <summary>Полный путь к исходному файлу.</summary>
    public string FilePath { get; }
    /// <summary>Отображаемое имя (для истории — из записи, могло отличаться от пути).</summary>
    public string FileName { get; set; } = "";
    /// <summary>Карточка показывает запись истории (а не свежий запуск очереди).</summary>
    public bool FromHistory { get; set; }
    /// <summary>Запись в истории транскрибаций (null, если ещё не сохранялась).</summary>
    public TranscriptionRecord? Record { get; set; }
    /// <summary>CTS именно саммари этой карточки (отдельная кнопка «Остановить саммари»).</summary>
    public CancellationTokenSource? SummaryCts { get; set; }

    private QueueFileStatus _status = QueueFileStatus.Queued;
    public QueueFileStatus Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; Npc(); Npc(nameof(StatusLabel)); }
    }

    public string StatusLabel => Status switch
    {
        QueueFileStatus.Active => App.Current.T("L_FileTr_StatusActive"),
        QueueFileStatus.Done => App.Current.T("L_FileTr_StatusDone"),
        QueueFileStatus.Error => App.Current.T("L_FileTr_StatusError"),
        _ => App.Current.T("L_FileTr_StatusQueued")
    };

    private string _progressText = App.Current.T("L_FileTr_QueuedText");
    /// <summary>Строка состояния под именем: прогресс / итог / ошибка.</summary>
    public string ProgressText
    {
        get => _progressText;
        set { if (_progressText == value) return; _progressText = value; Npc(); }
    }

    private string _transcriptText = "";
    public string TranscriptText
    {
        get => _transcriptText;
        set { if (_transcriptText == value) return; _transcriptText = value; Npc(); }
    }

    private bool _hasTranscript;
    /// <summary>Есть ли осмысленный транскрипт (для кнопки «Сгенерировать саммари»).</summary>
    public bool HasTranscript
    {
        get => _hasTranscript;
        set { if (_hasTranscript == value) return; _hasTranscript = value; Npc(); Npc(nameof(CanGenerateSummary)); }
    }

    private string _summaryText = "";
    public string SummaryText
    {
        get => _summaryText;
        set { if (_summaryText == value) return; _summaryText = value; Npc(); Npc(nameof(HasSummary)); }
    }

    public bool HasSummary => !string.IsNullOrWhiteSpace(SummaryText);

    private bool _summaryBusy;
    /// <summary>Идёт генерация саммари именно этого файла.</summary>
    public bool SummaryBusy
    {
        get => _summaryBusy;
        set { if (_summaryBusy == value) return; _summaryBusy = value; Npc(); Npc(nameof(CanGenerateSummary)); }
    }

    public bool CanGenerateSummary => HasTranscript && !SummaryBusy;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Npc([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Адаптивный порог ширины (волна 04): ActualWidth (double, DIP) → bool «ширина ≥ порога».
/// ConverterParameter — порог в DIP (например, "760"/"680"), парсится инвариантно.
/// Используется DataTrigger'ами XAML для перекладки панели движка и строки команд.
/// </summary>
public sealed class FtWidthThresholdConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double threshold = parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0;
        return value is double w && w >= threshold;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// Ширина колонки истории (волна 04): 256 DIP при клиентской ширине окна ≥1000, иначе 224.
/// Биндится к RootGrid.ActualWidth; ConverterParameter — суммарный горизонтальный Margin
/// корневой сетки (20+20=40): ActualWidth+Margin = клиентская область окна в DIP
/// (та же методика, что в MeetingHistoryWindow — консистентность порогов между окнами).
/// Возвращает double для биндинга к FrameworkElement.Width. Минимум окна не трогает.
/// </summary>
public sealed class FtHistoryColumnWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double extra = parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : 0;
        return value is double w && w + extra >= 1000 ? 256.0 : 224.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
