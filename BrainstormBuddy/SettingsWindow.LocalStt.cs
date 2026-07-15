using System.Windows;
using BrainstormBuddy.Services;

namespace BrainstormBuddy;

// Вкладка "Локальный STT" — управление Docker-контейнером GigaAM (см. AGENTS.md §14)
public partial class SettingsWindow
{
    private void InitLocalSttTab()
    {
        var stt = App.Current.LocalStt;
        // Включение локального Docker-STT теперь выражается выбором движка «Локальный сервер в Docker»
        // в выпадающем списке (см. OnSttEngineChanged) — отдельный чекбокс убран.

        stt.StatusChanged += (s, status) => Dispatcher.Invoke(() => ApplyLocalSttStatus(status));
        stt.LogReceived += (s, line) => Dispatcher.Invoke(() =>
        {
            LocalSttLogBox.AppendText(line + Environment.NewLine);
            LocalSttLogBox.ScrollToEnd();
        });
        stt.StatsUpdated += (s, e) => Dispatcher.Invoke(() =>
        {
            LocalSttStatsText.Text = $"CPU: {stt.CurrentCpu}   RAM: {stt.CurrentRam}   ping: {stt.CurrentPingMs}   uptime: {stt.CurrentUptime}";
        });

        ApplyLocalSttStatus(stt.Status);
        if (!string.IsNullOrEmpty(stt.BuildLog))
        {
            LocalSttLogBox.Text = stt.BuildLog;
            LocalSttLogBox.ScrollToEnd();
        }
    }

    private void ApplyLocalSttStatus(LocalSttStatus status)
    {
        var (text, brushKey) = status switch
        {
            LocalSttStatus.Running => ($"Работает — {App.Current.LocalStt.EndpointUrl}", "AccentBrush"),
            LocalSttStatus.Starting => ("Запускается…", "TextMutedBrush"),
            LocalSttStatus.Building => ("Сборка образа… (первый раз до 10 мин)", "TextMutedBrush"),
            LocalSttStatus.Stopping => ("Останавливается…", "TextMutedBrush"),
            LocalSttStatus.Error => ("Ошибка — смотри лог ниже", "ErrorBrush"),
            _ => ("Остановлен", "TextMutedBrush"),
        };
        LocalSttStatusText.Text = text;
        LocalSttStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        LocalSttStartBtn.IsEnabled = status is LocalSttStatus.Stopped or LocalSttStatus.Error;
        LocalSttStopBtn.IsEnabled = status == LocalSttStatus.Running;
        LocalSttRemoveBtn.IsEnabled = status is LocalSttStatus.Running or LocalSttStatus.Stopped or LocalSttStatus.Error;
    }

    private async void OnLocalSttStart(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Settings: LocalStt start clicked", "LocalStt");
        try
        {
            await App.Current.LocalStt.StartAsync();
        }
        catch (Exception ex)
        {
            App.Current.Logger.Error("LocalStt start failed", ex, "LocalStt");
            App.Current.Notifier.ShowError("Локальный STT", ex.Message);
        }
    }

    private async void OnLocalSttStop(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Settings: LocalStt stop clicked", "LocalStt");
        try { await App.Current.LocalStt.StopAsync(); }
        catch (Exception ex) { App.Current.Logger.Error("LocalStt stop failed", ex, "LocalStt"); }
    }

    private async void OnLocalSttRemove(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Settings: LocalStt remove clicked", "LocalStt");
        try { await App.Current.LocalStt.RemoveAsync(); }
        catch (Exception ex) { App.Current.Logger.Error("LocalStt remove failed", ex, "LocalStt"); }
    }

    private async void OnLocalSttPing(object sender, RoutedEventArgs e)
    {
        var ok = await App.Current.LocalStt.HealthCheckAsync();
        App.Current.Logger.Info($"Settings: LocalStt ping = {ok}", "LocalStt");
        LocalSttLogBox.AppendText($"[ping] {(ok ? "OK" : "нет ответа")} ({App.Current.LocalStt.CurrentPingMs}){Environment.NewLine}");
        LocalSttLogBox.ScrollToEnd();
    }

}
