using System.IO;
using System.Text;
using System.Windows.Threading;
using BrainstormBuddy.Services;
using Microsoft.Win32;

namespace BrainstormBuddy;

/// <summary>
/// Crash-hardening: артефакты в каталоге logs рядом с app.log.
///   • heartbeat.txt — тикает раз в ~30 с, пока процесс жив;
///   • crash-last.txt — перезаписываемый маркер последнего managed-креша;
///   • WER LocalDumps — минидампы Windows для нативных крешей (HKCU, без админа).
///
/// Нативный креш (access violation в onnxruntime и т.п.) обходит все managed-хендлеры —
/// единственные его следы: свежий heartbeat при следующем старте и WER-дамп.
/// </summary>
public sealed class CrashGuard : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    // Свежий heartbeat при старте = процесс был жив только что и вышел без чистого выхода.
    private static readonly TimeSpan FreshThreshold = TimeSpan.FromMinutes(2);

    private readonly LoggingService _logger;
    private readonly string _heartbeatPath;
    private readonly string _crashLastPath;
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly object _fileLock = new();
    private readonly DispatcherTimer _timer;

    /// <summary>Признаки аварийного завершения прошлой сессии (читается после Install).</summary>
    public bool PreviousSessionAbnormal { get; private set; }

    public CrashGuard(LoggingService logger)
    {
        _logger = logger;
        _heartbeatPath = Path.Combine(logger.LogDirectory, "heartbeat.txt");
        _crashLastPath = Path.Combine(logger.LogDirectory, "crash-last.txt");

        // Остаток прошлой сессии проверяем ДО первой записи своего heartbeat —
        // иначе свежим окажется наш же файл (ложный positive на чистом старте).
        try
        {
            if (File.Exists(_heartbeatPath))
            {
                var age = DateTime.Now - File.GetLastWriteTime(_heartbeatPath);
                if (age < FreshThreshold)
                {
                    _logger.Error(
                        $"Предыдущая сессия завершена аварийно (native crash?): heartbeat свежий " +
                        $"({age.TotalSeconds:F0}с назад), clean-exit не состоялся", null, "Crash");
                    PreviousSessionAbnormal = true;
                }
                else
                {
                    _logger.Warn(
                        $"Heartbeat прошлой сессии остался на диске (age={age.TotalHours:F1}ч) — " +
                        "завершение было нештатным", "Crash");
                }
            }
        }
        catch (Exception ex) { _logger.Warn($"CrashGuard: heartbeat-check failed: {ex.Message}", "Crash"); }

        // Ставим heartbeat сразу: если умрём ещё в OnStartup, маркер уже на диске.
        WriteHeartbeat();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = HeartbeatInterval
        };
        _timer.Tick += (_, _) => WriteHeartbeat();
        _timer.Start();
    }
    /// <summary>
    /// Потребляет маркер последнего managed-креша прошлой сессии (crash-last.txt).
    /// Свежий heartbeat проверяется раньше — в конструкторе, до записи своего.
    /// Маркер удаляется, чтобы следующий старт не репортил тот же краш повторно.
    /// </summary>
    public void CheckPreviousSession()
    {
        try
        {
            if (File.Exists(_crashLastPath))
            {
                string marker = File.ReadAllText(_crashLastPath);
                _logger.Error(
                    $"Предыдущая сессия завершена аварийно (managed crash){Environment.NewLine}{marker.Trim()}",
                    null, "Crash");
                PreviousSessionAbnormal = true;
                File.Delete(_crashLastPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"CrashGuard: проверка прошлой сессии не удалась: {ex.Message}", "Crash");
        }
    }


    /// <summary>
    /// Маркер последнего managed-креша (перезаписываемый). Вызывать из
    /// UnhandledException/DispatcherUnhandledException/UnobservedTaskException.
    /// Прямая запись на диск, без зависимости от состояния логгера.
    /// </summary>
    public void ReportManagedCrash(string kind, Exception? ex, bool abnormalTermination)
    {
        try
        {
            var sb = new StringBuilder(512);
            sb.AppendLine($"timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"kind: {kind}");
            sb.AppendLine($"abnormal_termination: {abnormalTermination}");
            if (ex != null)
            {
                sb.AppendLine($"exception: {ex.GetType().FullName}: {ex.Message}");
                sb.AppendLine("stacktrace:");
                sb.AppendLine(ex.StackTrace ?? "(нет)");
                if (ex.InnerException != null)
                    sb.AppendLine($"inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            }
            else
            {
                sb.AppendLine("exception: (нет данных)");
            }
            lock (_fileLock)
                File.WriteAllText(_crashLastPath, sb.ToString());
        }
        catch { /* запись маркера креша — последний рубеж, падать нельзя */ }
    }

    /// <summary>
    /// WER LocalDumps для BrainstormBuddy.exe: нативный креш оставит дамп в
    /// %AppData%/BrainstormBuddy/dumps. HKCU — прав админа не требует. Отказ — молча.
    /// </summary>
    public void TryRegisterWerLocalDumps()
    {
        try
        {
            var dumpDir = Path.Combine(
                Path.GetDirectoryName(_logger.LogDirectory) ?? _logger.LogDirectory, "dumps");
            Directory.CreateDirectory(dumpDir);
            const string keyPath = @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\BrainstormBuddy.exe";
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key?.SetValue("DumpFolder", dumpDir, RegistryValueKind.String);
            // DumpType=2: full dump — в минидампе часто нет нативного стека onnxruntime.
            key?.SetValue("DumpType", 2, RegistryValueKind.DWord);
            key?.SetValue("DumpCount", 3, RegistryValueKind.DWord);
            _logger.Info($"WER LocalDumps registered: {dumpDir} (full, keep 3)", "Crash");
        }
        catch { /* реестр может быть закрыт политикой — best-effort, молча */ }
    }

    private void WriteHeartbeat()
    {
        try
        {
            lock (_fileLock)
            {
                File.WriteAllText(_heartbeatPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} " +
                    $"started={_startedAt:yyyy-MM-dd HH:mm:ss}");
            }
        }
        catch { /* heartbeat не должен мешать приложению */ }
    }

    /// <summary>Чистый выход: heartbeat снимается, чтобы старт не считал сессию крашем.</summary>
    public void Dispose()
    {
        _timer.Stop();
        try { lock (_fileLock) File.Delete(_heartbeatPath); }
        catch { /* не мешаем выходу */ }
    }
}
