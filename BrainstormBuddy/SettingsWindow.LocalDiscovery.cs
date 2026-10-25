using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;

namespace BrainstormBuddy;

/// <summary>
/// Панель «Обнаружение локальных провайдеров» на вкладке API.
/// Сканирует типичные места, где на машине уже есть ключ (Codex/ChatGPT,
/// opencode, переменные окружения), и подключает найденное в один клик.
/// Ключи только читаются; в конфиг попадают исключительно по нажатию «Подключить».
/// </summary>
public partial class SettingsWindow
{
    private readonly ObservableCollection<DiscoveryRow> _discoveryRows = new();
    private CancellationTokenSource? _discoveryScanCts;
    private bool _discoveryScanRunning;

    /// <summary>Строки панели обнаружения (создаётся один раз на окно).</summary>
    private void InitDiscoveryPanel()
    {
        DiscoveryItems.ItemsSource = _discoveryRows;
    }

    /// <summary>
    /// Сканирование источников. Каждый найденный/проверенный источник попадает в
    /// список сразу (по мере готовности), а не пачкой в конце — так видно, что
    /// сканер работает, а не завис.
    /// </summary>
    private async void OnDiscoveryScan(object sender, RoutedEventArgs e)
    {
        if (_discoveryScanRunning) return;
        _discoveryScanRunning = true;
        _discoveryScanCts?.Cancel();
        _discoveryScanCts = new CancellationTokenSource();
        var ct = _discoveryScanCts.Token;

        DiscoveryScanBtn.IsEnabled = false;
        _discoveryRows.Clear();
        DiscoveryStatusText.Visibility = Visibility.Visible;
        DiscoveryStatusText.Text = (string)FindResource("L_SetApi_DiscoverScanning");
        DiscoveryStatusText.SetResourceReference(ForegroundProperty, "TextMutedBrush");

        try
        {
            var options = new LocalDiscoveryOptions
            {
                ProbeLocalServers = true,
                HttpTimeoutSeconds = 4,
            };
            var progress = new Progress<LocalDiscoveryStep>(step =>
            {
                // Отчёт по прогрессу приходит из фонового потока — маршалим в UI-поток.
                Dispatcher.Invoke(() => ApplyDiscoveryStep(step));
            });

            var report = await LocalProviderDiscovery.ScanAsync(options, progress, ct);
            if (ct.IsCancellationRequested) return;

            var fmt = (string)FindResource("L_SetApi_DiscoverDoneFmt");
            DiscoveryStatusText.Text = string.Format(fmt, report.Found, report.Total,
                report.Elapsed.TotalSeconds.ToString("0.0"));
            DiscoveryStatusText.SetResourceReference(ForegroundProperty,
                report.Found > 0 ? "AccentBrush" : "TextMutedBrush");
            App.Current.Logger.Info(
                $"Settings: discovery scan done — found {report.Found}/{report.Total} in {report.Elapsed.TotalSeconds:0.0}s", "UI");
        }
        catch (OperationCanceledException)
        {
            DiscoveryStatusText.Text = (string)FindResource("L_SetApi_DiscoverCanceled");
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: discovery scan failed", ex, "UI");
            DiscoveryStatusText.Text = (string)FindResource("L_SetApi_DiscoverFailed");
            DiscoveryStatusText.SetResourceReference(ForegroundProperty, "ErrorBrush");
        }
        finally
        {
            _discoveryScanRunning = false;
            DiscoveryScanBtn.IsEnabled = true;
        }
    }

    /// <summary>Добавляет/обновляет строку по результату проверки источника.</summary>
    private void ApplyDiscoveryStep(LocalDiscoveryStep step)
    {
        var row = _discoveryRows.FirstOrDefault(r => r.SourceId == step.SourceId);
        if (row == null)
        {
            row = new DiscoveryRow
            {
                SourceId = step.SourceId,
                Name = step.DisplayName,
                Profile = step.Profile,
                ApiKey = step.ApiKey,
                CanApply = step.CanApply,
            };
            _discoveryRows.Add(row);
        }
        row.State = step.State;
        row.Summary = step.Summary;
        row.Detail = step.Detail;
        row.CanApply = step.CanApply;
        if (step.Profile != null) row.Profile = step.Profile;
        if (!string.IsNullOrEmpty(step.ApiKey)) row.ApiKey = step.ApiKey;
        if (!string.IsNullOrEmpty(step.RefreshToken)) row.RefreshToken = step.RefreshToken;
        row.Refresh();
    }

