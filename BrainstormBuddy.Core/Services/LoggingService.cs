using System.Collections.Concurrent;
using System.IO;
using System.Text;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public sealed class LogEvent
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Category { get; init; } = "App";
    public string Message { get; init; } = string.Empty;

    public string FormattedLine =>
        $"[{Timestamp:HH:mm:ss.fff}] [{Level,-5}] [{Category,-10}] {Message}";
}

/// <summary>
/// Централизованный логгер с тремя sink'ами:
///   1) Debug.WriteLine / Console
///   2) Файл (ротация 5×1 МБ)
///   3) In-memory ring buffer + событие LogAppended (для UI-окна логов)
/// </summary>
public class LoggingService : IDisposable
{
    private readonly string _logDir;
    private long _maxFileSize = 1_048_576;
    private int _maxFiles = 5;
    private readonly object _fileLock = new();
    private string _currentLogPath = string.Empty;

    // Runtime-переключаемые из настроек. volatile — читаются из аудио/воркер-потоков.
    private volatile bool _fileEnabled = true;   // писать app.log на диск
    private volatile bool _verbose;              // пропускать Debug-уровень

    // Ring buffer для UI-окна
    private const int RingCapacity = 500;
    private readonly LogEvent[] _ringBuffer = new LogEvent[RingCapacity];
    private int _ringHead;
    private int _ringCount;
    private readonly object _ringLock = new();

    public event EventHandler<LogEvent>? LogAppended;

    public LoggingService(string appDataDir)
    {
        _logDir = Path.Combine(appDataDir, "logs");
        Directory.CreateDirectory(_logDir);
        _currentLogPath = Path.Combine(_logDir, "app.log");
    }

    public string LogDirectory => _logDir;
    public string LogFilePath => _currentLogPath;

    public bool FileEnabled => _fileEnabled;
    public bool Verbose => _verbose;

    /// <summary>
    /// Применить настройки логирования (вкл/выкл файла, подробность, ротация).
    /// Можно вызывать в рантайме при сохранении настроек.
    /// </summary>
    public void Configure(LoggingConfig cfg)
    {
        if (cfg == null) return;
        _fileEnabled = cfg.Enabled;
        _verbose = cfg.Verbose;
        _maxFiles = Math.Max(1, cfg.MaxFiles);
        _maxFileSize = Math.Max(64 * 1024L, cfg.MaxFileSizeMb * 1024L * 1024L);
        Info($"Logging configured: file={_fileEnabled}, verbose={_verbose}, maxFiles={_maxFiles}, maxSize={_maxFileSize / 1024}KB", "Log");
    }

    public void Debug(string message, string category = "App") => Write(LogLevel.Debug, category, message);
    public void Info(string message, string category = "App") => Write(LogLevel.Info, category, message);
    public void Warn(string message, string category = "App") => Write(LogLevel.Warn, category, message);
    public void Error(string message, Exception? ex = null, string category = "App")
    {
        var msg = ex == null ? message : $"{message}: {ex}";
        Write(LogLevel.Error, category, msg);
    }

    /// <summary>
    /// Возвращает копию последних N лог-событий (от старых к новым).
    /// </summary>
    public IReadOnlyList<LogEvent> GetRecent(int max = RingCapacity)
    {
        lock (_ringLock)
        {
            if (_ringCount == 0) return Array.Empty<LogEvent>();
            var take = Math.Min(max, _ringCount);
            var result = new List<LogEvent>(take);

            int start;
            if (_ringCount < RingCapacity)
            {
                // Ring ещё не заполнен: старейший на 0, новейший на _ringCount-1
                // Берём последние 'take' начиная с (_ringCount - take)
                start = Math.Max(0, _ringCount - take);
            }
            else
            {
                // Ring полный: старейший на _ringHead (куда пишем следующим)
                // Последние 'take' начинаются с (_ringHead - take)
                start = (_ringHead - take + RingCapacity) % RingCapacity;
            }

            for (int i = 0; i < take; i++)
            {
                int idx = (start + i) % RingCapacity;
                var ev = _ringBuffer[idx];
                if (ev != null) result.Add(ev);
            }
            return result;
        }
    }

