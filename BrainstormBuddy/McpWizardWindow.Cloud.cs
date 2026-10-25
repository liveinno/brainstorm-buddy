using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BrainstormBuddy.Core.Mcp.Http;

namespace BrainstormBuddy;

/// <summary>
/// Облачный шаг мастера (спека §6.1): панель CloudStepPanel показывает состояние
/// Tailscale Funnel и даёт кнопку включения. Всё опирается на существующие
/// именованные контролы (CloudStatusText/CloudInstructionsText/CloudUrlText/
/// CloudFunnelButton) — XAML не меняется.
///
/// Состояния:
///   NotInstalled        → инструкция поставить Tailscale, кнопка «Открыть сайт»
///   DaemonUnavailable   → инструкция залогиниться (tailscale up), кнопка «Проверить снова»
///   Ready/Running       → «Ожидаем подключение агента» + публичный URL + кнопка вкл/выкл.
///
/// TunnelMode=manual: панель показывает PublicBaseUrl из конфига и подсказку
/// про собственный туннель (кнопки funnel нет).
/// </summary>
public partial class McpWizardWindow
{
    private bool _cloudWired;
    private bool _cloudBusy;
    private DispatcherTimer? _cloudWaitTimer;

    /// <summary>Вызывается из UpdateCloudPanelVisibility: ленивая wiring + освежение статуса.</summary>
    private void RefreshCloudPanel()
    {
        if (CloudStepPanel == null || CloudStepPanel.Visibility != Visibility.Visible) return;
        if (!_cloudWired)
        {
            _cloudWired = true;
            CloudFunnelButton.Click += OnCloudFunnelClick;
            Closed += (_, _) => _cloudWaitTimer?.Stop();
        }
        if (!_cloudBusy) _ = RefreshCloudStateAsync();
    }