    /// <summary>Пересчитывает подписи строк (после смены языка интерфейса).</summary>
    private void RefreshDiscoveryRows()
    {
        foreach (var row in _discoveryRows) row.Refresh();
    }

    /// <summary>Подключение найденного провайдера: профиль, ключ, модель, сохранение.</summary>
    private void OnDiscoveryConnect(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as DiscoveryRow;
        if (row?.Profile == null) return;
        // Ключ обязателен не для всех: локальные провайдеры без авторизации
        // (LM Studio, AuthKind=none) подключаются вообще без ключа.
        if (string.IsNullOrEmpty(row.ApiKey)
            && !string.Equals(row.Profile.AuthKind, "none", StringComparison.OrdinalIgnoreCase)) return;

        var profile = row.Profile;
        try
        {
            // 1) Профиль: встроенный — выбираем, новый (codex-chatgpt и т.п.) — сохраняем
            //    как пользовательский, чтобы он появился в общем списке провайдеров.
            var existing = _vm.FindLlmProvider(profile.Id);
            if (existing == null)
            {
                var custom = profile.Clone();
                custom.IsBuiltIn = false;
                _vm.SaveCustomProvider(custom);
            }
            else if (!existing.IsBuiltIn)
            {
                // Пользовательский профиль с тем же id — обновляем его параметры.
                var custom = profile.Clone();
                custom.IsBuiltIn = false;
                _vm.SaveCustomProvider(custom);
            }

            // 2) Выбор провайдера в комбо + подстановка его полей.
            _vm.ProviderId = profile.Id;
            LlmProviderCombo.SelectedValue = profile.Id;
            var resolved = _vm.FindLlmProvider(profile.Id) ?? profile;
            ApplyProviderSelection(resolved);

            // 3) Ключ — в конфиг и в поля окна (у keyless-провайдера ключа нет).
            _vm.ApiKey = row.ApiKey ?? "";
            SyncApiKeyFields();

            // 3a) refresh_token подписки ChatGPT: нужен, чтобы обновить истёкший
            //     access_token без повторного обнаружения.
            if (profile.Id == "codex-chatgpt" && !string.IsNullOrWhiteSpace(row.RefreshToken))
                _vm.Config.Api.CodexRefreshToken = row.RefreshToken;

            // 4) Модель: если юзер уже выбрал её в строке — применяем.
            if (!string.IsNullOrWhiteSpace(row.SelectedModel))
                _vm.ChatModel = row.SelectedModel;

            // 5) Запись конфига + применение на лету (провайдер подхватывается без рестарта).
            SaveConfigAndApply();

            row.IsConnected = true;
            row.Refresh();
            DiscoveryStatusText.Visibility = Visibility.Visible;
            DiscoveryStatusText.Text = (string)FindResource("L_SetApi_DiscoverConnected");
            DiscoveryStatusText.SetResourceReference(ForegroundProperty, "AccentBrush");
            App.Current.Logger.Info($"Settings: connected discovered provider '{profile.Id}'", "UI");

            // 6) Модели этого провайдера — заранее, чтобы не искать их вручную.
            if (row.Models.Count == 0) _ = LoadDiscoveryModelsAsync(row);
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: connect discovered provider failed", ex, "UI");
            DiscoveryStatusText.Visibility = Visibility.Visible;
            DiscoveryStatusText.Text = (string)FindResource("L_SetApi_DiscoverFailed");
            DiscoveryStatusText.SetResourceReference(ForegroundProperty, "ErrorBrush");
        }
    }

