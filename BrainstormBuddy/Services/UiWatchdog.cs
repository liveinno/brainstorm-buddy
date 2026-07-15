using System.IO;
using System.Windows.Threading;
using BrainstormBuddy.Services;

namespace BrainstormBuddy;

/// <summary>
/// Сторож зависаний UI. Оверлей скрыт из таскбара и из захвата экрана —
/// если UI-поток намертво встал (как в дедлоке слайдер/лог, 2026-07-04),
/// пользователь НЕ может закрыть приложение штатно. Сторож пингует Dispatcher
/// из фонового потока; если UI не отвечает HangThresholdSeconds подряд —
/// пишет диагностику прямо в файл (минуя UI-логгер) и жёстко завершает процесс.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private const int PingIntervalMs = 5000;
    private const int HangThresholdSeconds = 20;

    private readonly Dispatcher _dispatcher;
    private readonly LoggingService _logger;
    private readonly string _crashLogPath;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public UiWatchdog(Dispatcher dispatcher, LoggingService logger, string appDataDir)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _crashLogPath = Path.Combine(appDataDir, "logs", "ui-hang.log");
    }

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.Info($"UiWatchdog started (ping {PingIntervalMs}ms, kill after {HangThresholdSeconds}s hang)", "App");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var lastResponse = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var op = _dispatcher.BeginInvoke(DispatcherPriority.Background, () => { });
                var completed = await Task.WhenAny(
                    op.Task,
                    Task.Delay(PingIntervalMs, ct)) == op.Task;

                if (completed)
                {
                    lastResponse = DateTime.UtcNow;
                }
                else
                {
                    var hangSeconds = (DateTime.UtcNow - lastResponse).TotalSeconds;
                    TryWriteCrashLog($"UI ping timeout, hang for {hangSeconds:F0}s");
                    if (hangSeconds >= HangThresholdSeconds)
                    {
                        TryWriteCrashLog(
                            $"UI thread hung for {hangSeconds:F0}s — self-terminating so the user isn't stuck " +
                            $"with an unkillable overlay. Check the last entries of app.log for the deadlock site.");
                        Environment.FailFast("BrainstormBuddy UI thread hang — watchdog self-termination");
                    }
                }

                await Task.Delay(PingIntervalMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* сторож не имеет права упасть сам */ }
        }
    }

    private void TryWriteCrashLog(string message)
    {
        try
        {
            File.AppendAllText(_crashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
