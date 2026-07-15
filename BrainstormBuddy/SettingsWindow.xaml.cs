using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NAudio.Wave;
using NAudio.Utils;
using BrainstormBuddy.Config;
using TabControl = System.Windows.Controls.TabControl;

namespace BrainstormBuddy;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly DispatcherTimer _rmsTimer;
    private bool _apiKeyVisible = true;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        LogPathRun.Text = App.Current.Logger.LogDirectory;

        _rmsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _rmsTimer.Tick += (s, e) =>
        {
            CurrentRmsRun.Text = vm.CurrentRms;
            // В авто-режиме порог живой (калиброванный по шуму) — показываем его, а не ползунок.
            double effThreshold = vm.AutoCalibrateThreshold ? vm.LiveEffectiveThreshold : vm.RmsThreshold;
            RmsThresholdRun.Text = effThreshold.ToString("F3") + (vm.AutoCalibrateThreshold ? " (авто)" : "");
            // Зелёный если есть звук выше порога, серый если тишина
            double rms = 0;
            double.TryParse(vm.CurrentRms, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out rms);
            RmsLiveIndicator.Fill = rms > effThreshold
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x6B, 0x6B));
            // Обновляем дисплей значений слайдеров
            SilenceMsRun.Text = vm.SilenceThresholdMs.ToString("F0");
            MinSpeechMsRun.Text = vm.MinSpeechMsUi.ToString();
            PreRollMsRun.Text = vm.PreRollMsUi.ToString();
            PostRollMsRun.Text = vm.PostRollMsUi.ToString();

            // Живое значение авто-паузы (в авто-режимах контроллер ведёт порог тишины сам —
            // юзер должен видеть, что именно подобрано, в реальном времени).
            if (!vm.IsManualEndpoint)
            {
                AutoPauseLive.Visibility = Visibility.Visible;
                AutoPauseLoopRun.Text = $"{App.Current.AudioBuffer?.CurrentSilenceSeconds ?? 0:F1}с";
                AutoPauseMicRun.Text = App.Current.MicAudioBuffer != null
                    ? $"{App.Current.MicAudioBuffer.CurrentSilenceSeconds:F1}с" : "—";
            }
            else
            {
                AutoPauseLive.Visibility = Visibility.Collapsed;
            }
        };
        _rmsTimer.Start();

        if (vm.AudioPresets.Count > 0 && AudioPresetCombo.Items.Count == 0)
        {
            foreach (var p in vm.AudioPresets)
                AudioPresetCombo.Items.Add(p);
            AudioPresetCombo.SelectedItem = vm.SelectedAudioPreset;
        }

        if (EndpointModeCombo != null && EndpointModeCombo.Items.Count == 0)
        {
            foreach (var m in vm.EndpointModes)
                EndpointModeCombo.Items.Add(m);
            EndpointModeCombo.SelectedItem = vm.SelectedEndpointMode;
        }

        // Populate audio devices
        try
        {
            var devices = BrainstormBuddy.Audio.AudioDeviceEnumerator.GetAvailableDevices();
            var micDevices = devices.Where(d => !d.IsLoopback).ToList();
            var spkDevices = devices.Where(d => d.IsLoopback).ToList();

            MicDeviceCombo.Items.Clear();
            MicDeviceCombo.Items.Add("По умолчанию");
            foreach (var d in micDevices) MicDeviceCombo.Items.Add(d);
            MicDeviceCombo.SelectedIndex = 0;

            SpeakerDeviceCombo.Items.Clear();
            SpeakerDeviceCombo.Items.Add("По умолчанию");
            foreach (var d in spkDevices) SpeakerDeviceCombo.Items.Add(d);
            // Восстанавливаем сохранённый выбор по Id (пусто/пропавшее устройство → «По умолчанию»).
            SpeakerDeviceCombo.SelectedIndex = 0;
            var savedLoopbackId = App.Current.Config.Audio.LoopbackDeviceId;
            if (!string.IsNullOrWhiteSpace(savedLoopbackId))
            {
                var savedIdx = spkDevices.FindIndex(d => d.Id == savedLoopbackId);
                if (savedIdx >= 0) SpeakerDeviceCombo.SelectedIndex = savedIdx + 1; // +1: [0] = «По умолчанию»
            }
        }
        catch { }

        InitLocalSttTab();
        LanguageCombo.SelectedValue = App.Current.Config.Ui.Language;

        // Движок STT: пусто → встроенный GigaAM; старое значение "remote" при включённом
        // Docker показываем как отдельный пункт "docker" (внешний сервер и Docker разделены).
        var savedEng = App.Current.Config.Audio.SttEngine?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(savedEng)) savedEng = "native";
        if (savedEng == "remote" && App.Current.Config.LocalStt.Enabled) savedEng = "docker";
        SttEngineCombo.SelectedValue = savedEng;
        SttAccelCombo.SelectedValue = string.IsNullOrWhiteSpace(App.Current.Config.Audio.SttAccel)
            ? "cpu" : App.Current.Config.Audio.SttAccel.ToLowerInvariant();
        WhisperAccelCombo.SelectedValue = string.IsNullOrWhiteSpace(App.Current.Config.Audio.WhisperAccel)
            ? "auto" : App.Current.Config.Audio.WhisperAccel.ToLowerInvariant();

        // Пикер видеокарты (для GPU-режима)
        try
        {
            SttGpuCombo.Items.Clear();
            foreach (var g in BrainstormBuddy.Native.GpuEnumerator.List())
                SttGpuCombo.Items.Add($"#{g.Index}: {g.Name}");
            if (SttGpuCombo.Items.Count == 0) SttGpuCombo.Items.Add("видеокарты не найдены");
            int dev = App.Current.Config.Audio.SttGpuDevice;
            SttGpuCombo.SelectedIndex = (dev >= 0 && dev < SttGpuCombo.Items.Count) ? dev : 0;
        }
        catch { }
        UpdateGpuPanelVisibility();

        App.Current.Logger.Info($"SettingsWindow opened (ApiKey present: {!string.IsNullOrEmpty(vm.ApiKey)}, devices: {vm.AudioDevices.Count})", "UI");
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var lang = LanguageCombo.SelectedValue as string ?? "ru";
        App.Current.Config.Ui.Language = lang;
        App.Current.ApplyLanguage(lang);
        App.Current.Logger.Info($"Settings: language switched to {lang}", "UI");
    }

    private void OnToggleApiKeyVisibility(object sender, RoutedEventArgs e)
    {
        if (_apiKeyVisible)
        {
            var current = ApiKeyBox.Text;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            var pwd = new System.Windows.Controls.PasswordBox
            {
                Margin = ApiKeyBox.Margin
            };
            pwd.Password = current;
            pwd.PasswordChanged += (s, args) => _vm.ApiKey = pwd.Password;
            ApiKeyBox.SetValue(Grid.ColumnProperty, 0);
            ApiKeyBox.SetValue(Grid.ColumnSpanProperty, 1);
            pwd.SetValue(Grid.ColumnProperty, 0);
            ((System.Windows.Controls.Grid)ApiKeyBox.Parent).Children.Add(pwd);
            _apiKeyVisible = false;
            App.Current.Logger.Debug("Settings: ApiKey hidden (PasswordBox shown)", "UI");
        }
        else
        {
            var parent = (System.Windows.Controls.Grid)ApiKeyBox.Parent;
            var pwd = parent.Children.OfType<System.Windows.Controls.PasswordBox>().FirstOrDefault();
            if (pwd != null)
            {
                ApiKeyBox.Text = pwd.Password;
                parent.Children.Remove(pwd);
            }
            ApiKeyBox.Visibility = Visibility.Visible;
            _apiKeyVisible = true;
            App.Current.Logger.Debug("Settings: ApiKey shown (TextBox)", "UI");
        }
    }

    private async void OnCheckConnection(object sender, RoutedEventArgs e)
    {
        ConnectionStatusText.Text = "Проверяю…";
        App.Current.Logger.Info($"Settings: CheckConnection started, url={_vm.BaseUrl}", "UI");
        try
        {
            // Честная проверка: не только ключ/URL, но и реальный чат-пинг выбранной моделью.
            // Боевые параметры: тот же лимит токенов и системный промпт, что у реальных запросов —
            // иначе reasoning-модель проходит пинг и возвращает пустые ответы в главном окне.
            var cfg = App.Current.Config;
            var (ok, detail) = await _vm.ApiClient!.CheckLlmConnectionAsync(
                cfg.Advanced.MaxResponseTokens, cfg.Advanced.SystemPrompt);
            ConnectionStatusText.Text = ok ? $"✓ {detail}" : $"✗ {detail}";
            ConnectionStatusText.SetResourceReference(ForegroundProperty, ok ? "AccentBrush" : "ErrorBrush");
            App.Current.Logger.Info($"Settings: CheckConnection result = {ok} ({detail})", "UI");
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = $"Ошибка: {ex.Message}";
            ConnectionStatusText.SetResourceReference(ForegroundProperty, "ErrorBrush");
            App.Current.Logger.Error("Settings: CheckConnection threw", ex, "UI");
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info($"Settings: Save clicked", "UI");
        try
        {
            // «Динамик (loopback)» живёт в code-behind (комбо без биндинга): устройство → его Id,
            // «По умолчанию» (строка [0]) → пустая строка = системный дефолт + автоследование.
            // Рестарт захвата при смене Id делает AudioEngine.UpdateConfig (через ApplyLiveConfigChanges).
            _vm.Config.Audio.LoopbackDeviceId =
                SpeakerDeviceCombo.SelectedItem is BrainstormBuddy.Audio.AudioDeviceInfo spk ? spk.Id : string.Empty;

            var loader = new ConfigLoader(App.Current.ConfigPath, App.Current.Logger);
            loader.Save(_vm.Config);
            App.Current.ApplyLiveConfigChanges();
            App.Current.Notifier.ShowInfo("Настройки", "Конфигурация сохранена");
            App.Current.Logger.Info("Settings: saved and applied (window stays open)", "UI");
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: save failed", ex, "UI");
            App.Current.Notifier.ShowError("Ошибка сохранения", ex.Message);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Debug("Settings: Close clicked", "UI");
        Close();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.Current.Logger.LogDirectory;
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                App.Current.Logger.Info($"Settings: opened logs folder: {dir}", "UI");
            }
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: failed to open logs folder", ex, "UI");
        }
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.Current.AppDataDir;
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                App.Current.Logger.Info($"Settings: opened config folder: {dir}", "UI");
            }
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: failed to open config folder", ex, "UI");
        }
    }

    private void OnBrowseSavePath(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Выберите папку для экспорта транскрибаций и протоколов",
                ShowNewFolderButton = true
            };
            var path = _vm.SavePath;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                dialog.SelectedPath = path;
            else
                dialog.SelectedPath = App.Current.AppDataDir;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _vm.SavePath = dialog.SelectedPath;
                App.Current.Logger.Info($"Settings: save path changed to {dialog.SelectedPath}", "UI");
            }
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: browse save path failed", ex, "UI");
        }
    }

    private void OnSaveAsNewPrompt(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            App.Current.Notifier.ShowWarning("Имя пресета", "Введите имя пресета");
            return;
        }
        _vm.SaveAsNewPreset(name);
        App.Current.Notifier.ShowInfo("Пресет сохранён", name);
        App.Current.Logger.Info($"Settings: saved system prompt preset '{name}'", "UI");
    }

    private void OnDeletePrompt(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSystemPromptPreset == null)
        {
            App.Current.Notifier.ShowWarning("Удаление", "Выберите пресет");
            return;
        }
        var name = _vm.SelectedSystemPromptPreset.Name;
        _vm.DeleteSelectedPreset();
        App.Current.Notifier.ShowInfo("Пресет удалён", name);
        App.Current.Logger.Info($"Settings: deleted system prompt preset '{name}'", "UI");
    }

    private void OnNewEmptyPrompt(object sender, RoutedEventArgs e)
    {
        var name = "Новый пресет " + DateTime.Now.ToString("HH:mm");
        var newPreset = new NamedPrompt { Name = name, Content = string.Empty };
        _vm.SystemPromptPresets.Add(newPreset);
        _vm.Config.Advanced.SystemPromptPresets.Add(newPreset);
        _vm.SelectedSystemPromptPreset = newPreset;
        PresetNameBox.Focus();
        PresetNameBox.SelectAll();
        App.Current.Logger.Info($"Settings: created new empty prompt preset '{name}'", "UI");
    }

    protected override void OnClosed(EventArgs e)
    {
        _rmsTimer.Stop();
        StopMicTest();
        App.Current.Logger.Debug("SettingsWindow.OnClosed", "UI");
        base.OnClosed(e);
    }

    // ===================== Тест микрофона =====================
    // Автономный захват (WaveInEvent) с индикатором из 6 полосок. Не зависит от
    // основного пайплайна: юзер может проверить, слышен ли выбранный микрофон.
    private WaveInEvent? _micTest;
    private double _micTestLevel;

    private void OnMicTestToggle(object sender, RoutedEventArgs e)
    {
        if (_micTest != null) { StopMicTest(); return; }
        try
        {
            _micTest = new WaveInEvent
            {
                DeviceNumber = ResolveMicTestDevice(),
                WaveFormat = new WaveFormat(44100, 16, 1),
                BufferMilliseconds = 60
            };
            _micTest.DataAvailable += OnMicTestData;
            _micTest.StartRecording();
            MicTestButton.Content = "Остановить";
            MicTestHint.Text = "Говорите — полоски реагируют на громкость. Если тихо: проверьте выбранный микрофон и его уровень в Windows.";
            App.Current.Logger.Info($"Mic test started: device #{_micTest.DeviceNumber}", "UI");
        }
        catch (Exception ex)
        {
            MicTestHint.Text = "Не удалось открыть микрофон: " + ex.Message;
            _micTest = null;
            App.Current.Logger.Warn($"Mic test failed: {ex.Message}", "UI");
        }
    }

    // MicDeviceCombo: [0]=«По умолчанию» → устройство 0; иначе сопоставляем по имени.
    private int ResolveMicTestDevice()
    {
        if (MicDeviceCombo.SelectedIndex <= 0) return 0;
        var name = MicDeviceCombo.SelectedItem?.ToString() ?? "";
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            if (name.Contains(caps.ProductName, StringComparison.OrdinalIgnoreCase) ||
                caps.ProductName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private void OnMicTestData(object? sender, WaveInEventArgs e)
    {
        long sumSq = 0;
        int n = e.BytesRecorded / 2;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            sumSq += (long)s * s;
        }
        double rms = n > 0 ? Math.Sqrt(sumSq / (double)n) / 32768.0 : 0;
        double level = Math.Min(1.0, rms * 6.0); // речь ~0.02..0.17 RMS → ~0..1
        Dispatcher.BeginInvoke(() => UpdateMicBars(level));
    }

    private void UpdateMicBars(double level)
    {
        if (_micTest == null) return;
        _micTestLevel = _micTestLevel * 0.4 + level * 0.6; // лёгкое сглаживание
        int lit = (int)Math.Round(_micTestLevel * 6);
        for (int i = 0; i < MicLevelBars.Children.Count; i++)
        {
            var bar = (Border)MicLevelBars.Children[i];
            bar.Background = i < lit ? BarBrush(i) : (Brush)FindResource("BorderBrush");
        }
    }

    private static Brush BarBrush(int idx)
    {
        var c = idx < 3 ? Color.FromRgb(0x4A, 0xDE, 0x80)   // зелёный
              : idx < 5 ? Color.FromRgb(0xF0, 0xC6, 0x5F)   // жёлтый
                        : Color.FromRgb(0xFF, 0x5A, 0x6A);  // красный (пик)
        return new SolidColorBrush(c);
    }

    private void StopMicTest()
    {
        try { _micTest?.StopRecording(); _micTest?.Dispose(); } catch { }
        _micTest = null;
        _micTestLevel = 0;
        if (MicTestButton != null) MicTestButton.Content = "Проверить микрофон";
        if (MicLevelBars != null)
            foreach (var child in MicLevelBars.Children)
                ((Border)child).Background = (Brush)FindResource("BorderBrush");
    }

    // ===================== Движок STT + замер скорости =====================
    private void OnSttEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SttEngineCombo.SelectedValue is string v && IsLoaded)
        {
            App.Current.Config.Audio.SttEngine = v;
            // «docker» и «remote» оба работают через remote-движок, но по-разному:
            // docker = локальный контейнер (поднимаем сами, localhost), remote = свой адрес.
            if (v == "docker")
            {
                App.Current.Config.LocalStt.Enabled = true;
                App.Current.Config.LocalStt.AutoStart = true;
                _vm.SttBaseUrl = App.Current.LocalStt.EndpointUrl;
                _vm.SttModel = App.Current.Config.LocalStt.Model;
            }
            else
            {
                // native/whisper/remote — Docker-мост не нужен (для remote адрес вводит пользователь).
                App.Current.Config.LocalStt.Enabled = false;
            }
        }
        UpdateEnginePanels();
    }

    /// <summary>
    /// Показывает настройки ТОЛЬКО выбранного движка (GigaAM / Whisper / Docker) — без «солянки».
    /// Внутри блока: модель скачана → статус + «Удалить» (+ ускорение); нет → только «Скачать».
    /// </summary>
    private void UpdateEnginePanels()
    {
        if (WhisperSettingsPanel == null || GigaamSettingsPanel == null || DockerSettingsPanel == null || RemoteUrlPanel == null) return;
        var eng = (SttEngineCombo.SelectedValue as string) ?? "native";

        GigaamSettingsPanel.Visibility  = eng == "native"  ? Visibility.Visible : Visibility.Collapsed;
        WhisperSettingsPanel.Visibility = eng == "whisper" ? Visibility.Visible : Visibility.Collapsed;
        DockerSettingsPanel.Visibility  = eng == "docker"  ? Visibility.Visible : Visibility.Collapsed;
        RemoteUrlPanel.Visibility       = eng == "remote"  ? Visibility.Visible : Visibility.Collapsed;

        if (eng == "native")
        {
            var gp = App.Current.GetGigaamModelPath();
            bool have = gp != null;
            var appDataModels = Path.Combine(App.Current.AppDataDir, "models");
            bool userDownloaded = have && gp!.StartsWith(appDataModels, StringComparison.OrdinalIgnoreCase);
            GigaamModelStatus.Text = !have
                ? "⚠ Модель GigaAM не найдена — скачайте её."
                : (userDownloaded ? "✓ Модель GigaAM скачана." : "✓ Модель GigaAM входит в поставку.");
            GigaamModelStatus.Foreground = (System.Windows.Media.Brush)FindResource(have ? "SuccessBrush" : "WarnBrush");
            SttDownloadBtn.Visibility = have ? Visibility.Collapsed : Visibility.Visible;
            GigaamDeleteBtn.Visibility = userDownloaded ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (eng == "whisper")
        {
            bool have = App.Current.ResolveWhisperModel() != null;
            WhisperModelStatus.Text = have ? "✓ Модель Whisper скачана." : "⚠ Модель Whisper не скачана — скачайте её.";
            WhisperModelStatus.Foreground = (System.Windows.Media.Brush)FindResource(have ? "SuccessBrush" : "WarnBrush");
            WhisperDownloadBtn.Visibility = have ? Visibility.Collapsed : Visibility.Visible;
            WhisperDeleteBtn.Visibility = have ? Visibility.Visible : Visibility.Collapsed;
            WhisperAccelPanel.Visibility = have ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ===================== Подсветка раздела (по ссылке из плашки оверлея) =====================
    // Открывает нужную вкладку и мигает золотой рамкой вокруг ключевого контрола 3 раза.
    public void HighlightSection(string? target)
    {
        FrameworkElement? el = target == "llm" ? (FrameworkElement)LlmProviderCombo : SttEngineCombo;
        if (el == null) return;
        SelectTabContaining(el);
        UpdateLayout();
        // дать вкладке раскрыться после смены, затем подсветить
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { el.BringIntoView(); FlashElement(el); }
            catch (Exception ex) { App.Current.Logger.Warn($"HighlightSection flash failed: {ex.Message}", "UI"); }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Выбирает вкладку TabControl, содержащую контрол (контент невыбранной вкладки не в визуальном дереве).
    private void SelectTabContaining(FrameworkElement el)
    {
        try
        {
            DependencyObject? d = el;
            while (d != null && d is not TabItem)
                d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);
            if (d is TabItem ti)
            {
                var tc = ItemsControl.ItemsControlFromItemContainer(ti) as TabControl;
                if (tc == null)
                {
                    DependencyObject? p = LogicalTreeHelper.GetParent(ti);
                    while (p != null && p is not TabControl) p = LogicalTreeHelper.GetParent(p);
                    tc = p as TabControl;
                }
                if (tc != null) tc.SelectedItem = ti;
            }
        }
        catch { /* best-effort */ }
    }

    // Золотая рамка-мигание вокруг контрола: 3 пульса, потом исчезает.
    private void FlashElement(FrameworkElement target)
    {
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer == null) return;
        var adorner = new FlashAdorner(target);
        layer.Add(adorner);
        var blink = new DoubleAnimation
        {
            From = 0.2,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(360)),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };
        blink.Completed += (s, e) => { try { layer.Remove(adorner); } catch { /* уже снят */ } };
        adorner.BeginAnimation(UIElement.OpacityProperty, blink);
    }

    // Рисует золотую скруглённую рамку с лёгкой заливкой поверх контрола. Клики не перехватывает.
    private sealed class FlashAdorner : Adorner
    {
        private static readonly System.Windows.Media.Pen GoldPen = MakePen();
        private static readonly System.Windows.Media.Brush GoldFill = MakeFill();
        private static System.Windows.Media.Pen MakePen()
        {
            var p = new System.Windows.Media.Pen(
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)), 3);
            p.Freeze();
            return p;
        }
        private static System.Windows.Media.Brush MakeFill()
        {
            var b = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xC1, 0x07));
            b.Freeze();
            return b;
        }
        public FlashAdorner(UIElement el) : base(el) { IsHitTestVisible = false; }
        protected override void OnRender(System.Windows.Media.DrawingContext dc)
        {
            var sz = AdornedElement.RenderSize;
            var r = new System.Windows.Rect(
                new System.Windows.Point(-5, -5),
                new System.Windows.Size(sz.Width + 10, sz.Height + 10));
            dc.DrawRoundedRectangle(GoldFill, GoldPen, r, 6, 6);
        }
    }

    private void OnWhisperDeleteModel(object sender, RoutedEventArgs e)
    {
        var m = App.Current.ResolveWhisperModel();
        if (m == null) { UpdateEnginePanels(); return; }
        if (MessageBox.Show(this, $"Удалить модель Whisper?\n{Path.GetFileName(m)} (~547 МБ)\nПотом можно скачать заново.",
                "Удаление модели", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Current.DisposeWhisperEngine(); // освободить файл, если модель загружена в память
        try
        {
            File.Delete(m);
            WhisperDownloadStatus.Text = "модель удалена";
            App.Current.Logger.Info($"Whisper model deleted: {m}", "Ai");
        }
        catch (Exception ex) { WhisperDownloadStatus.Text = "занята: " + ex.Message + " — перезапустите приложение"; }
        UpdateEnginePanels();
    }

    private void OnGigaamDeleteModel(object sender, RoutedEventArgs e)
    {
        var modelsDir = Path.Combine(App.Current.AppDataDir, "models");
        var onnx = Path.Combine(modelsDir, "v2_ctc.onnx");
        if (!File.Exists(onnx)) { UpdateEnginePanels(); return; }
        if (MessageBox.Show(this, "Удалить скачанную модель GigaAM?\nv2_ctc.onnx (~930 МБ)\nПотом можно скачать заново.",
                "Удаление модели", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            File.Delete(onnx);
            var labels = Path.Combine(modelsDir, "labels.json");
            if (File.Exists(labels)) File.Delete(labels);
            SttDownloadStatus.Text = "модель удалена";
            App.Current.Logger.Info("GigaAM model deleted", "Ai");
        }
        catch (Exception ex) { SttDownloadStatus.Text = "занята: " + ex.Message + " — перезапустите приложение"; }
        UpdateEnginePanels();
    }

    private void OnWhisperAccelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WhisperAccelCombo.SelectedValue is string v && IsLoaded) App.Current.Config.Audio.WhisperAccel = v;
    }

    private void OnSttAccelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SttAccelCombo.SelectedValue is string v && IsLoaded) App.Current.Config.Audio.SttAccel = v;
        UpdateGpuPanelVisibility();
    }

    // Пикер видеокарты нужен только для GPU-режимов (directml/auto); при CPU — прячем.
    private void UpdateGpuPanelVisibility()
    {
        if (SttGpuPanel == null) return;
        var accel = (SttAccelCombo.SelectedValue as string) ?? "cpu";
        SttGpuPanel.Visibility = accel == "cpu" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSttGpuChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // элемент вида "#1: NVIDIA ..." → берём индекс из SelectedIndex (совпадает с DXGI-порядком)
        if (SttGpuCombo.SelectedIndex >= 0)
            App.Current.Config.Audio.SttGpuDevice = SttGpuCombo.SelectedIndex;
    }

    private void OnShowOnboarding(object sender, RoutedEventArgs e) => App.Current.ShowOnboarding();

    private void OnOpenFileTranscription(object sender, RoutedEventArgs e) => App.Current.ShowFileTranscription();

    // Пресет LLM-провайдера → подставляет BaseUrl (ключ/модель юзер вписывает сам).
    private void OnLlmProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (LlmProviderCombo.SelectedValue is string url && !string.IsNullOrEmpty(url))
        {
            _vm.BaseUrl = url;

            // Модель провайдера ≠ модель другого провайдера. Не даём остаться «чужой» модели,
            // иначе провайдер вернёт 400 «not a valid model ID» (реальный кейс с OpenRouter).
            // Эвристика: у облачных ID есть «/» (tencent/hy3:free, meta/llama-3.3), у локального Ollama — нет.
            bool isLocal = url.Contains("127.0.0.1") || url.Contains("localhost");
            var m = _vm.ChatModel ?? "";
            if (isLocal)
            {
                if (string.IsNullOrWhiteSpace(m) || m.Contains("/"))
                    _vm.ChatModel = "qwen2.5vl:7b"; // рабочая модель по умолчанию для Ollama
            }
            else if (!m.Contains("/"))
            {
                // Локальная модель на облачном провайдере невалидна — очищаем, чтобы юзер вписал свою.
                _vm.ChatModel = "";
                ConnectionStatusText.Text = "↓ Впишите модель этого провайдера в «Chat Model» (пример под полем).";
            }
        }
    }

    // Прогон 3-сек эталона через активный движок → мс + RTF + вердикт.
    private async void OnSttSpeedTest(object sender, RoutedEventArgs e)
    {
        SttSpeedTestBtn.IsEnabled = false;
        SttSpeedResult.Text = "замеряю…";
        try
        {
            const double secs = 3.0;
            var wav = MakeSineWav(secs, MelFrontendSampleRate);
            var engine = App.Current.SttEngine;
            bool isRemote = string.Equals(engine.Name, "remote", System.StringComparison.OrdinalIgnoreCase);

            // Каждый вызов ограничиваем таймаутом: недоступный внешний сервер иначе висит
            // ~15с (3 ретрая) и рисует фиктивный RTF. Встроенным движкам даём запас на прогрев.
            int perCallTimeout = isRemote ? 6 : 60;
            async Task<long> TimedTranscribe()
            {
                using var cts = new CancellationTokenSource(System.TimeSpan.FromSeconds(perCallTimeout));
                var sw = Stopwatch.StartNew();
                await engine.TranscribeAsync(wav, cts.Token);
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }

            // Прогрев (загрузка сессии/графа). Если внешний сервер молчит — честный вердикт сразу.
            try { await TimedTranscribe(); }
            catch (Exception ex) when (isRemote)
            {
                SttSpeedResult.Text = "🔴 сервер распознавания не отвечает — проверьте Docker/STT-сервер или URL ниже";
                App.Current.Logger.Warn($"STT speed test [remote] недоступен: {ex.Message}", "Ai");
                return;
            }

            var times = new List<long>();
            for (int i = 0; i < 3; i++) times.Add(await TimedTranscribe());
            times.Sort();
            long ms = times[times.Count / 2];
            double rtf = ms / (secs * 1000.0);
            string verdict = rtf < 0.3 ? "🟢 быстро" : rtf < 0.7 ? "🟡 нормально" : "🔴 медленно";

            // Движок выбирается при старте. Если в списке выбран другой — предупреждаем про перезапуск.
            string selected = (SttEngineCombo.SelectedValue as string) ?? engine.Name;
            string pending = !string.Equals(selected, engine.Name, System.StringComparison.OrdinalIgnoreCase)
                ? $"  ⚠ выбран «{selected}» — применится после перезапуска" : "";
            SttSpeedResult.Text = $"{engine.Name}: {ms} мс · RTF={rtf:F2} · {verdict}{pending}";
            App.Current.Logger.Info($"STT speed test [{engine.Name}]: {ms}ms RTF={rtf:F2}", "Ai");
        }
        catch (Exception ex)
        {
            SttSpeedResult.Text = "ошибка: " + ex.Message;
        }
        finally { SttSpeedTestBtn.IsEnabled = true; }
    }

    // Скачать встроенную модель (GigaAM ONNX) в %APPDATA%\models. URL/токен — из конфига.
    private async void OnSttDownloadModel(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        if (string.IsNullOrWhiteSpace(cfg.SttModelUrl))
        {
            SttDownloadStatus.Text = "URL модели не задан (config Audio.SttModelUrl)";
            return;
        }
        SttDownloadBtn.IsEnabled = false;
        SttDownloadBar.Visibility = Visibility.Visible;
        SttDownloadBar.Value = 0;
        SttDownloadStatus.Text = "скачиваю…";
        try
        {
            var modelsDir = Path.Combine(App.Current.AppDataDir, "models");
            var modelDest = Path.Combine(modelsDir, "v2_ctc.onnx");
            var labelsDest = Path.Combine(modelsDir, "labels.json");
            int slash = cfg.SttModelUrl.LastIndexOf('/');
            var labelsUrl = slash > 0 ? cfg.SttModelUrl.Substring(0, slash + 1) + "labels.json" : "";

            var dl = new BrainstormBuddy.Stt.ModelDownloader();
            var progress = new Progress<double>(p =>
            {
                if (p >= 0) { SttDownloadBar.Value = p; SttDownloadStatus.Text = $"{p * 100:F0}%"; }
                else SttDownloadStatus.Text = "скачиваю…";
            });

            if (!string.IsNullOrEmpty(labelsUrl))
                await dl.DownloadAsync(labelsUrl, labelsDest, cfg.SttModelAuthHeader, cfg.SttModelAuthValue, 0, null, default);
            await dl.DownloadAsync(cfg.SttModelUrl, modelDest, cfg.SttModelAuthHeader, cfg.SttModelAuthValue,
                                   cfg.SttModelBytes, progress, default);

            SttDownloadStatus.Text = "✅ готово — перезапустите приложение";
            App.Current.Logger.Info($"STT model downloaded → {modelDest}", "Ai");
            UpdateEnginePanels(); // модель появилась → показать «Удалить»
        }
        catch (Exception ex)
        {
            SttDownloadStatus.Text = "ошибка: " + ex.Message;
            App.Current.Logger.Error("STT model download failed", ex, "Ai");
        }
        finally { SttDownloadBtn.IsEnabled = true; }
    }

    private async void OnWhisperDownloadModel(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        if (string.IsNullOrWhiteSpace(cfg.WhisperModelUrl))
        {
            WhisperDownloadStatus.Text = "URL не задан (Audio.WhisperModelUrl)";
            return;
        }
        WhisperDownloadBtn.IsEnabled = false;
        WhisperDownloadBar.Visibility = Visibility.Visible;
        WhisperDownloadBar.Value = 0;
        WhisperDownloadStatus.Text = "скачиваю…";
        try
        {
            var modelsDir = Path.Combine(App.Current.AppDataDir, "models");
            Directory.CreateDirectory(modelsDir);
            var dest = Path.Combine(modelsDir, "ggml-large-v3-turbo-q5_0.bin");
            var dl = new BrainstormBuddy.Stt.ModelDownloader();
            var progress = new Progress<double>(p =>
            {
                if (p >= 0) { WhisperDownloadBar.Value = p; WhisperDownloadStatus.Text = $"{p * 100:F0}%"; }
                else WhisperDownloadStatus.Text = "скачиваю…";
            });
            await dl.DownloadAsync(cfg.WhisperModelUrl, dest, null, null, cfg.WhisperModelBytes, progress, default);
            WhisperDownloadStatus.Text = "✅ готово — применится после перезапуска";
            App.Current.Logger.Info($"Whisper model downloaded → {dest}", "Ai");
            UpdateEnginePanels(); // модель появилась → показать «Удалить» + ускорение
        }
        catch (Exception ex)
        {
            WhisperDownloadStatus.Text = "ошибка: " + ex.Message;
            App.Current.Logger.Error("Whisper model download failed", ex, "Ai");
        }
        finally { WhisperDownloadBtn.IsEnabled = true; }
    }

    private void OnHwCheck(object sender, RoutedEventArgs e)
    {
        try
        {
            var r = BrainstormBuddy.Native.HardwareInfo.Gather();
            HwVerdict.Text = r.Verdict;
            HwVerdict.Foreground = (System.Windows.Media.Brush)FindResource(
                r.Tier == 2 ? "SuccessBrush" : r.Tier == 1 ? "WarnBrush" : "ErrorBrush");
            HwSpecs.Text = $"CPU: {r.CpuName}\n" +
                           $"{r.PhysicalCores} ядер / {r.LogicalCores} потоков  ·  ОЗУ: {r.RamGb} ГБ  ·  " +
                           $"GPU: {r.GpuName} ({(r.HasDiscreteGpu ? "дискретная" : "встроенная")})";
            HwRec.Text = "Рекомендация: " + r.Recommendation;
            HwVerdict.Visibility = HwSpecs.Visibility = HwRec.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            HwVerdict.Text = "Не удалось прочитать характеристики: " + ex.Message;
            HwVerdict.Visibility = Visibility.Visible;
        }
    }

    private const int MelFrontendSampleRate = 16000;

    // WAV в памяти: синус 180 Гц, mono 16 бит. Текст не важен — меряем только время прохода.
    private static byte[] MakeSineWav(double seconds, int sr)
    {
        int n = (int)(seconds * sr);
        var ms = new MemoryStream();
        using (var w = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(sr, 16, 1)))
        {
            var buf = new short[n];
            for (int i = 0; i < n; i++)
                buf[i] = (short)(Math.Sin(2.0 * Math.PI * 180.0 * i / sr) * 8000);
            var bytes = new byte[n * 2];
            Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
            w.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Current.Config.MultiAgent.UserProfile.Summary = _vm.UserProfileSummary;
            App.Current.Config.MultiAgent.UserProfile.CannotDo = _vm.UserProfileCannotDo;
            App.Current.Logger.Info("Profile saved", "UI");
            MessageBox.Show("Профиль сохранён", "BrainstormBuddy", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { App.Current.Logger.Error("Save profile failed", ex, "UI"); }
    }

    private void OnClearLlmLogs(object sender, RoutedEventArgs e)
    {
        _vm.ClearLlmLogs();
    }

    private void OnCopyLlmLogs(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_vm.LlmLogText);
    }

    private void OnCollectSupportLogs(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        try
        {
            if (btn != null) { btn.IsEnabled = false; btn.Content = "Собираю…"; }

            // Убедимся, что последние настройки логирования применены перед сбором.
            var loader = new ConfigLoader(App.Current.ConfigPath, App.Current.Logger);
            loader.Save(_vm.Config);
            App.Current.ApplyLiveConfigChanges();

            var zip = App.Current.CreateSupportBundle();
            App.Current.Logger.Info($"Support bundle ready: {zip}", "UI");
            App.Current.Notifier.ShowInfo("Логи собраны (без истории и токенов)", System.IO.Path.GetFileName(zip));

            // Открываем проводник с выделенным файлом — юзер сразу видит, что сохранилось.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{zip}\"",
                    UseShellExecute = true
                });
            }
            catch { /* открыть проводник — не критично */ }
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Collect support logs failed", ex, "UI");
            App.Current.Notifier.ShowInfo("Не удалось собрать логи", ex.Message);
        }
        finally
        {
            if (btn != null) { btn.IsEnabled = true; btn.Content = "Собрать логи для поддержки"; }
        }
    }

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.Current.Logger.LogDirectory;
            if (System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { App.Current.Logger.Error("Open logs folder failed", ex, "UI"); }
    }
}