    private async Task RefreshCloudStateAsync()
    {
        try
        {
            CloudStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            if (_config.Mcp.TunnelMode == "manual")
            {
                CloudStatusText.Text = T("L_McpW_CloudManual",
                    "Ручной туннель: укажите публичный HTTPS-адрес (он уже направлен на этот компьютер).");
                CloudUrlText.Text = string.IsNullOrWhiteSpace(_config.Mcp.PublicBaseUrl)
                    ? "" : _config.Mcp.PublicBaseUrl.TrimEnd('/') + "/mcp";
                CloudInstructionsText.Text = T("L_McpW_CloudManualHint",
                    "Поднимите свой туннель (ngrok/cloudflared и т.п.) на 127.0.0.1:" + _config.Mcp.HttpPort +
                    " и вставьте адрес в настройки (MCP → Публичный URL).");
                CloudFunnelButton.Visibility = Visibility.Collapsed;
                return;
            }

            var info = await TailscaleFunnel.DetectAsync(_config.Mcp.HttpPort);
            switch (info.State)
            {
                case TailscaleFunnel.State.NotInstalled:
                    CloudStatusText.Text = T("L_McpW_CloudNoTs",
                        "Для облачного доступа нужен Tailscale (бесплатный).");
                    CloudInstructionsText.Text = T("L_McpW_CloudNoTsHint",
                        "1) Установите Tailscale с tailscale.com/download и войдите в аккаунт.\n" +
                        "2) Вернитесь сюда и нажмите «Включить облачный доступ».\n" +
                        "Buddy сам включит Funnel: HTTPS-порт наружу → локальный MCP-сервер.");
                    CloudUrlText.Text = "";
                    CloudFunnelButton.Content = T("L_McpW_CloudInstall", "Открыть tailscale.com");
                    CloudFunnelButton.Visibility = Visibility.Visible;
                    break;
                case TailscaleFunnel.State.DaemonUnavailable:
                    CloudStatusText.Text = T("L_McpW_CloudTsDown",
                        "Tailscale установлен, но не подключён к tailnet.");
                    CloudInstructionsText.Text = T("L_McpW_CloudTsDownHint",
                        info.Detail ?? "Запустите Tailscale и войдите в аккаунт (иконка в трее → Log in), затем нажмите «Проверить снова».");
                    CloudUrlText.Text = "";
                    CloudFunnelButton.Content = T("L_McpW_CloudRetry", "Проверить снова");
                    CloudFunnelButton.Visibility = Visibility.Visible;
                    break;
                case TailscaleFunnel.State.Running:
                    CloudStatusText.Text = T("L_McpW_CloudWaiting",
                        "Облачный доступ включён. Ожидаем подключение агента.");
                    CloudInstructionsText.Text = T("L_McpW_CloudRunningHint",
                        "Этот адрес и вся конфигурация уже в JSON ниже на шаге 3 — вставьте его в облачный коннектор (OAuth пройдёт автоматически).");
                    CloudUrlText.Text = (info.PublicUrl ?? _config.Mcp.PublicBaseUrl).TrimEnd('/') + "/mcp";
                    CloudFunnelButton.Content = T("L_McpW_CloudDisable", "Выключить облачный доступ");
                    CloudFunnelButton.Visibility = Visibility.Visible;
                    StartCloudWaitTicker();
                    break;
                default: // Ready — tailscale есть, funnel ещё не поднят
                    CloudStatusText.Text = T("L_McpW_CloudReady",
                        "Tailscale найден (" + (info.DnsName ?? "tailnet") + "). Можно включать облачный доступ.");
                    CloudInstructionsText.Text = T("L_McpW_CloudReadyHint",
                        "Нажмите «Включить облачный доступ» — Buddy запустит Tailscale Funnel и покажет публичный адрес.");
                    CloudUrlText.Text = info.DnsName ?? "";
                    CloudFunnelButton.Content = T("L_McpW_CloudEnable", "Включить облачный доступ");
                    CloudFunnelButton.Visibility = Visibility.Visible;
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"MCP wizard: Tailscale check failed: {ex}", "Mcp");
            CloudStatusText.Text = T("L_McpW_CloudErr",
                "Не удалось проверить Tailscale — подробности в журнале.");
            CloudStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            CloudFunnelButton.Content = T("L_McpW_CloudRetry", "Проверить снова");
            CloudFunnelButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Лёгкий тикер «Ожидаем подключение»: перечитывает LastRequestUtc раз в 5 с.</summary>
    private void StartCloudWaitTicker()
    {
        _cloudWaitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _cloudWaitTimer.Tick -= OnCloudWaitTick;
        _cloudWaitTimer.Tick += OnCloudWaitTick;
        _cloudWaitTimer.Start();
    }

    private void OnCloudWaitTick(object? s, EventArgs e)
    {
        try
        {
            var facade = App.Current.McpHost?.Facade ?? App.Current.McpHttp?.Facade;
            var status = facade?.GetHttpStatus();
            if (status == null) return;
            if (status.PublicBaseUrl == null || !status.ListenerRunning)
            {
                CloudStatusText.Text = T("L_McpW_CloudDown",
                    "Нет связи: ") + (status.DownReason ?? "listener/funnel недоступен");
                CloudStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            }
            else
            {
                CloudStatusText.Text = T("L_McpW_CloudWaiting",
                    "Облачный доступ включён. Ожидаем подключение агента.");
            }
        }
        catch { /* панель статуса не должна падать */ }
    }

    private async void OnCloudFunnelClick(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy) return;
        var info = await TailscaleFunnel.DetectAsync(_config.Mcp.HttpPort);
        if (info.State == TailscaleFunnel.State.NotInstalled)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://tailscale.com/download") { UseShellExecute = true }); } catch { }
            return;
        }
        _cloudBusy = true;
        CloudFunnelButton.IsEnabled = false;
        try
        {
            if (info.State == TailscaleFunnel.State.Running)
            {
                await TailscaleFunnel.StopFunnelAsync(_config.Mcp.HttpPort);
                _config.Mcp.TunnelMode = "off";
                _config.Mcp.PublicBaseUrl = "";
                App.Current.SaveConfigSafe();
            }
            else if (info.State != TailscaleFunnel.State.NotInstalled &&
                     info.State != TailscaleFunnel.State.DaemonUnavailable)
            {
                var (url, error) = await TailscaleFunnel.StartFunnelAsync(_config.Mcp.HttpPort);
                if (url == null)
                {
                    CloudStatusText.Text = T("L_McpW_CloudDown", "Нет связи: ") + (error ?? "funnel не поднялся");
                    CloudStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                }
                else
                {
                    _config.Mcp.TunnelMode = "tailscale-funnel";
                    _config.Mcp.HttpEnabled = true;
                    _config.Mcp.PublicBaseUrl = url;
                    App.Current.SaveConfigSafe();
                }
            }
        }
        finally
        {
            _cloudBusy = false;
            CloudFunnelButton.IsEnabled = true;
            await RefreshCloudStateAsync();
        }
    }
}