    public string GetRecentAsText(int max = RingCapacity)
    {
        var sb = new StringBuilder();
        foreach (var ev in GetRecent(max))
            sb.AppendLine(ev.FormattedLine);
        return sb.ToString();
    }

    private void Write(LogLevel level, string category, string message)
    {
        // Подробный (Debug) уровень пишем только в verbose-режиме. На Debug логируются
        // фрагменты распознанной речи (AudioDiagnostics) — по умолчанию их не сохраняем.
        if (level == LogLevel.Debug && !_verbose) return;

        var ev = new LogEvent
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message
        };

        // 1) Debug + Console — мгновенно, без блокировок
        var line = ev.FormattedLine;
        System.Diagnostics.Debug.WriteLine(line);
        Console.WriteLine(line);

        // 2) Ring buffer + событие
        lock (_ringLock)
        {
            _ringBuffer[_ringHead] = ev;
            _ringHead = (_ringHead + 1) % RingCapacity;
            if (_ringCount < RingCapacity) _ringCount++;
        }
        // Копируем делегат локально, чтобы избежать гонки с отпиской
        var handler = LogAppended;
        if (handler != null)
        {
            try { handler.Invoke(this, ev); }
            catch { /* подписчик не должен ломать логгер */ }
        }

        // 3) Файл (best-effort, не падаем). Отключается тумблером «Вести лог-файл».
        if (!_fileEnabled) return;
        try
        {
            lock (_fileLock)
            {
                RotateIfNeeded();
                File.AppendAllText(_currentLogPath, line + Environment.NewLine, Encoding.UTF8);
                _fileWriteFailures = 0;
            }
        }
        catch (Exception writeEx)
        {
            // Лог не должен крашить приложение, но и МОЛЧАТЬ о своей смерти не должен:
            // немой catch уже стоил потерянных сессий при баг-хантинге (файл залочен/недоступен →
            // ни одной строки за всю сессию, и никто об этом не знал).
            HandleFileWriteFailure(writeEx);
        }
    }

    private int _fileWriteFailures;
    private bool _fellBackToTemp;

    // После 3 подряд неудач записи переключаемся на резервный путь в %TEMP% (он практически
    // всегда доступен) и оставляем след об аварии и в резервном файле, и в ring-буфере
    // (виден в окне логов и попадает в диагностический zip).
    private void HandleFileWriteFailure(Exception writeEx)
    {
        lock (_fileLock)
        {
            _fileWriteFailures++;
            if (_fileWriteFailures < 3 || _fellBackToTemp) return;
            _fellBackToTemp = true;
            try
            {
                var fallbackDir = Path.Combine(Path.GetTempPath(), "BrainstormBuddy", "logs");
                Directory.CreateDirectory(fallbackDir);
                var fallback = Path.Combine(fallbackDir, "app.log");
                File.AppendAllText(fallback,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [Error] [Log       ] Основной лог недоступен ({_currentLogPath}): {writeEx.Message} — пишу сюда{Environment.NewLine}",
                    Encoding.UTF8);
                _currentLogPath = fallback;
            }
            catch { /* совсем некуда писать — остаётся ring-буфер */ }
        }

        var failEv = new LogEvent
        {
            Level = LogLevel.Error,
            Category = "Log",
            Message = $"Не могу писать лог-файл: {writeEx.Message}" +
                      (_fellBackToTemp ? $" → резервный путь {_currentLogPath}" : "")
        };
        lock (_ringLock)
        {
            _ringBuffer[_ringHead] = failEv;
            _ringHead = (_ringHead + 1) % RingCapacity;
            if (_ringCount < RingCapacity) _ringCount++;
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_currentLogPath)) return;
            var size = new FileInfo(_currentLogPath).Length;
            if (size < _maxFileSize) return;

            for (int i = _maxFiles - 1; i >= 1; i--)
            {
                var src = i == 1
                    ? _currentLogPath
                    : Path.Combine(_logDir, $"app.{i - 1}.log");
                var dst = Path.Combine(_logDir, $"app.{i}.log");
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
        }
        catch
        {
            // ротация — best effort
        }
    }

    public void Dispose()
    {
        // ничего не делаем — файл уже flushed после каждой записи
    }
}
