using BrainstormBuddy.Core.Mcp;
using Microsoft.Extensions.Logging;

namespace BrainstormBuddy.McpHost;

/// <summary>
/// Провайдер логирования, пишущий в собственный файл журнала хоста
/// ({appData}/logs/mcphost.log) с ротацией. Нужен, чтобы у хоста был
/// свой след вызовов: stdout — это протокол MCP, туда писать нельзя,
/// а stderr клиенты MCP обычно не собирают.
///
/// Уровни: Information → INFO, Warning → WARN, Error/Critical → ERROR,
/// Debug/Trace → DEBUG. Сообщения с форматированными аргументами.
/// </summary>
public sealed class McpHostJournalLoggerProvider : ILoggerProvider
{
    private readonly McpIncomingJournal _journal;

    public McpHostJournalLoggerProvider(McpIncomingJournal journal) => _journal = journal;

    public ILogger CreateLogger(string categoryName) => new JournalLogger(_journal, categoryName);

    public void Dispose() { /* журнал пишет в файл напрямую, освобождать нечего */ }

    private sealed class JournalLogger(McpIncomingJournal journal, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            var line = string.IsNullOrEmpty(text) ? category : $"{category}: {text}";
            switch (logLevel)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    journal.Error(line, exception);
                    break;
                case LogLevel.Warning:
                    journal.Warn(line);
                    break;
                case LogLevel.Debug:
                case LogLevel.Trace:
                    journal.Debug(line);
                    break;
                default:
                    journal.Info(line);
                    break;
            }
        }
    }
}
