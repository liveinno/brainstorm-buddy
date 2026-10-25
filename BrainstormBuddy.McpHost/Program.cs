using BrainstormBuddy.Core.Mcp;
using BrainstormBuddy.McpHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// BrainstormBuddy.McpHost — stdio MCP-сервер. ВНИМАНИЕ: stdout — это протокол,
// туда нельзя писать НИЧЕГО кроме JSON-RPC фреймов. Все логи — только stderr.
//
// Использование (из конфига MCP-клиента, напр. claude_desktop_config.json):
//   BrainstormBuddy.McpHost.exe <token-file> [--pipe <name>]
// token-file пишет Buddy при выдаче подключения (mcp/tokens/{id}.token).

if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
{
    Console.Error.WriteLine("usage: BrainstormBuddy.McpHost.exe <token-file> [--pipe <name>]");
    Console.Error.WriteLine("  token-file: path to the connection token issued by BrainstormBuddy");
    Console.Error.WriteLine("  --pipe:     override named pipe name (default buddy-mcp-{UserSid})");
    return args.Length == 0 ? 2 : 0;
}

var tokenFile = args[0];
string? pipeName = null;
for (var i = 1; i + 1 < args.Length; i++)
    if (args[i] == "--pipe") pipeName = args[i + 1];

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    // Не читаем DOTNET_*/ASPNETCORE_* env и лишние конфиги — сервер должен быть
    // детерминированным в любой среде клиента.
    Args = [],
    DisableDefaults = true,
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Собственный файл журнала хоста: {appData}/logs/mcphost.log (ротация внутри).
// stdout остаётся чистым протоколом MCP, stderr — для клиента, файл — для нас.
var dataDir = McpIncomingJournal.ResolveDataDirFromTokenFile(tokenFile)
              ?? Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                  "BrainstormBuddy");
var journal = new McpIncomingJournal(Path.Combine(dataDir, "logs"), "mcphost.log");
builder.Logging.AddProvider(new McpHostJournalLoggerProvider(journal));
builder.Services.AddSingleton(journal);

builder.Services.AddSingleton(new BuddyPipeClient(tokenFile, pipeName, journal));
builder.Services.AddSingleton<BuddyTools>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();
journal.Info($"mcphost started token={McpIncomingJournal.Mask(Path.GetFileName(tokenFile))} pipe={pipeName ?? "default"}");
try
{
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    journal.Error("mcphost fatal", ex);
    Console.Error.WriteLine($"fatal: {ex.Message}");
    return 1;
}