    /// <summary>Загрузка списка моделей найденного провайдера (в фоне).</summary>
    private async Task LoadDiscoveryModelsAsync(DiscoveryRow row)
    {
        if (row.Profile == null) return;
        row.ModelsLoading = true;
        row.Refresh();
        try
        {
            var result = await ProviderModelLister.FetchAsync(
                row.Profile,
                row.ApiKey,
                model: row.SelectedModel ?? row.Profile.ModelPresets.FirstOrDefault());
            Dispatcher.Invoke(() =>
            {
                row.Models.Clear();
                foreach (var m in result.Models) row.Models.Add(m);
                row.ModelsStatus = result.Ok
                    ? string.Format((string)FindResource("L_SetApi_DiscoverModelsFmt"), result.Models.Count)
                    : (result.Error ?? "—");
                row.ModelsFailed = !result.Ok;
                row.SelectedModel = row.Models.FirstOrDefault();
                row.ModelsLoading = false;
                row.Refresh();

                // Список моделей провайдера — в комбо модели, чтобы модель можно было
                // выбрать прямо из выдачи, а не вписывать руками.
                var presets = row.Profile.ModelPresets.Concat(row.Models).Distinct().ToList();
                var items = SettingsViewModel.ModelItemsWithCurrent(presets, _vm.ChatModel);
                if (items.Count > 0)
                {
                    ChatModelCombo.ItemsSource = items;
                    // WPF при пересборке ItemsSource сбрасывает Text редактируемого
                    // комбо — возвращаем текущую модель, иначе поле покажет чужую.
                    var currentModel = _vm.ChatModel ?? "";
                    if (ChatModelCombo.Text != currentModel)
                        ChatModelCombo.SetCurrentValue(System.Windows.Controls.ComboBox.TextProperty, currentModel);
                    if (!string.IsNullOrWhiteSpace(currentModel))
                        ChatModelCombo.SelectedItem = currentModel;
                }

                // Провайдер подключён, а текущая модель — от другого провайдера
                // («deepseek-v4.1-flash» у codex): переключаем на модель этого
                // провайдера, иначе чат уйдёт с несуществующей моделью.
                if (_vm.ProviderId == row.Profile.Id && row.Models.Count > 0)
                {
                    var current = _vm.ChatModel ?? "";
                    if (!row.Models.Contains(current))
                    {
                        var preset = row.Profile.ModelPresets.FirstOrDefault(p => row.Models.Contains(p));
                        var pick = preset ?? row.Models[0];
                        _vm.ChatModel = pick;
                        ChatModelCombo.SetCurrentValue(System.Windows.Controls.ComboBox.TextProperty, pick);
                        SaveConfigAndApply();
                        App.Current.Logger.Info($"Settings: model switched to '{pick}' for provider '{row.Profile.Id}'", "UI");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            App.Current.Logger.Warn($"Settings: discovery model list failed: {ex.Message}", "UI");
            Dispatcher.Invoke(() =>
            {
                row.ModelsLoading = false;
                // Причина сбоя видна юзеру: «—» прятала ошибку за пустым списком.
                row.ModelsStatus = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
                row.ModelsFailed = true;
                row.Refresh();
            });
        }
    }

    /// <summary>Кнопка «Модели» в строке — повторный запрос списка моделей.</summary>
    private async void OnDiscoveryLoadModels(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as DiscoveryRow;
        if (row?.Profile == null) return;
        await LoadDiscoveryModelsAsync(row);
    }

    /// <summary>Применение модели, выбранной в строке обнаружения.</summary>
    private void OnDiscoveryApplyModel(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as DiscoveryRow;
        if (row == null || string.IsNullOrWhiteSpace(row.SelectedModel)) return;
        _vm.ChatModel = row.SelectedModel;
        ChatModelCombo.SetCurrentValue(System.Windows.Controls.ComboBox.TextProperty, row.SelectedModel);
        SaveConfigAndApply();
        DiscoveryStatusText.Visibility = Visibility.Visible;
        DiscoveryStatusText.Text = string.Format((string)FindResource("L_SetApi_DiscoverModelAppliedFmt"), row.SelectedModel);
        DiscoveryStatusText.SetResourceReference(ForegroundProperty, "AccentBrush");
    }

    /// <summary>Синхронизация поля ключа (TextBox/PasswordBox) с новым значением.</summary>
    private void SyncApiKeyFields()
    {
        if (_apiKeyVisible) ApiKeyBox.Text = _vm.ApiKey;
        else ApiKeyPwdBox.Password = _vm.ApiKey;
    }

    /// <summary>Запись конфига и применение на лету (общий путь для панели обнаружения).</summary>
    private void SaveConfigAndApply()
    {
        try
        {
            var loader = new ConfigLoader(App.Current.ConfigPath, App.Current.Logger);
            loader.Save(_vm.Config);
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: save failed (discovery)", ex, "UI");
            DiscoveryStatusText.Visibility = Visibility.Visible;
            DiscoveryStatusText.Text = string.Format((string)FindResource("L_SetApi_DiscoverSaveFailedFmt"), ex.Message);
            DiscoveryStatusText.SetResourceReference(ForegroundProperty, "ErrorBrush");
            return;
        }
        try
        {
            App.Current.ApplyLiveConfigChanges();
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("Settings: apply live changes failed (discovery)", ex, "UI");
            DiscoveryStatusText.Visibility = Visibility.Visible;
            DiscoveryStatusText.Text = string.Format((string)FindResource("L_SetApi_DiscoverSaveFailedFmt"), ex.Message);
            DiscoveryStatusText.SetResourceReference(ForegroundProperty, "WarnBrush");
        }
    }
}

/// <summary>Строка панели обнаружения: один проверенный источник.</summary>
public sealed class DiscoveryRow : INotifyPropertyChanged
{
    public string SourceId { get; init; } = "";
    public string Name { get; init; } = "";
    public LocalDiscoveryState State { get; set; } = LocalDiscoveryState.Pending;
    public string Summary { get; set; } = "";
    public string Detail { get; set; } = "";
    public LlmProviderProfile? Profile { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>refresh_token подписки (только ChatGPT/Codex) — хранится в конфиге.</summary>
    public string? RefreshToken { get; set; }
    public bool CanApply { get; set; }
    public bool IsConnected { get; set; }
    public bool ModelsLoading { get; set; }
    public string ModelsStatus { get; set; } = "";
    /// <summary>Загрузка моделей упала — статус показываем ошибкой.</summary>
    public bool ModelsFailed { get; set; }
    public ObservableCollection<string> Models { get; } = new();
    public string? SelectedModel { get; set; }

    /// <summary>Локализованная метка состояния.</summary>
    public string StateLabel => State switch
    {
        LocalDiscoveryState.Found => Loc("L_SetApi_DiscoverStateFound", "найдено"),
        LocalDiscoveryState.Missing => Loc("L_SetApi_DiscoverStateMissing", "не найдено"),
        LocalDiscoveryState.Unsupported => Loc("L_SetApi_DiscoverStateUnsupported", "не поддерживается"),
        LocalDiscoveryState.Failed => Loc("L_SetApi_DiscoverStateFailed", "ошибка"),
        LocalDiscoveryState.Checking => Loc("L_SetApi_DiscoverStateChecking", "проверка…"),
        _ => Loc("L_SetApi_DiscoverStatePending", "ожидает"),
    };

    public Visibility CanApplyVisibility =>
        CanApply && !IsConnected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ConnectedVisibility =>
        IsConnected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ModelsVisibility =>
        Models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Статус загрузки моделей виден всегда, когда он есть: при неудачном
    /// запросе список пуст, но текст ошибки юзеру нужен.
    /// </summary>
    public Visibility ModelsStatusVisibility =>
        !string.IsNullOrWhiteSpace(ModelsStatus) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Кнопка «Модели» остаётся и после подключения — список можно перезапросить.</summary>
    public Visibility ModelsButtonVisibility =>
        Profile != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Пересчёт видимостей и подписей (после смены языка/состояния).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(CanApplyVisibility));
        OnPropertyChanged(nameof(ConnectedVisibility));
        OnPropertyChanged(nameof(ModelsVisibility));
        OnPropertyChanged(nameof(ModelsButtonVisibility));
        OnPropertyChanged(nameof(ModelsStatusVisibility));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ModelsStatus));
        OnPropertyChanged(nameof(ModelsFailed));
        OnPropertyChanged(nameof(SelectedModel));
    }

    private static string Loc(string key, string fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
