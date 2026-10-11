using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using NAudio.Wave;
using NAudio.Utils;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Ocr;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Memory.Embeddings;
using BrainstormBuddy.Transcription;
using TabControl = System.Windows.Controls.TabControl;

namespace BrainstormBuddy;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly DispatcherTimer _rmsTimer;
    private bool _apiKeyVisible;
    // Счётчик версий статусов «Сохранено»: таймер прячет только СВОЁ сообщение
    // (свежая ошибка/успех не должна пропадать по чужому таймеру).
    private int _saveStatusGen;
    private MeetingTranscriptStore? _meetingStore;

    // Журнал встреч для вкладки «История»: живой стор приложения, если он уже создан
    // (тогда Delete корректно сбрасывает текущую «живую» встречу), иначе свой экземпляр
    // на том же appData-каталоге, что и App/MeetingHistoryWindow.
    private MeetingTranscriptStore MeetingStore =>
        _meetingStore ??= App.Current.MeetingJournal ?? new MeetingTranscriptStore(App.Current.AppDataDir);

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        LogPathRun.Text = App.Current.Logger.LogDirectory;
        var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionRun.Text = $" {asmVer?.Major}.{asmVer?.Minor}.{asmVer?.Build}";
        TitleVersionRun.Text = $"BrainstormBuddy v{asmVer?.Major}.{asmVer?.Minor}.{asmVer?.Build}";

        _rmsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _rmsTimer.Tick += (s, e) =>
        {
            CurrentRmsRun.Text = vm.CurrentRms;
            // В авто-режиме порог живой (калиброванный по шуму) — показываем его, а не ползунок.
            double effThreshold = vm.AutoCalibrateThreshold ? vm.LiveEffectiveThreshold : vm.RmsThreshold;
            RmsThresholdRun.Text = effThreshold.ToString("F3") + (vm.AutoCalibrateThreshold ? (string)FindResource("L_Set_SuffixAuto") : "");
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
                AutoPauseLoopRun.Text = $"{App.Current.AudioBuffer?.CurrentSilenceSeconds ?? 0:F1}{(string)FindResource("L_Set_SecShort")}";
                AutoPauseMicRun.Text = App.Current.MicAudioBuffer != null
                    ? $"{App.Current.MicAudioBuffer.CurrentSilenceSeconds:F1}{(string)FindResource("L_Set_SecShort")}" : "—";
            }
            else
            {
                AutoPauseLive.Visibility = Visibility.Collapsed;
            }
        };
        _rmsTimer.Start();



        // Populate audio devices
        try
        {
            var devices = BrainstormBuddy.Audio.AudioDeviceEnumerator.GetAvailableDevices();
            var micDevices = devices.Where(d => !d.IsLoopback).ToList();
            var spkDevices = devices.Where(d => d.IsLoopback).ToList();

            MicDeviceCombo.Items.Clear();
            MicDeviceCombo.Items.Add((string)FindResource("L_SetAudio_DefaultDevice"));
            foreach (var d in micDevices) MicDeviceCombo.Items.Add(d);
            MicDeviceCombo.SelectedIndex = 0;

            SpeakerDeviceCombo.Items.Clear();
            SpeakerDeviceCombo.Items.Add((string)FindResource("L_SetAudio_DefaultDevice"));
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

        // Статический текст при открытии окна на EN-конфиге (SelectedValue выше не отстрелит
        // OnLanguageChanged до IsLoaded — язык для статики применяем явно).
        ApplyStaticStrings(App.Current.Config.Ui.Language);
        // Подсказка-плейсхолдер: в пустом поле промпта саммари показываем дефолт в тултипе.
        SummaryPromptBox.ToolTip = FileTranscriptionWindow.DefaultSummaryPrompt;

        // Движок STT: пусто → встроенный GigaAM; старое значение "remote" при включённом
        var savedEng = App.Current.Config.Audio.SttEngine?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(savedEng)) savedEng = "native3";
        if (savedEng == "remote" && App.Current.Config.LocalStt.Enabled) savedEng = "docker";
        SttEngineCombo.SelectedValue = savedEng;
        SttAccelCombo.SelectedValue = string.IsNullOrWhiteSpace(App.Current.Config.Audio.SttAccel)
            ? "cpu" : App.Current.Config.Audio.SttAccel.ToLowerInvariant();
        WhisperAccelCombo.SelectedValue = string.IsNullOrWhiteSpace(App.Current.Config.Audio.WhisperAccel)
            ? "auto" : App.Current.Config.Audio.WhisperAccel.ToLowerInvariant();

        // Пикеры видеокарты (для GPU-режимов GigaAM и Whisper)
        try
        {
            SttGpuCombo.Items.Clear();
            WhisperGpuCombo.Items.Clear();
            foreach (var g in BrainstormBuddy.Native.GpuEnumerator.List())
            {
                var item = $"#{g.Index}: {g.Name}";
                SttGpuCombo.Items.Add(item);
                WhisperGpuCombo.Items.Add(item);
            }
            if (SttGpuCombo.Items.Count == 0) SttGpuCombo.Items.Add((string)FindResource("L_SetStt_NoGpu"));
            if (WhisperGpuCombo.Items.Count == 0) WhisperGpuCombo.Items.Add((string)FindResource("L_SetStt_NoGpu"));
            int dev = App.Current.Config.Audio.SttGpuDevice;
            SttGpuCombo.SelectedIndex = (dev >= 0 && dev < SttGpuCombo.Items.Count) ? dev : 0;
            // -1 = авто → дискретная карта при наличии, иначе первый адаптер.
            int wdev = App.Current.Config.Audio.WhisperGpuDevice;
            int wdisc = BrainstormBuddy.Native.GpuEnumerator.BestDiscreteIndex();
            int wsel = (wdev >= 0 && wdev < WhisperGpuCombo.Items.Count) ? wdev
                : (wdisc >= 0 && wdisc < WhisperGpuCombo.Items.Count) ? wdisc : 0;
            WhisperGpuCombo.SelectedIndex = WhisperGpuCombo.Items.Count > 0 ? wsel : -1;
        }
        catch { }
        UpdateGpuPanelVisibility();
        UpdateWhisperGpuPanelVisibility();

        // Лимит токенов ответа (VM клампит 50..4096 — показываем уже клампнутое).
        MaxTokensBox.Text = _vm.MaxResponseTokens.ToString();

        // Провайдер LLM: профиль по сохранённому ProviderId (или угадывание по BaseUrl для старых конфигов).
        InitProviderSelection();

        // Pressed-арт дизайн-набора BuddySettings: своп иконки по Button.IsPressed.
        WireSettingsIcons();

        // Ключ скрыт по умолчанию (волна 06): PasswordBox поверх TextBox,
        // секрет передаётся между полями без потерь.
        SetApiKeyVisible(false);

        App.Current.Logger.Info($"SettingsWindow opened (ApiKey present: {!string.IsNullOrEmpty(vm.ApiKey)}, devices: {vm.AudioDevices.Count})", "UI");
    }

    /// <summary>
    /// Окно тяжёлое (тысячи контролов, перечисление устройств, WMI) — раньше пересоздавалось
    /// на каждое открытие и давало 3-5с фриз. Теперь «закрытие» = Hide(), экземпляр
    /// переиспользуется App.OpenSettings (App прогревает его на старте). Реальный Close
    /// допускаем только на выходе приложения, иначе окно отказывается умирать.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.Current.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // Берём Tag из AddedItems, а не SelectedValue: при SelectedValuePath без биндинга
        // SelectedValue внутри SelectionChanged может вернуть прежнее значение
        // (тот же класс бага, что _appliedProviderId у LlmProviderCombo).
        var item = e.AddedItems.Count > 0 ? e.AddedItems[0] as ComboBoxItem : LanguageCombo.SelectedItem as ComboBoxItem;
        var lang = item?.Tag as string ?? LanguageCombo.SelectedValue as string ?? "ru";
        App.Current.Config.Ui.Language = lang;
        App.Current.ApplyLanguage(lang);
        // Статический текст окна (сайдбар-заголовок без ключа, заголовок окна) не на DynamicResource —
        // после смены словаря обновляем вручную.
        ApplyStaticStrings(lang);
        App.Current.Logger.Info($"Settings: language switched to {lang}", "UI");
    }

    /// <summary>Применяет переводы к элементам, чей текст не идёт через DynamicResource.</summary>
    private void ApplyStaticStrings(string lang)
    {
        // Элементы, чей текст строится в коде (мониторы и т.п.) — пересоздаём, чтобы подтянуть новый язык.
        _screenOcrMonitorsLoaded = false;
        PopulateScreenOcrMonitors();

        // Пункт [0] «По умолчанию» и «Нет GPU» — обычные строки из FindResource: словарь
        // уже сменился, но старые строки в Items не обновятся сами. Пересобираем комбо,
        // сохраняя выбор (устройства — по Id, GPU — по индексу).
        try
        {
            var devices = BrainstormBuddy.Audio.AudioDeviceEnumerator.GetAvailableDevices();
            var micDevices = devices.Where(d => !d.IsLoopback).ToList();
            var spkDevices = devices.Where(d => d.IsLoopback).ToList();

            var micSel = MicDeviceCombo.SelectedItem as BrainstormBuddy.Audio.AudioDeviceInfo;
            MicDeviceCombo.Items.Clear();
            MicDeviceCombo.Items.Add((string)FindResource("L_SetAudio_DefaultDevice"));
            foreach (var d in micDevices) MicDeviceCombo.Items.Add(d);
            MicDeviceCombo.SelectedIndex = 0;
            if (micSel != null)
            {
                int idx = micDevices.FindIndex(d => d.Id == micSel.Id);
                if (idx >= 0) MicDeviceCombo.SelectedIndex = idx + 1;
            }

            var spkSel = SpeakerDeviceCombo.SelectedItem as BrainstormBuddy.Audio.AudioDeviceInfo;
            SpeakerDeviceCombo.Items.Clear();
            SpeakerDeviceCombo.Items.Add((string)FindResource("L_SetAudio_DefaultDevice"));
            foreach (var d in spkDevices) SpeakerDeviceCombo.Items.Add(d);
            SpeakerDeviceCombo.SelectedIndex = 0;
            if (spkSel != null)
            {
                int idx = spkDevices.FindIndex(d => d.Id == spkSel.Id);
                if (idx >= 0) SpeakerDeviceCombo.SelectedIndex = idx + 1;
            }
        }
        catch { }

        try
        {
            int sttSel = SttGpuCombo.SelectedIndex;
            int whSel = WhisperGpuCombo.SelectedIndex;
            SttGpuCombo.Items.Clear();
            WhisperGpuCombo.Items.Clear();
            foreach (var g in BrainstormBuddy.Native.GpuEnumerator.List())
            {
                var item = $"#{g.Index}: {g.Name}";
                SttGpuCombo.Items.Add(item);
                WhisperGpuCombo.Items.Add(item);
            }
            if (SttGpuCombo.Items.Count == 0) SttGpuCombo.Items.Add((string)FindResource("L_SetStt_NoGpu"));
            if (WhisperGpuCombo.Items.Count == 0) WhisperGpuCombo.Items.Add((string)FindResource("L_SetStt_NoGpu"));
            if (SttGpuCombo.Items.Count > 0)
                SttGpuCombo.SelectedIndex = (sttSel >= 0 && sttSel < SttGpuCombo.Items.Count) ? sttSel : 0;
            if (WhisperGpuCombo.Items.Count > 0)
                WhisperGpuCombo.SelectedIndex = (whSel >= 0 && whSel < WhisperGpuCombo.Items.Count) ? whSel : 0;
        }
        catch { }

        // Заметка о приватности берётся из ресурсов по Id — при смене языка перечитываем,
        // иначе на уже открытом окне останется старый язык до следующего выбора провайдера.
        if (LlmProviderCombo.SelectedItem is LlmProviderProfile cur)
        {
            var note = PrivacyNoteFor(cur);
            LlmProviderPrivacyNote.Text = note ?? "";
            LlmProviderPrivacyNote.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Статус локального STT — код-построенная строка, при смене языка переприменяем.
        try { ApplyLocalSttStatus(App.Current.LocalStt.Status); } catch { }
    }


    // No62: сброс системного промпта AI-саммари к дефолту (Advanced.SummaryPrompt).
    private void OnSummaryPromptReset(object sender, RoutedEventArgs e)
    {
        SummaryPromptBox.Text = FileTranscriptionWindow.DefaultSummaryPrompt;
    }

    private void OnToggleApiKeyVisibility(object sender, RoutedEventArgs e)
        => SetApiKeyVisible(!_apiKeyVisible);

    // Оба поля (TextBox и PasswordBox) лежат в одной ячейке Grid: показываем одно,
    // переносим введённый текст без потери символов, тултип/иконка описывают
    // СЛЕДУЮЩЕЕ действие (ключ скрыт → «Показать ключ» + глаз).
    private void SetApiKeyVisible(bool visible)
    {
        var secret = _apiKeyVisible ? ApiKeyBox.Text : ApiKeyPwdBox.Password;
        if (visible)
        {
            ApiKeyBox.Text = secret;
            ApiKeyPwdBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Visibility = Visibility.Visible;
            App.Current.Logger.Debug("Settings: ApiKey shown (TextBox)", "UI");
        }
        else
        {
            // При первом вызове из конструктора биндинг ApiKeyBox.Text ещё не
            // успел вычислиться → берём секрет из VM, чтобы не затереть ключ.
            if (secret.Length == 0 && _vm.ApiKey.Length > 0)
                secret = _vm.ApiKey;
            ApiKeyPwdBox.Password = secret;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyPwdBox.Visibility = Visibility.Visible;
            App.Current.Logger.Debug("Settings: ApiKey hidden (PasswordBox shown)", "UI");
        }
        _apiKeyVisible = visible;

        PressedIconSwap.UpdateKey(ApiKeyVisibilityBtn,
            visible ? "BuddySettings.Icon.VisibilityOff.Default" : "BuddySettings.Icon.Visibility.Default");
        var tipKey = visible ? "L_TT_HideKey" : "L_TT_ShowKey";
        if (TryFindResource(tipKey) is string tip)
        {
            ApiKeyVisibilityBtn.SetResourceReference(ToolTipProperty, tipKey);
            ApiKeyVisibilityBtn.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, tipKey);
        }
        else
        {
            ApiKeyVisibilityBtn.ToolTip = visible ? "Скрыть ключ" : "Показать ключ";
            System.Windows.Automation.AutomationProperties.SetName(
                ApiKeyVisibilityBtn, (string)ApiKeyVisibilityBtn.ToolTip);
        }
    }

    // PasswordBox не поддерживает биндинг Password — вручную пушим в VM,
    // зеркало привязки ApiKeyBox.Text.
    private void OnApiKeyPwdChanged(object sender, RoutedEventArgs e)
        => _vm.ApiKey = ApiKeyPwdBox.Password;

    private async void OnCheckConnection(object sender, RoutedEventArgs e)
    {
        if (_vm.ApiClient == null)
        {
            ConnectionStatusText.Text = (string)FindResource("L_Set_ConnNoClient");
            ConnectionStatusText.SetResourceReference(ForegroundProperty, "ErrorBrush");
            return;
        }
        ConnectionStatusText.Text = (string)FindResource("L_Set_Checking");
        ConnectionStatusText.SetResourceReference(ForegroundProperty, "TextMutedBrush");
        App.Current.Logger.Info($"Settings: CheckConnection started, url={_vm.BaseUrl}", "UI");
        try
        {
            // Пошаговая проверка (URL → заголовки → /models → чат-пинг): у каждого шага
            // свой текст ошибки и подсказка, юзер видит, где именно отвалилось.
            var steps = await _vm.ApiClient.CheckLlmConnectionDetailedAsync();
            var sb = new StringBuilder();
            for (int i = 0; i < steps.Count; i++)
            {
                if (i > 0) sb.AppendLine();
                var s = steps[i];
                sb.Append(s.Ok ? "✓" : "✗").Append(string.Format((string)FindResource("L_Set_StepFmt"), i + 1, steps.Count, s.Name, s.Detail));
            }
            // Не-openai-chat протоколы могут не иметь чат-пинга — это информация, а не ошибка.
            var prof = _vm.FindLlmProvider(_vm.ProviderId);
            if (prof != null && prof.Protocol is "openai-responses" or "anthropic" &&
                !steps.Any(s => s.Name == "Chat"))
            {
                sb.AppendLine();
                sb.Append(string.Format((string)FindResource("L_Set_StepSkippedFmt"), prof.Protocol));
            }
            bool ok = steps.Count > 0 && steps.All(s => s.Ok);
            bool anyOk = steps.Any(s => s.Ok);
            ConnectionStatusText.Text = sb.Length > 0 ? sb.ToString() : (string)FindResource("L_Set_CheckFailed");
            ConnectionStatusText.SetResourceReference(ForegroundProperty,
                ok ? "AccentBrush" : anyOk ? "WarnBrush" : "ErrorBrush");
            App.Current.Logger.Info($"Settings: CheckConnection result = {ok} ({steps.Count} steps)", "UI");
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = string.Format((string)FindResource("L_Set_ErrorFmt"), ex.Message);
            ConnectionStatusText.SetResourceReference(ForegroundProperty, "ErrorBrush");
            App.Current.Logger.Error("Settings: CheckConnection threw", ex, "UI");
        }
    }

    // «Получить модели»: GET {baseUrl}/models → выпадающий список у Chat Model.
    private async void OnFetchModels(object sender, RoutedEventArgs e)
    {
        if (_vm.ApiClient == null) return;
        FetchModelsBtn.IsEnabled = false;
        ModelsStatusText.Text = (string)FindResource("L_Set_ModelsLoading");
        ModelsStatusText.SetResourceReference(ForegroundProperty, "TextMutedBrush");
        ModelsStatusText.Visibility = Visibility.Visible;
        try
        {
            var models = await _vm.ApiClient.GetModelsAsync();
            if (models.Count == 0)
            {
                ModelsStatusText.Text = (string)FindResource("L_Set_ModelsUnavailable");
                ModelsStatusText.SetResourceReference(ForegroundProperty, "WarnBrush");
                return;
            }
            var current = _vm.ChatModel;
            // Текущий ID обязан быть в списке: иначе после смены ItemsSource SelectedItem = null,
            // WPF сбрасывает Text редактируемого комбо и биндинг затирает сохранённый ChatModel.
            ChatModelCombo.ItemsSource = SettingsViewModel.ModelItemsWithCurrent(models, current);
            // Страховка от сброса Text самим WPF при пересборке ItemsSource
            // (SetCurrentValue, чтобы не уничтожить TwoWay-биндинг на ChatModel).
            if (ChatModelCombo.Text != current)
                ChatModelCombo.SetCurrentValue(System.Windows.Controls.ComboBox.TextProperty, current);
            // SelectedItem (не только Text): WPF сам выставит текст и обновит биндинг ChatModel.
            if (!string.IsNullOrWhiteSpace(current))
                ChatModelCombo.SelectedItem = current;
            ModelsStatusText.Text = string.Format((string)FindResource("L_Set_ModelsCountFmt"), models.Count);
            ModelsStatusText.SetResourceReference(ForegroundProperty, "AccentBrush");
            App.Current.Logger.Info($"Settings: fetched {models.Count} models", "UI");
        }
        catch (Exception ex)
        {
            ModelsStatusText.Text = (string)FindResource("L_Set_ModelsUnavailable");
            ModelsStatusText.SetResourceReference(ForegroundProperty, "WarnBrush");
            App.Current.Logger.Error("Settings: GetModels failed", ex, "UI");
        }
        finally { FetchModelsBtn.IsEnabled = true; }
    }

    // Локальный статус сохранения рядом с кнопкой: «Сохранено» — 3 с и авто-скрытие,
    // ошибки и персистентные подсказки («нажмите Сохранить») висят до следующей записи.
    // Таймер прячет только своё поколение (gen), свежий текст он не трогает.
    private void ShowSaveStatus(TextBlock tb, string text, string? brushKey, bool autoHide)
    {
        int gen = ++_saveStatusGen;
        tb.Text = text;
        if (brushKey != null)
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        else
            tb.Foreground = null; // дефолт из стиля окна
        tb.Visibility = Visibility.Visible;
        if (!autoHide) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (gen == _saveStatusGen)
                tb.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info($"Settings: Save clicked", "UI");
        // «Динамик (loopback)» живёт в code-behind (комбо без биндинга): устройство → его Id,
        // «По умолчанию» (строка [0]) → пустая строка = системный дефолт + автоследование.
        // Рестарт захвата при смене Id делает AudioEngine.UpdateConfig (через ApplyLiveConfigChanges).
        _vm.Config.Audio.LoopbackDeviceId =
            SpeakerDeviceCombo.SelectedItem is BrainstormBuddy.Audio.AudioDeviceInfo spk ? spk.Id : string.Empty;

        // Стелс-alias: валидируем как имя файла (запрещённые символы, «.», зарезервир.)
        // ДО сохранения — битое имя уронило бы стартовый relaunch в молчаливый fallback.
        if (!StealthAlias.TryNormalize(_vm.Config.Ui.StealthProcessName, out var stealthAlias, out var stealthError))
        {
            App.Current.Notifier.ShowError((string)FindResource("L_Set_StealthTitle"), stealthError);
            return;
        }
        _vm.Config.Ui.StealthProcessName = stealthAlias; // нормализовано (trim, без «.exe»)

        // Два отдельных контракта: запись файла и применение. У каждого — свой
        // текст в статус-строке: «не записано» ≠ «записано, но не применено».
        try
        {
            var loader = new ConfigLoader(App.Current.ConfigPath, App.Current.Logger);
            loader.Save(_vm.Config);
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: save failed", ex, "UI");
            var errTitle = TryFindResource("L_Set_SaveErrorTitle") as string ?? "Ошибка сохранения";
            ShowSaveStatus(SaveFlash, $"{errTitle}: {ex.Message}", "ErrorBrush", autoHide: false);
            return;
        }
        try
        {
            App.Current.ApplyLiveConfigChanges();
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: apply live changes failed", ex, "UI");
            var partialFmt = TryFindResource("L_Set_SavePartial") as string
                ?? "Настройки записаны, но не применены. {0}";
            ShowSaveStatus(SaveFlash, string.Format(partialFmt, ex.Message), "WarnBrush", autoHide: false);
            return;
        }
        ShowSaveStatus(SaveFlash,
            TryFindResource("L_Set_SavedFlash") as string ?? "Сохранено",
            "AccentBrush", autoHide: true);
        App.Current.Logger.Info("Settings: saved and applied (window stays open)", "UI");
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

    // «О приложении» → ссылка на сайт программы (открываем в системном браузере).
    private void OnSiteLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            var uri = (sender as Hyperlink)?.NavigateUri?.AbsoluteUri ?? e.Uri?.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(uri))
            {
                Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
                App.Current.Logger.Info($"Settings: opened site link {uri}", "UI");
            }
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: failed to open site link", ex, "UI");
        }
        e.Handled = true;
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
                Description = (string)FindResource("L_Set_ExportFolderPick"),
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
            App.Current.Notifier.ShowWarning((string)FindResource("L_Set_PresetNameTitle"), (string)FindResource("L_Set_PresetNameHint"));
            return;
        }
        _vm.SaveAsNewPreset(name);
        // Пресет лежит только в памяти — запись в файл делает общий «Сохранить»;
        // об этом и говорит персистентная подсказка рядом с кнопкой.
        ShowSaveStatus(PresetFlash,
            TryFindResource("L_Set_PresetUpdated") as string
                ?? "Пресет обновлён. Нажмите «Сохранить», чтобы записать настройки.",
            "TextMutedBrush", autoHide: false);
        App.Current.Logger.Info($"Settings: saved system prompt preset '{name}'", "UI");
    }

    private void OnDeletePrompt(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSystemPromptPreset == null)
        {
            App.Current.Notifier.ShowWarning((string)FindResource("L_Set_PresetDeleteTitle"), (string)FindResource("L_Set_PresetSelectHint"));
            return;
        }
        var name = _vm.SelectedSystemPromptPreset.Name;
        _vm.DeleteSelectedPreset();
        App.Current.Notifier.ShowInfo((string)FindResource("L_Set_PresetDeletedTitle"), name);
        App.Current.Logger.Info($"Settings: deleted system prompt preset '{name}'", "UI");
    }

    private void OnNewEmptyPrompt(object sender, RoutedEventArgs e)
    {
        var name = (string)FindResource("L_Set_PresetNewName") + DateTime.Now.ToString("HH:mm");
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
            MicTestButton.Content = (string)FindResource("L_Set_MicStop");
            MicTestHint.Text = (string)FindResource("L_Set_MicSpeakHint");
            App.Current.Logger.Info($"Mic test started: device #{_micTest.DeviceNumber}", "UI");
        }
        catch (Exception ex)
        {
            MicTestHint.Text = string.Format((string)FindResource("L_Set_MicFailFmt"), ex.Message);
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
        if (MicTestButton != null) MicTestButton.Content = (string)FindResource("L_Set_MicTestBtn");
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
        var eng = (SttEngineCombo.SelectedValue as string) ?? "native3";

        GigaamSettingsPanel.Visibility  = eng is "native" or "native3" ? Visibility.Visible : Visibility.Collapsed;
        WhisperSettingsPanel.Visibility = eng == "whisper" ? Visibility.Visible : Visibility.Collapsed;
        DockerSettingsPanel.Visibility  = eng == "docker"  ? Visibility.Visible : Visibility.Collapsed;
        RemoteUrlPanel.Visibility       = eng == "remote"  ? Visibility.Visible : Visibility.Collapsed;

        if (eng is "native" or "native3")
        {
            bool wantV3 = eng == "native3";
            bool haveV3 = wantV3 && App.Current.HasGigaamV3Model();
            var gp = App.Current.GetGigaamModelPath();
            bool have = wantV3 ? haveV3 : gp != null;
            var appDataModels = Path.Combine(App.Current.AppDataDir, "models");
            bool userDownloaded = !wantV3 && gp != null && gp.StartsWith(appDataModels, StringComparison.OrdinalIgnoreCase);
            if (wantV3)
                GigaamModelStatus.Text = haveV3
                    ? (string)FindResource("L_SetStt_GigaV3Found")
                    : gp != null
                        ? (string)FindResource("L_SetStt_GigaV3Missing")
                        : (string)FindResource("L_SetStt_GigaNoneMissing");
            else
                GigaamModelStatus.Text = !have
                    ? (string)FindResource("L_SetStt_GigaMissing")
                    : (userDownloaded ? (string)FindResource("L_SetStt_GigaDownloaded") : (string)FindResource("L_SetStt_GigaBundled"));
            GigaamModelStatus.Foreground = (System.Windows.Media.Brush)FindResource(have ? "SuccessBrush" : "WarnBrush");
            // Кнопка скачивания — про v2-модель (для v3 прямого URL пока нет, показываем её только на ветке v2/пусто).
            SttDownloadBtn.Visibility = !wantV3 && gp == null ? Visibility.Visible : Visibility.Collapsed;
            GigaamDeleteBtn.Visibility = userDownloaded ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (eng == "whisper")
        {
            bool have = App.Current.ResolveWhisperModel() != null;
            WhisperModelStatus.Text = have ? (string)FindResource("L_SetStt_WhisperDownloaded") : (string)FindResource("L_SetStt_WhisperMissing");
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
        FrameworkElement? el = target switch
        {
            "llm" => LlmProviderCombo,
            "overlay" => OverlaySection,
            _ => SttEngineCombo,
        };
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
        if (AppMessageBox.Show(this, string.Format((string)FindResource("L_SetStt_WhisperDeleteConfirmFmt"), Path.GetFileName(m)),
                (string)FindResource("L_SetStt_ModelDeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Current.DisposeWhisperEngine(); // освободить файл, если модель загружена в память
        try
        {
            File.Delete(m);
            WhisperDownloadStatus.Text = (string)FindResource("L_SetStt_ModelDeleted");
            App.Current.Logger.Info($"Whisper model deleted: {m}", "Ai");
        }
        catch (Exception ex) { WhisperDownloadStatus.Text = string.Format((string)FindResource("L_SetStt_ModelBusyFmt"), ex.Message); }
        UpdateEnginePanels();
    }

    private void OnGigaamDeleteModel(object sender, RoutedEventArgs e)
    {
        var modelsDir = Path.Combine(App.Current.AppDataDir, "models");
        var onnx = Path.Combine(modelsDir, "v2_ctc.onnx");
        if (!File.Exists(onnx)) { UpdateEnginePanels(); return; }
        if (AppMessageBox.Show(this, (string)FindResource("L_SetStt_GigaDeleteConfirm"),
                (string)FindResource("L_SetStt_ModelDeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            File.Delete(onnx);
            var labels = Path.Combine(modelsDir, "labels.json");
            if (File.Exists(labels)) File.Delete(labels);
            SttDownloadStatus.Text = (string)FindResource("L_SetStt_ModelDeleted");
            App.Current.Logger.Info("GigaAM model deleted", "Ai");
        }
        catch (Exception ex) { SttDownloadStatus.Text = string.Format((string)FindResource("L_SetStt_ModelBusyFmt"), ex.Message); }
        UpdateEnginePanels();
    }

    private void OnWhisperAccelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WhisperAccelCombo.SelectedValue is string v && IsLoaded) App.Current.Config.Audio.WhisperAccel = v;
        UpdateWhisperGpuPanelVisibility();
    }

    // Пикер видеокарты Whisper: показываем для всех режимов кроме CPU (auto и gpu).
    private void UpdateWhisperGpuPanelVisibility()
    {
        if (WhisperGpuPanel == null || WhisperAccelCombo == null) return;
        var accel = (WhisperAccelCombo.SelectedValue as string) ?? "auto";
        WhisperGpuPanel.Visibility = accel == "cpu" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnWhisperGpuChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // элемент вида "#1: NVIDIA ..." → берём индекс из SelectedIndex (совпадает с DXGI-порядком)
        if (WhisperGpuCombo.SelectedIndex >= 0)
            App.Current.Config.Audio.WhisperGpuDevice = WhisperGpuCombo.SelectedIndex;
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

    // ===================== LLM-провайдеры (профили) =====================
    // Дропдаун хранит Id профиля (биндинг ProviderId → Config.Api.ProviderId).
    // Встроенные — из LlmProviderRegistry, пользовательские — из Config.Api.CustomProviders.

    // Строка редактируемой таблицы заголовков (KeyValuePair не редактируем → отдельная строка).
    public sealed class ProviderHeaderRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private LlmProviderProfile? _editingProvider;      // профиль в панели редактирования
    private bool _providerEditIsNew;                    // черновик ещё не в CustomProviders
    private ObservableCollection<ProviderHeaderRow>? _headerRows;
    // SelectedValue-биндинг пишет _vm.ProviderId ДО SelectionChanged -> сравнение
    // _vm.ProviderId != profile.Id даёт false и BaseUrl провайдера не подставляется.
    // Ведём отдельный _appliedProviderId: «какой профиль реально применили к полям».
    private string? _appliedProviderId;

    // Тэг выбранного пункта ReasoningEffortCombo ("inherit"|""|"low"|"medium"|"high").
    // Обновляем ДО присвоения SelectedValue — иначе SelectionChanged при
    // ApplyProviderSelection перезаписывает только что прочитанное значение обратно.
    private string? _appliedEffort;

    // Выбор провайдера → подставить BaseUrl/модель-пресет, показать приватность/редактор.
    private void OnLlmProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var profile = LlmProviderCombo.SelectedItem as LlmProviderProfile;
        if (profile == null) return;
        // Выбрали другого провайдера, пока открыт черновик/редактор → сбрасываем панель.
        if (_editingProvider != null && profile.Id != _editingProvider.Id)
            HideProviderEditor();
        ApplyProviderSelection(profile);
    }

    // Общая логика применения профиля (выбор из списка, создание, дублирование).
    private void ApplyProviderSelection(LlmProviderProfile profile)
    {
        // Биндинг уже записал profile.Id в _vm.ProviderId -> источник истины по смене
        // провайдера это _appliedProviderId, а не _vm.ProviderId (иначе изменение не видно).
        bool providerChanged = _appliedProviderId != profile.Id;
        _appliedProviderId = profile.Id;
        if (providerChanged) _vm.ProviderId = profile.Id;
        if (!string.IsNullOrWhiteSpace(profile.BaseUrl) &&
            (providerChanged || string.IsNullOrWhiteSpace(_vm.BaseUrl)))
            _vm.BaseUrl = profile.BaseUrl;

        // Модель провайдера ≠ модель другого провайдера — при СМЕНЕ провайдера не оставляем
        // «чужой» ID («not a valid model ID», реальный кейс с OpenRouter). Без смены провайдера
        // (открыли настройки повторно) модель не трогаем — юзер мог вписать свой ID вручную.
        var m = _vm.ChatModel ?? "";
        // Сохранённый ID держим в ItemsSource: редактируемый комбо сбрасывает Text,
        // когда SelectedItem теряется при смене ItemsSource → биндинг затирал бы ChatModel.
        var items = SettingsViewModel.ModelItemsWithCurrent(profile.ModelPresets, m);
        ChatModelCombo.ItemsSource = items.Count > 0 ? items : null;
        // На всякий случай подстраховываемся от внутреннего сброса Text у WPF:
        // если при пересборке ItemsSource сработал deselect (OnSelectionChanged →
        // SelectedItemUpdated → Text = ""), возвращаем текст через SetCurrentValue —
        // прямое присвоение свойства уничтожило бы TwoWay-биндинг на ChatModel.
        if (ChatModelCombo.Text != m)
            ChatModelCombo.SetCurrentValue(System.Windows.Controls.ComboBox.TextProperty, m);
        if (providerChanged && profile.ModelPresets.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(m) || !profile.ModelPresets.Contains(m))
                _vm.ChatModel = profile.ModelPresets[0];
        }
        else if (providerChanged)
        {
            bool isLocal = profile.BaseUrl.Contains("127.0.0.1") || profile.BaseUrl.Contains("localhost")
                           || profile.AuthKind == "none";
            if (isLocal)
            {
                if (string.IsNullOrWhiteSpace(m) || m.Contains("/"))
                    _vm.ChatModel = "qwen2.5vl:7b"; // рабочая модель по умолчанию для Ollama
            }
            else if (!string.IsNullOrWhiteSpace(m) && !m.Contains("/") && !m.Contains(":"))
            {
                // Локальная модель на облачном провайдере невалидна — очищаем, чтобы юзер вписал свою.
                _vm.ChatModel = "";
                ConnectionStatusText.Text = (string)FindResource("L_Set_EnterModelHint");
                ConnectionStatusText.SetResourceReference(ForegroundProperty, "WarnBrush");
            }
        }

        // Заметка о приватности профиля (например, «запросы идут через шлюз opencode.ai»).
        // Заметка о приватности профиля: встроенные берём из ресурсов по Id (EN-совместимо),
        // кастомные — текстовое поле провайдера.
        var note = PrivacyNoteFor(profile);
        if (!string.IsNullOrWhiteSpace(note))
        {
            LlmProviderPrivacyNote.Text = note;
            LlmProviderPrivacyNote.Visibility = Visibility.Visible;
        }
        else
        {
            LlmProviderPrivacyNote.Text = "";
            LlmProviderPrivacyNote.Visibility = Visibility.Collapsed;
        }

        // Reasoning effort: строка видна только для openai-chat (где поле reasoning поддерживается).
        UpdateReasoningEffortUi(profile);

        // Псевдо-профиль реестра (Id="custom", не запись CustomProviders) удалять нельзя — прячем ✕.
        LlmProviderDeleteBtn.Visibility = (profile.IsBuiltIn || IsPseudoCustomProvider(profile))
            ? Visibility.Collapsed : Visibility.Visible;

        // Custom-профиль → сразу режим редактирования (поля уже в конфиге, отмена просто скрывает).
        if (!profile.IsBuiltIn && _editingProvider?.Id != profile.Id)
            ShowProviderEditor(profile, isNew: false);
        else if (profile.IsBuiltIn && !_providerEditIsNew)
            HideProviderEditor();
    }

    // Ключ ресурса заметки о приватности для встроенного провайдера; кастом — текст профиля.
    private string? PrivacyNoteFor(BrainstormBuddy.Ai.LlmProviderProfile p)
    {
        var key = "L_Llm_Privacy_" + p.Id.Replace('-', '_');
        var res = TryFindResource(key) as string;
        return res ?? p.PrivacyNote;
    }

    // Поле OpenAiClient шлёт "reasoning" только при Protocol=="openai-chat" — строку прячем
    // для остальных протоколов. Built-in-профиль → глобальный Config.Api.ReasoningEffortOverride
    // (null = брать значение профиля); custom-профиль → его собственное profile.ReasoningEffort.
    private void UpdateReasoningEffortUi(LlmProviderProfile profile)
    {
        ReasoningEffortRow.Visibility = profile.Protocol == "openai-chat"
            ? Visibility.Visible : Visibility.Collapsed;
        var effort = profile.IsBuiltIn
            ? _vm.Config.Api.ReasoningEffortOverride
            : profile.ReasoningEffort;
        _appliedEffort = effort switch
        {
            null => "inherit",
            "" or "low" or "medium" or "high" => effort,
            _ => "inherit", // неизвестное значение в конфиге — показываем «по профилю», не затираем
        };
        ReasoningEffortCombo.SelectedValue = _appliedEffort;
        // Лейбл "(по профилю)" при смене провайдера тот же, а значение за ним другое —
        // дёргаем INPC, чтобы биндинги на ReasoningEffortUi перечитали.
        _vm.NotifyReasoningEffortChanged();
    }

    // Смена пункта комбо: "inherit" → null (дефолт профиля), "" → не слать поле reasoning.
    private void OnReasoningEffortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var tag = ReasoningEffortCombo.SelectedValue as string;
        if (tag == _appliedEffort) return; // программный SelectedValue из UpdateReasoningEffortUi
        _appliedEffort = tag;
        string? eff = tag == "inherit" ? null : tag;
        var profile = LlmProviderCombo.SelectedItem as LlmProviderProfile;
        if (profile == null || profile.IsBuiltIn)
        {
            _vm.Config.Api.ReasoningEffortOverride = eff;
            _vm.NotifyReasoningEffortChanged();
        }
        else
        {
            // У custom-профиля null = «наследовать глобальный override».
            profile.ReasoningEffort = eff!;
            _vm.SaveCustomProvider(profile);
        }
    }

    // Лимит токенов ответа: применяем по Enter/уходу фокуса; мусор → откат к текущему значению.
    private void OnMaxTokensCommit(object sender, RoutedEventArgs e) => CommitMaxTokens();

    private void OnMaxTokensKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommitMaxTokens();
        e.Handled = true;
    }

    private void CommitMaxTokens()
    {
        if (!IsLoaded) return;
        if (int.TryParse(MaxTokensBox.Text, out int v))
        {
            _vm.MaxResponseTokens = v;
            // Сеттер VM клампит 50..4096 — показываем фактическое значение.
            MaxTokensBox.Text = _vm.MaxResponseTokens.ToString();
        }
        else
        {
            MaxTokensBox.Text = _vm.MaxResponseTokens.ToString();
        }
    }

    // «＋» — новый custom-провайдер (черновик; в конфиг попадёт только по «Сохранить»).
    private void OnAddProvider(object sender, RoutedEventArgs e)
    {
        var draft = _vm.NewCustomProvider();
        ShowProviderEditor(draft, isNew: true);
        App.Current.Logger.Info($"Settings: new custom provider draft {draft.Id}", "UI");
    }

    // «⧉» — копия выбранного профиля (built-in → тоже копируется как custom).
    private void OnDuplicateProvider(object sender, RoutedEventArgs e)
    {
        var src = LlmProviderCombo.SelectedItem as LlmProviderProfile ?? _vm.FindLlmProvider(_vm.ProviderId);
        if (src == null) return;
        var copy = _vm.CloneProvider(src);
        ShowProviderEditor(copy, isNew: true);
        App.Current.Logger.Info($"Settings: duplicated provider '{src.Id}' → draft {copy.Id}", "UI");
    }

    // Реестровый псевдо-профиль "custom" (его ставит LlmProviderRegistry): совпадающая по Id
    // запись в Config.Api.CustomProviders его затеняет и остаётся удаляемой.
    private bool IsPseudoCustomProvider(LlmProviderProfile profile) =>
        string.Equals(profile.Id, "custom", StringComparison.OrdinalIgnoreCase)
        && !_vm.Config.Api.CustomProviders.Any(p =>
            string.Equals(p.Id, "custom", StringComparison.OrdinalIgnoreCase));

    // «✕» — удалить custom-профиль (встроенные и реестровый "custom" не удаляются — кнопка скрыта).
    private void OnDeleteProvider(object sender, RoutedEventArgs e)
    {
        var profile = LlmProviderCombo.SelectedItem as LlmProviderProfile;
        if (profile == null || profile.IsBuiltIn || IsPseudoCustomProvider(profile)) return;
        _vm.DeleteCustomProvider(profile.Id);
        HideProviderEditor();
        LlmProviderCombo.SelectedIndex = 0;
        App.Current.Logger.Info($"Settings: deleted custom provider '{profile.Id}'", "UI");
    }

    // Панель редактирования custom-профиля.
    private void ShowProviderEditor(LlmProviderProfile profile, bool isNew)
    {
        _editingProvider = profile;
        _providerEditIsNew = isNew;
        ProviderNameBox.Text = profile.Name;
        ProviderBaseUrlBox.Text = profile.BaseUrl;
        ProviderProtocolCombo.SelectedValue = profile.Protocol;
        ProviderAuthCombo.SelectedValue = profile.AuthKind;
        _headerRows = new ObservableCollection<ProviderHeaderRow>(
            profile.ExtraHeaders.Select(h => new ProviderHeaderRow { Key = h.Key, Value = h.Value }));
        ProviderHeadersList.ItemsSource = _headerRows;
        ProviderPresetsBox.Text = string.Join(", ", profile.ModelPresets);
        ProviderPrivacyBox.Text = profile.PrivacyNote;
        LlmProviderEditPanel.Visibility = Visibility.Visible;
    }

    private void HideProviderEditor()
    {
        _editingProvider = null;
        _providerEditIsNew = false;
        _headerRows = null;
        ProviderHeadersList.ItemsSource = null;
        LlmProviderEditPanel.Visibility = Visibility.Collapsed;
    }

    private void OnAddProviderHeader(object sender, RoutedEventArgs e)
    {
        _headerRows ??= new ObservableCollection<ProviderHeaderRow>();
        ProviderHeadersList.ItemsSource ??= _headerRows;
        _headerRows.Add(new ProviderHeaderRow());
    }

    private void OnRemoveProviderHeader(object sender, RoutedEventArgs e)
    {
        if (_headerRows == null) return;
        if ((sender as FrameworkElement)?.DataContext is ProviderHeaderRow row)
            _headerRows.Remove(row);
    }

    // Кнопка «убрать заголовок» живёт в DataTemplate — имени нет, вайрим по Loaded.
    private void OnProviderHeaderRemoveBtnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn)
            PressedIconSwap.Wire(btn, "BuddySettings.Icon.Remove.Default");
    }

    // «Сохранить» в редакторе: поля панели → профиль → Config.Api.CustomProviders.
    private void OnSaveProvider(object sender, RoutedEventArgs e)
    {
        var p = _editingProvider;
        if (p == null) return;
        if (string.IsNullOrWhiteSpace(ProviderNameBox.Text) || string.IsNullOrWhiteSpace(ProviderBaseUrlBox.Text))
        {
            App.Current.Notifier.ShowWarning((string)FindResource("L_Set_ProviderTitle"), (string)FindResource("L_Set_ProviderFillHint"));
            return;
        }
        p.Name = ProviderNameBox.Text.Trim();
        p.BaseUrl = ProviderBaseUrlBox.Text.Trim();
        p.Protocol = ProviderProtocolCombo.SelectedValue as string ?? "openai-chat";
        p.AuthKind = ProviderAuthCombo.SelectedValue as string ?? "bearer";
        p.ExtraHeaders = _headerRows == null ? new List<KeyValuePair<string, string>>()
            : _headerRows.Where(r => !string.IsNullOrWhiteSpace(r.Key))
                .Select(r => new KeyValuePair<string, string>(r.Key.Trim(), r.Value.Trim()))
                .ToList();
        p.ModelPresets = (ProviderPresetsBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        p.PrivacyNote = ProviderPrivacyBox.Text?.Trim() ?? "";
        p.IsBuiltIn = false;

        // Если правили тот провайдер, что сейчас выбран — синкнуть главное поле BaseUrl сразу.
        if (_vm.ProviderId == p.Id && !string.IsNullOrWhiteSpace(p.BaseUrl))
            _vm.BaseUrl = p.BaseUrl;
        bool wasNew = _providerEditIsNew;
        _vm.SaveCustomProvider(p);
        _providerEditIsNew = false;
        _vm.ProviderId = p.Id; // SelectedValue обновит комбо → ApplyProviderSelection откроет редактор заново
        // Протокол в редакторе могли поменять (combo-провайдер остался тем же →
        // ApplyProviderSelection не сработает) — обновить видимость/значение effort-строки.
        UpdateReasoningEffortUi(p);
        App.Current.Notifier.ShowInfo((string)FindResource("L_Set_ProviderTitle"), string.Format((string)FindResource("L_Set_ProviderSavedFmt"), p.Name));
        App.Current.Logger.Info($"Settings: saved custom provider '{p.Id}' (new={wasNew})", "UI");
    }

    // «Отмена»: черновик выбрасываем (ничего не сохранено), сохранённый профиль просто прячет панель.
    private void OnCancelProviderEdit(object sender, RoutedEventArgs e)
    {
        HideProviderEditor();
    }

    // Первичная инициализация вкладки API: если ProviderId пуст/битый — угадываем профиль по BaseUrl.
    private void InitProviderSelection()
    {
        var profile = _vm.FindLlmProvider(_vm.ProviderId);
        if (profile == null && !string.IsNullOrWhiteSpace(_vm.BaseUrl))
            profile = _vm.LlmProviders.FirstOrDefault(p =>
                string.Equals(p.BaseUrl?.TrimEnd('/'), _vm.BaseUrl.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase));
        // Это ПЕРВИЧНАЯ загрузка сохранённого профиля, а не смена пользователем:
        // помечаем как уже применённый, чтобы ApplyProviderSelection не затирал
        // загруженные из конфига BaseUrl/ChatModel («providerChanged=false»).
        _appliedProviderId = profile?.Id;
        if (profile != null)
            ApplyProviderSelection(profile);
    }

    // Вайринг pressed-арта BuddySettings.Icon.* на иконочные кнопки окна
    // (DataTemplate-кнопка заголовков провайдера — через OnProviderHeaderRemoveBtnLoaded).
    private void WireSettingsIcons()
    {
        PressedIconSwap.Wire(LlmProviderAddBtn, "BuddySettings.Icon.Add.Default");
        PressedIconSwap.Wire(LlmProviderDuplicateBtn, "BuddySettings.Icon.Copy.Default");
        PressedIconSwap.Wire(LlmProviderDeleteBtn, "BuddySettings.Icon.Remove.Default");
        PressedIconSwap.Wire(FetchModelsBtn, "BuddySettings.Icon.Refresh.Default");
        PressedIconSwap.Wire(BrowseSavePathBtn, "BuddySettings.Icon.BrowseFolder.Default");
        PressedIconSwap.Wire(MemoryProjectAddBtn, "BuddySettings.Icon.Add.Default");
        PressedIconSwap.Wire(MemoryProjectEditBtn, "BuddySettings.Icon.Edit.Default");
        PressedIconSwap.Wire(MemoryProjectDeleteBtn, "BuddySettings.Icon.Delete.Default");
        // Ключ скрыт по умолчанию → глиф «показать» (глаз); точное состояние
        // выставляет SetApiKeyVisible(false) в конструкторе.
        PressedIconSwap.Wire(ApiKeyVisibilityBtn, "BuddySettings.Icon.Visibility.Default");

        // Текстовые кнопки с иконкой BuddySettings (иконка + подпись в StackPanel).
        PressedIconSwap.Wire(AddHeaderBtn, "BuddySettings.Icon.Add.Default");
        PressedIconSwap.Wire(CheckConnectionBtn, "BuddySettings.Icon.Refresh.Default");
        PressedIconSwap.Wire(SaveAsNewPromptBtn, "BuddySettings.Icon.Add.Default");
        PressedIconSwap.Wire(DeletePresetBtn, "BuddySettings.Icon.Delete.Default");
        PressedIconSwap.Wire(NewEmptyPromptBtn, "BuddySettings.Icon.Add.Default");
        PressedIconSwap.Wire(OpenFileTranscriptionBtn, "BuddySettings.Icon.BrowseFolder.Default");
        PressedIconSwap.Wire(SummaryPromptResetBtn, "BuddySettings.Icon.Refresh.Default");
        PressedIconSwap.Wire(HwCheckBtn, "BuddySettings.Icon.Refresh.Default");
        PressedIconSwap.Wire(GigaamDeleteBtn, "BuddySettings.Icon.Delete.Default");
        PressedIconSwap.Wire(WhisperDeleteBtn, "BuddySettings.Icon.Delete.Default");
        PressedIconSwap.Wire(LocalSttRemoveBtn, "BuddySettings.Icon.Delete.Default");
        PressedIconSwap.Wire(LocalSttPingBtn, "BuddySettings.Icon.Refresh.Default");
        PressedIconSwap.Wire(KaraokePreviewButton, "BuddySettings.Icon.Visibility.Default");
        PressedIconSwap.Wire(OpenLogsBtn, "BuddySettings.Icon.BrowseFolder.Default");
        PressedIconSwap.Wire(ScreenOcrOnceBtn, "BuddySettings.Icon.Visibility.Default");
        PressedIconSwap.Wire(OpenLogsFolderBtn, "BuddySettings.Icon.BrowseFolder.Default");
        PressedIconSwap.Wire(ClearLlmLogsBtn, "BuddySettings.Icon.Delete.Default");
        PressedIconSwap.Wire(CopyLlmLogsBtn, "BuddySettings.Icon.Copy.Default");
        PressedIconSwap.Wire(HistoryDeleteBtn, "BuddySettings.Icon.Delete.Default");
        PressedIconSwap.Wire(OpenMeetingsFolderBtn, "BuddySettings.Icon.BrowseFolder.Default");
        PressedIconSwap.Wire(MemoryE5CheckBtn, "BuddySettings.Icon.Refresh.Default");
        PressedIconSwap.Wire(MemoryE5UnloadBtn, "BuddySettings.Icon.Remove.Default");
        PressedIconSwap.Wire(OpenConfigFolderBtn, "BuddySettings.Icon.BrowseFolder.Default");
    }

    // Прогон 3-сек эталона через активный движок → мс + RTF + вердикт.
    private async void OnSttSpeedTest(object sender, RoutedEventArgs e)
    {
        SttSpeedTestBtn.IsEnabled = false;
        SttSpeedResult.Text = (string)FindResource("L_Set_Measuring");
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
                SttSpeedResult.Text = (string)FindResource("L_SetStt_RemoteFail");
                App.Current.Logger.Warn($"STT speed test [remote] недоступен: {ex.Message}", "Ai");
                return;
            }

            var times = new List<long>();
            for (int i = 0; i < 3; i++) times.Add(await TimedTranscribe());
            times.Sort();
            long ms = times[times.Count / 2];
            double rtf = ms / (secs * 1000.0);
            string verdict = rtf < 0.3 ? (string)FindResource("L_SetStt_VerdictFast") : rtf < 0.7 ? (string)FindResource("L_SetStt_VerdictOk") : (string)FindResource("L_SetStt_VerdictSlow");

            // Движок выбирается при старте. Если в списке выбран другой — предупреждаем про перезапуск.
            string selected = (SttEngineCombo.SelectedValue as string) ?? engine.Name;
            string pending = !string.Equals(selected, engine.Name, System.StringComparison.OrdinalIgnoreCase)
                ? string.Format((string)FindResource("L_SetStt_PendingEngineFmt"), selected) : "";
            SttSpeedResult.Text = string.Format((string)FindResource("L_SetStt_SpeedResultFmt"), engine.Name, ms, rtf.ToString("F2"), verdict + pending);
            App.Current.Logger.Info($"STT speed test [{engine.Name}]: {ms}ms RTF={rtf:F2}", "Ai");
        }
        catch (Exception ex)
        {
            SttSpeedResult.Text = string.Format((string)FindResource("L_Set_ErrorFmt"), ex.Message);
        }
        finally { SttSpeedTestBtn.IsEnabled = true; }
    }

    // Скачать встроенную модель (GigaAM ONNX) в %APPDATA%\models. URL/токен — из конфига.
    private async void OnSttDownloadModel(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        if (string.IsNullOrWhiteSpace(cfg.SttModelUrl))
        {
            SttDownloadStatus.Text = (string)FindResource("L_SetStt_UrlMissing");
            return;
        }
        SttDownloadBtn.IsEnabled = false;
        SttDownloadBar.Visibility = Visibility.Visible;
        SttDownloadBar.Value = 0;
        SttDownloadStatus.Text = (string)FindResource("L_Set_Downloading");
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
                else SttDownloadStatus.Text = (string)FindResource("L_Set_Downloading");
            });

            if (!string.IsNullOrEmpty(labelsUrl))
                await dl.DownloadAsync(labelsUrl, labelsDest, cfg.SttModelAuthHeader, cfg.SttModelAuthValue, 0, null, default);
            await dl.DownloadAsync(cfg.SttModelUrl, modelDest, cfg.SttModelAuthHeader, cfg.SttModelAuthValue,
                                   cfg.SttModelBytes, progress, default);

            SttDownloadStatus.Text = (string)FindResource("L_SetStt_DlDoneRestart");
            App.Current.Logger.Info($"STT model downloaded → {modelDest}", "Ai");
            UpdateEnginePanels(); // модель появилась → показать «Удалить»
        }
        catch (Exception ex)
        {
            SttDownloadStatus.Text = string.Format((string)FindResource("L_Set_ErrorFmt"), ex.Message);
            App.Current.Logger.Error("STT model download failed", ex, "Ai");
        }
        finally { SttDownloadBtn.IsEnabled = true; }
    }

    private async void OnWhisperDownloadModel(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        if (string.IsNullOrWhiteSpace(cfg.WhisperModelUrl))
        {
            WhisperDownloadStatus.Text = (string)FindResource("L_SetStt_WhisperUrlMissing");
            return;
        }
        WhisperDownloadBtn.IsEnabled = false;
        WhisperDownloadBar.Visibility = Visibility.Visible;
        WhisperDownloadBar.Value = 0;
        WhisperDownloadStatus.Text = (string)FindResource("L_Set_Downloading");
        try
        {
            var modelsDir = Path.Combine(App.Current.AppDataDir, "models");
            Directory.CreateDirectory(modelsDir);
            var dest = Path.Combine(modelsDir, "ggml-large-v3-turbo-q5_0.bin");
            var dl = new BrainstormBuddy.Stt.ModelDownloader();
            var progress = new Progress<double>(p =>
            {
                if (p >= 0) { WhisperDownloadBar.Value = p; WhisperDownloadStatus.Text = $"{p * 100:F0}%"; }
                else WhisperDownloadStatus.Text = (string)FindResource("L_Set_Downloading");
            });
            await dl.DownloadAsync(cfg.WhisperModelUrl, dest, null, null, cfg.WhisperModelBytes, progress, default);
            WhisperDownloadStatus.Text = (string)FindResource("L_SetStt_DlDoneApply");
            App.Current.Logger.Info($"Whisper model downloaded → {dest}", "Ai");
            UpdateEnginePanels(); // модель появилась → показать «Удалить» + ускорение
        }
        catch (Exception ex)
        {
            WhisperDownloadStatus.Text = string.Format((string)FindResource("L_Set_ErrorFmt"), ex.Message);
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
            HwSpecs.Text = string.Format((string)FindResource("L_Set_HwSpecsFmt"), r.CpuName, r.PhysicalCores, r.LogicalCores, r.RamGb, r.GpuName,
                r.HasDiscreteGpu ? (string)FindResource("L_Set_HwGpuDiscrete") : (string)FindResource("L_Set_HwGpuIntegrated"));
            HwRec.Text = (string)FindResource("L_Set_HwRecommendation") + r.Recommendation;
            HwVerdict.Visibility = HwSpecs.Visibility = HwRec.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            HwVerdict.Text = string.Format((string)FindResource("L_Set_HwFailFmt"), ex.Message);
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
            // Обновляет только in-memory конфиг — файл пишет общий «Сохранить»;
            // персистентная подсказка рядом с кнопкой говорит это пользователю.
            App.Current.Config.MultiAgent.UserProfile.Summary = _vm.UserProfileSummary;
            App.Current.Config.MultiAgent.UserProfile.CannotDo = _vm.UserProfileCannotDo;
            App.Current.Logger.Info("Profile saved", "UI");
            ShowSaveStatus(ProfileFlash,
                TryFindResource("L_Set_ProfileUpdated") as string
                    ?? "Резюме обновлено. Нажмите «Сохранить», чтобы записать настройки.",
                "TextMutedBrush", autoHide: false);
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Save profile failed", ex, "UI");
            ShowSaveStatus(ProfileFlash, ex.Message, "ErrorBrush", autoHide: false);
        }
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
            if (btn != null) { btn.IsEnabled = false; btn.Content = (string)FindResource("L_Set_LogCollecting"); }

            // Убедимся, что последние настройки логирования применены перед сбором.
            var loader = new ConfigLoader(App.Current.ConfigPath, App.Current.Logger);
            loader.Save(_vm.Config);
            App.Current.ApplyLiveConfigChanges();

            var zip = App.Current.CreateSupportBundle();
            App.Current.Logger.Info($"Support bundle ready: {zip}", "UI");
            App.Current.Notifier.ShowInfo((string)FindResource("L_Set_LogsCollected"), System.IO.Path.GetFileName(zip));

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
            App.Current.Notifier.ShowInfo((string)FindResource("L_Set_LogCollectFail"), ex.Message);
        }
        finally
        {
            if (btn != null) { btn.IsEnabled = true; btn.Content = (string)FindResource("L_Set_LogCollectBtn"); }
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

    // «Предпросмотр» караоке: App.PreviewKaraoke() вызывается через reflection,
    // чтобы вкладка собиралась независимо от наличия метода у App.
    private void OnKaraokePreviewClick(object sender, RoutedEventArgs e)
    {
        var app = System.Windows.Application.Current;
        app?.GetType().GetMethod("PreviewKaraoke")?.Invoke(app, null);
    }

    // ---- Вкладка «Экран OCR» ----

    private bool _screenOcrMonitorsLoaded;

    // Список мониторов строится лениво при открытии вкладки: перечисление Win32 идёт по
    // живой конфигурации дисплеев — после смены схемы список пересоберётся при следующем входе.
    private void PopulateScreenOcrMonitors()
    {
        if (_screenOcrMonitorsLoaded) return;
        try
        {
            var monitors = MonitorEnumerator.Enumerate();
            ScreenOcrMonitorCombo.Items.Clear();
            ScreenOcrMonitorCombo.Items.Add(new ComboBoxItem { Content = (string)FindResource("L_SetScr_MonitorPrimary"), Tag = -1 });
            foreach (var m in monitors)
            {
                var label = string.Format((string)FindResource(m.IsPrimary ? "L_SetScr_MonitorPrimaryFmt" : "L_SetScr_MonitorFmt"), m.Index + 1);
                if (!string.IsNullOrEmpty(m.DeviceName)) label += $" — {m.DeviceName}";
                ScreenOcrMonitorCombo.Items.Add(new ComboBoxItem { Content = label, Tag = m.Index });
            }
            _screenOcrMonitorsLoaded = true;
        }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"ScreenOcr: перечисление мониторов не удалось — {ex.Message}", "ScreenOcr");
        }
    }

    // «Распознать сейчас»: разовый снимок активного монитора и OCR в фоне — окно настроек
    // не морозится (инициализация Tesseract и сам проход занимают секунды).
    private async void OnScreenOcrRecognizeNow(object sender, RoutedEventArgs e)
    {
        ScreenOcrOnceBtn.IsEnabled = false;
        ScreenOcrPreviewBox.Text = (string)FindResource("L_SetScr_Recognizing");
        try
        {
            var text = await App.Current.RecognizeScreenOnceAsync();
            ScreenOcrPreviewBox.Text = string.IsNullOrWhiteSpace(text)
                ? (string)FindResource("L_SetScr_NoText")
                : text;
        }
        catch (Exception ex)
        {
            ScreenOcrPreviewBox.Text = string.Format((string)FindResource("L_SetScr_FailFmt"), ex.Message);
            App.Current.Logger?.Warn($"ScreenOcr one-shot failed — {ex.Message}", "ScreenOcr");
        }
        finally
        {
            ScreenOcrOnceBtn.IsEnabled = true;
        }
    }

    // ---- Вкладка «История» (журналы встреч %APPDATA%\BrainstormBuddy\meetings) ----

    private void OnSettingsTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Вкладка лениво обновляется при каждом открытии — подхватываем встречи,
        // записанные с момента последнего просмотра.
        if (!ReferenceEquals(e.Source, SettingsTabs)) return;
        if (SettingsTabs.SelectedItem == HistoryTab) RefreshMeetingList();
        if (SettingsTabs.SelectedItem == MemoryTab) RefreshMemoryTab();
        if (SettingsTabs.SelectedItem == ScreenOcrTab) PopulateScreenOcrMonitors();

        // Программный Select() (автотесты, ссылки «перейти в раздел») меняет
        // контент, но не прокручивает сайдбар — пункт остаётся за пределами
        // видимости. BringIntoView на TabItem ненадёжен (событие гаснет до
        // ScrollContentPresenter), поэтому скроллим напрямую: находим
        // ScrollViewer шаблона TabControl и сдвигаем Offset под геометрию
        // выбранного пункта. BeginInvoke — после прохода layout.
        if (SettingsTabs.SelectedItem is TabItem tab)
            Dispatcher.BeginInvoke(new Action(() => ScrollSettingsTabIntoView(tab)),
                System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ScrollSettingsTabIntoView(TabItem tab)
    {
        try
        {
            // Идём от TabItem вверх: TabPanel → ScrollContentPresenter → ScrollViewer.
            DependencyObject? d = VisualTreeHelper.GetParent(tab);
            System.Windows.Controls.Primitives.TabPanel? panel = d as System.Windows.Controls.Primitives.TabPanel;
            ScrollViewer? sv = null;
            while (d != null && sv == null)
            {
                sv = d as ScrollViewer;
                d = VisualTreeHelper.GetParent(d);
            }
            if (sv == null || panel == null || sv.ScrollableHeight <= 0) return;

            // Координаты пункта в пространстве TabPanel = координаты контента
            // (ScrollContentPresenter сдвигает саму панель, не её систему координат).
            // Докручиваем минимально: если пункт частично виден — только чтобы
            // его кромка целиком вошла в viewport.
            double offset = sv.VerticalOffset;
            double top = tab.TranslatePoint(new System.Windows.Point(0, 0), panel).Y;
            double bottom = top + tab.ActualHeight;
            if (top < offset) sv.ScrollToVerticalOffset(top);
            else if (bottom > offset + sv.ViewportHeight)
                sv.ScrollToVerticalOffset(Math.Max(0, bottom - sv.ViewportHeight));
        }
        catch (Exception ex) { App.Current.Logger?.Warn($"Nav scroll failed: {ex.Message}", "UI"); }
    }


    private void RefreshMeetingList()
    {
        try
        {
            var meetings = MeetingStore.LoadAll();
            MeetingHistoryList.ItemsSource = meetings;

            long total = 0;
            foreach (var m in meetings)
            {
                try { if (File.Exists(m.JournalPath)) total += new FileInfo(m.JournalPath).Length; }
                catch { /* файл могли удалить прямо сейчас */ }
            }
            HistoryStatsText.Text = string.Format(
                (string)FindResource("L_History_StatsFmt"), meetings.Count, FormatSize(total));
            HistoryEmptyText.Visibility = meetings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryDeleteBtn.IsEnabled = MeetingHistoryList.SelectedItem != null;
        }
        catch (Exception ex) { App.Current.Logger?.Error("Meeting history refresh failed", ex, "UI"); }
    }

    private void OnHistoryMeetingSelect(object sender, SelectionChangedEventArgs e)
        => HistoryDeleteBtn.IsEnabled = MeetingHistoryList.SelectedItems.Count > 0;

    private void OnHistorySelectAll(object sender, RoutedEventArgs e)
        => MeetingHistoryList.SelectAll();

    private void OnOpenMeetingHistory(object sender, RoutedEventArgs e)
    {
        try { App.Current.ShowMeetingHistory(); }
        catch (Exception ex) { App.Current.Logger?.Error("ShowMeetingHistory failed", ex, "UI"); }
    }

    // Пакетное удаление: MeetingStore.Delete снимает журнал+sidecar и сбрасывает
    // «живую» встречу, если удалили её. Подтверждение — только при выборе > 1.
    private void OnDeleteSelectedMeeting(object sender, RoutedEventArgs e)
    {
        var infos = MeetingHistoryList.SelectedItems.OfType<MeetingInfo>().ToList();
        if (infos.Count == 0) return;
        if (infos.Count > 1)
        {
            var q = string.Format((string)FindResource("L_Hist_ConfirmDeleteFmt"), infos.Count);
            if (AppMessageBox.Show(this, q, (string)FindResource("L_Tab_History"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        foreach (var info in infos)
        {
            try { MeetingStore.Delete(info.Id); }
            catch (Exception ex) { App.Current.Logger?.Error($"Delete meeting {info.Id} failed", ex, "UI"); }
        }
        RefreshMeetingList();
    }

    private void OnOpenMeetingsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = MeetingStore.Directory;
            if (System.IO.Directory.Exists(dir))
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { App.Current.Logger?.Error("Open meetings folder failed", ex, "UI"); }
    }

    // ---- Вкладка «Память» (Buddy Memory) ----

    // Пункт комбо «Проект по умолчанию»: null = «не выбран» (глобальная память).
    private sealed record MemoryProjectOption(long? Id, string Name)
    {
        public override string ToString() => Name; // DisplayMemberPath не всегда доезжает до UIA
    }

    // «Какой провайдер реально применён» — SelectedValue-биндинг пишет
    // _vm.MemoryProviderId ДО SelectionChanged (§8 AGENTS.md), поэтому источник
    // истины о смене провайдера — отдельное поле, как у LlmProviderCombo.
    private string? _appliedMemoryProviderId;

    private void RefreshMemoryTab()
    {
        try
        {
            // Провайдеры памяти — тот же список, что на вкладке API (built-in + custom).
            _appliedMemoryProviderId = _vm.MemoryProviderId;
            MemoryProviderCombo.SelectionChanged -= OnMemoryProviderChanged;
            MemoryProviderCombo.ItemsSource = _vm.LlmProviders;
            MemoryProviderCombo.SelectionChanged += OnMemoryProviderChanged;

            // Пресеты модели выбранного провайдера + текущее значение (editable-combo
            // иначе затирал бы Text при смене ItemsSource — тот же кейс, что ChatModel).
            var memProfile = _vm.FindLlmProvider(_vm.MemoryProviderId);
            var mm = _vm.MemoryModel ?? "";
            var memModels = SettingsViewModel.ModelItemsWithCurrent(memProfile?.ModelPresets, mm);
            MemoryModelCombo.ItemsSource = memModels.Count > 0 ? memModels : null;
            if (MemoryModelCombo.Text != mm)
                MemoryModelCombo.SetCurrentValue(System.Windows.Controls.ComboBox.TextProperty, mm);

            // App.MemoryStore создаётся eager в OnStartup и только при Config.Memory.Enabled;
            // null (память выкл / init failed) → комбо проектов disabled + подсказка.
            var store = App.Current.MemoryStore;
            RefreshMemoryProjects(store);
            UpdateMemoryModePanels();
            UpdateMemoryStats(store);
        }
        catch (Exception ex) { App.Current.Logger?.Error("Memory tab refresh failed", ex, "Memory"); }
    }

    private void RefreshMemoryProjects(IMemoryStore? store)
    {
        var options = new List<MemoryProjectOption>
        {
            new(null, (string)FindResource("L_Mem_Project_None"))
        };
        if (store != null)
        {
            foreach (var p in store.ListProjects())
                options.Add(new MemoryProjectOption(p.Id, p.Name));
        }

        MemoryProjectCombo.SelectionChanged -= OnMemoryProjectChanged;
        MemoryProjectCombo.ItemsSource = options;
        MemoryProjectCombo.SelectedItem =
            options.FirstOrDefault(o => o.Id == _vm.MemoryActiveProjectId) ?? options[0];
        MemoryProjectCombo.SelectionChanged += OnMemoryProjectChanged;

        MemoryProjectCombo.IsEnabled = store != null;
        MemoryNoStoreText.Visibility = store == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMemoryStats(IMemoryStore? store)
    {
        if (store == null)
        {
            MemoryStatsText.Text = (string)FindResource("L_Mem_StatsOff");
            return;
        }
        var s = store.GetStats();
        MemoryStatsText.Text = string.Format(
            (string)FindResource("L_Mem_StatsFmt"), s.Items, s.Meetings, FormatSize(s.DbBytes));
    }

    // Видимость вложенных панелей зависит от режимов (separate/local|openai/custom).
    private void UpdateMemoryModePanels()
    {
        if (MemorySeparatePanel == null) return;
        MemorySeparatePanel.Visibility =
            _vm.MemoryModelMode == "separate" ? Visibility.Visible : Visibility.Collapsed;
        MemoryEmbeddingsPanel.Visibility =
            _vm.MemoryEmbeddingsMode is "local" or "openai" ? Visibility.Visible : Visibility.Collapsed;
        MemoryLocalPanel.Visibility =
            _vm.MemoryEmbeddingsMode == "local" ? Visibility.Visible : Visibility.Collapsed;
        if (_vm.MemoryEmbeddingsMode == "local") UpdateE5Status();
        MemoryRetentionDaysPanel.Visibility =
            _vm.MemoryRetention == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Статус локальной E5-модели (спека §19): наличие файлов, состояние
    /// loader'а, idle-deadline; кнопки — Check/Unload/Download.</summary>
    private void UpdateE5Status()
    {
        if (MemoryE5StatusText == null) return;
        var ctl = App.Current.MemoryEmbedder as ILocalEmbeddingControl;
        var dir = ctl?.ModelDir ?? EmbeddingFactory.FindLocalModelDir(
            App.Current.AppDataDir, _vm.MemoryEmbeddingsModel);
        var installed = E5ModelDownloader.StaticIsInstalled(dir, App.Current.Logger);

        if (!installed)
        {
            MemoryE5StatusText.Text = string.Format(
                (string)FindResource("L_Mem_E5_NotInstalled"),
                dir, FormatSize(E5ModelDownloader.TotalDownloadBytes));
            MemoryE5DownloadBtn.IsEnabled = true;
            MemoryE5CheckBtn.IsEnabled = false;
            MemoryE5UnloadBtn.IsEnabled = false;
            return;
        }
        MemoryE5DownloadBtn.IsEnabled = false;
        MemoryE5CheckBtn.IsEnabled = ctl != null;
        var st = ctl?.GetStatus();
        MemoryE5StatusText.Text = st.HasValue
            ? string.Format((string)FindResource("L_Mem_E5_StatusFmt"),
                dir, st.Value.State, st.Value.ActiveLeases,
                st.Value.NextUnloadUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "-")
            : string.Format((string)FindResource("L_Mem_E5_Ready"), dir);
        MemoryE5UnloadBtn.IsEnabled = st is { State: LazyModelState.Loaded };
    }

    private void OnMemoryLocalRuntimeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // §39: режим/таймаут применяются на лету, без рестарта.
        (App.Current.MemoryEmbedder as ILocalEmbeddingControl)?.ApplyConfig(_vm.Config.Memory);
        UpdateE5Status();
    }

    private async void OnMemoryE5Check(object sender, RoutedEventArgs e)
    {
        var ctl = App.Current.MemoryEmbedder as ILocalEmbeddingControl;
        if (ctl == null) return;
        MemoryE5CheckBtn.IsEnabled = false;
        MemoryE5StatusText.Text = (string)FindResource("L_Mem_E5_Checking");
        try
        {
            var (ok, ms, dim) = await ctl.CheckAsync(CancellationToken.None);
            MemoryE5StatusText.Text = ok
                ? string.Format((string)FindResource("L_Mem_E5_CheckOk"), dim, ms)
                : string.Format((string)FindResource("L_Mem_E5_CheckFail"), "-");
        }
        catch (Exception ex)
        {
            MemoryE5StatusText.Text =
                string.Format((string)FindResource("L_Mem_E5_CheckFail"), ex.Message);
        }
        finally { UpdateE5Status(); }
    }

    private async void OnMemoryE5Unload(object sender, RoutedEventArgs e)
    {
        var ctl = App.Current.MemoryEmbedder as ILocalEmbeddingControl;
        if (ctl == null) return;
        MemoryE5UnloadBtn.IsEnabled = false;
        await ctl.UnloadNowAsync();
        UpdateE5Status();
    }

    private async void OnMemoryE5Download(object sender, RoutedEventArgs e)
    {
        var dir = EmbeddingFactory.FindLocalModelDir(
            App.Current.AppDataDir, _vm.MemoryEmbeddingsModel);
        MemoryE5DownloadBtn.IsEnabled = false;
        var progress = new Progress<double>(p =>
            MemoryE5StatusText.Text = string.Format(
                (string)FindResource("L_Mem_E5_Downloading"), p * 100.0));
        try
        {
            var dl = new E5ModelDownloader(log: App.Current.Logger);
            await dl.DownloadAsync(dir, progress, CancellationToken.None);
            MemoryE5StatusText.Text = (string)FindResource("L_Mem_E5_Downloaded");
        }
        catch (Exception ex)
        {
            MemoryE5StatusText.Text =
                string.Format((string)FindResource("L_Mem_E5_DownloadFail"), ex.Message);
            MemoryE5DownloadBtn.IsEnabled = true;
        }
        UpdateE5Status();
    }

    private void OnMemoryModelModeChanged(object sender, SelectionChangedEventArgs e) => UpdateMemoryModePanels();
    private void OnMemoryEmbeddingsModeChanged(object sender, SelectionChangedEventArgs e) => UpdateMemoryModePanels();
    private void OnMemoryRetentionChanged(object sender, SelectionChangedEventArgs e) => UpdateMemoryModePanels();

    // Смена провайдера памяти → подставить пресеты его моделей в комбо модели.
    private void OnMemoryProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (MemoryProviderCombo.SelectedItem is not LlmProviderProfile profile) return;
        // Биндинг уже записал profile.Id в _vm.MemoryProviderId — смену видим только
        // через _appliedMemoryProviderId (см. §8 AGENTS.md про SelectedValue).
        bool providerChanged = _appliedMemoryProviderId != profile.Id;
        _appliedMemoryProviderId = profile.Id;
        if (providerChanged) _vm.MemoryProviderId = profile.Id;

        var m = _vm.MemoryModel ?? "";
        var items = SettingsViewModel.ModelItemsWithCurrent(profile.ModelPresets, m);
        MemoryModelCombo.ItemsSource = items.Count > 0 ? items : null;
        if (MemoryModelCombo.Text != m)
            MemoryModelCombo.SetCurrentValue(System.Windows.Controls.ComboBox.TextProperty, m);
        if (providerChanged && profile.ModelPresets.Count > 0 &&
            (string.IsNullOrWhiteSpace(m) || !profile.ModelPresets.Contains(m)))
            _vm.MemoryModel = profile.ModelPresets[0];
    }

    private void OnMemoryProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (MemoryProjectCombo.SelectedItem is MemoryProjectOption opt)
            _vm.MemoryActiveProjectId = opt.Id;
    }

    // Хранилище живёт только при Memory.Enabled; null → объясняем, как включить.
    private IMemoryStore? ActiveMemoryStoreOrWarn()
    {
        var store = App.Current.MemoryStore;
        if (store == null)
            App.Current.Notifier.ShowWarning((string)FindResource("L_Tab_Memory"), (string)FindResource("L_Mem_NotAvailable"));
        return store;
    }

    private void OnMemoryProjectAdd(object sender, RoutedEventArgs e)
    {
        var store = ActiveMemoryStoreOrWarn();
        if (store == null) return;
        var name = PromptTextInput((string)FindResource("L_Mem_ProjectNewPrompt"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            store.UpsertProject(new MemoryProject { Name = name.Trim() });
            RefreshMemoryProjects(store);
            UpdateMemoryStats(store);
        }
        catch (Exception ex) { App.Current.Logger?.Error("Add memory project failed", ex, "Memory"); }
    }

    private void OnMemoryProjectEdit(object sender, RoutedEventArgs e)
    {
        var store = ActiveMemoryStoreOrWarn();
        if (store == null) return;
        if (MemoryProjectCombo.SelectedItem is not MemoryProjectOption opt || opt.Id == null)
        {
            App.Current.Notifier.ShowWarning((string)FindResource("L_Tab_Memory"), (string)FindResource("L_Mem_ProjectSelectFirst"));
            return;
        }
        var name = PromptTextInput((string)FindResource("L_Mem_ProjectRenamePrompt"), opt.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == opt.Name) return;
        try
        {
            // Upsert по Id перезапишет все поля — берём исходный объект, правим только имя
            // (иначе потеряются Description/Archived).
            var p = store.ListProjects(includeArchived: true).FirstOrDefault(x => x.Id == opt.Id.Value);
            if (p == null) return;
            p.Name = name.Trim();
            store.UpsertProject(p);
            RefreshMemoryProjects(store);
            UpdateMemoryStats(store);
        }
        catch (Exception ex) { App.Current.Logger?.Error("Rename memory project failed", ex, "Memory"); }
    }

    private void OnMemoryProjectDelete(object sender, RoutedEventArgs e)
    {
        var store = ActiveMemoryStoreOrWarn();
        if (store == null) return;
        if (MemoryProjectCombo.SelectedItem is not MemoryProjectOption opt || opt.Id == null)
        {
            App.Current.Notifier.ShowWarning((string)FindResource("L_Tab_Memory"), (string)FindResource("L_Mem_ProjectSelectFirst"));
            return;
        }
        var q = string.Format((string)FindResource("L_Mem_ProjectDeleteConfirmFmt"), opt.Name);
        if (AppMessageBox.Show(this, q, (string)FindResource("L_Tab_Memory"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            store.DeleteProject(opt.Id.Value);
            if (_vm.MemoryActiveProjectId == opt.Id) _vm.MemoryActiveProjectId = null;
            RefreshMemoryProjects(store);
            UpdateMemoryStats(store);
        }
        catch (Exception ex) { App.Current.Logger?.Error("Delete memory project failed", ex, "Memory"); }
    }

    private void OnOpenMemoryCenter(object sender, RoutedEventArgs e)
    {
        try { App.Current.OpenMemoryCenter(); }
        catch (Exception ex) { App.Current.Logger?.Error("OpenMemoryCenter failed", ex, "UI"); }
    }

    /// <summary>Минимальный ввод строки (как MemoryCenterWindow.PromptTextInput):
    /// текст + TextBox + OK/Cancel (Enter/Esc). null — отмена.</summary>
    private string? PromptTextInput(string prompt, string initial)
    {
        var dlg = new Window
        {
            Title = "BrainstormBuddy",
            Width = 380, SizeToContent = SizeToContent.Height,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = TryFindResource("BgBrush") as Brush,
            Foreground = TryFindResource("TextBrush") as Brush,
            FontFamily = FontFamily,
            FontSize = FontSize,
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 10) };
        tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        sp.Children.Add(tb);
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 76, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = (string)FindResource("L_MC_Btn_Cancel"), Width = 76, IsCancel = true };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        sp.Children.Add(row);
        dlg.Content = sp;
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; dlg.DialogResult = true; };
        return dlg.ShowDialog() == true ? result : null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
