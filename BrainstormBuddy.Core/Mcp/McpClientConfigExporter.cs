using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Генератор куска конфига для клиентских агентов (шаг 3 мастера
/// «Подключить AI-агент»). Claude Desktop: mcpServers.brainstorm-buddy
/// с command=McpHost.exe и args=[путь к .token-файлу]. ApplyToClaudeDesktop
/// мержит в существующий claude_desktop_config.json, не затирая чужие серверы.
/// </summary>
public static class McpClientConfigExporter
{
    public const string ServerKey = "brainstorm-buddy";
    public const string HostExeName = "BrainstormBuddy.McpHost.exe";

    /// <summary>Путь claude_desktop_config.json текущего пользователя.</summary>
    public static string ClaudeDesktopConfigPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", "claude_desktop_config.json");

    /// <summary>Фрагмент JSON «mcpServers.brainstorm-buddy» — то, что юзер копирует в конфиг клиента.</summary>
    public static string BuildConfigJson(string hostExePath, string tokenFilePath)
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerKey] = BuildServerNode(hostExePath, tokenFilePath),
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Автоматическая настройка Claude Desktop: читает его конфиг (или создаёт),
    /// вставляет/обновляет mcpServers.brainstorm-buddy, сохраняет атомарно.
    /// Возвращает человекочитаемый результат для статуса мастера.
    /// </summary>
    public static string ApplyToClaudeDesktop(string hostExePath, string tokenFilePath)
        => ApplyToPath(ClaudeDesktopConfigPath, hostExePath, tokenFilePath);

    /// <summary>То же, что ApplyToClaudeDesktop, но в произвольный файл (тесты, другие клиенты).</summary>
    public static string ApplyToPath(string path, string hostExePath, string tokenFilePath)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        var existed = servers.ContainsKey(ServerKey);
        servers[ServerKey] = BuildServerNode(hostExePath, tokenFilePath);
        AtomicFileWriter.WriteText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return existed ? "Обновил запись в claude_desktop_config.json" : "Добавил запись в claude_desktop_config.json";
    }

    private static JsonObject BuildServerNode(string hostExePath, string tokenFilePath) => new()
    {
        ["command"] = hostExePath,
        // Токен не в командной строке: хост читает файл при старте,
        // token-файл доступен только текущему пользователю (DPAPI-стор).
        ["args"] = new JsonArray { tokenFilePath },
    };

    /// <summary>
    /// Путь <c>BrainstormBuddy.McpHost.exe</c> для конфига клиента.
    /// Приоритет — постоянный путь установленной копии: клиент запускает хост
    /// при каждом старте, и рабочая папка сборки (bin\, publish\) туда не годится.
    /// Инсталлятор ставит Buddy в {autopf}\BrainstormBuddy: при установке без
    /// прав администратора это %LOCALAPPDATA%\Programs\BrainstormBuddy, при
    /// установке от администратора — Program Files. Проверяем оба, затем
    /// Program Files (x86). Если установленной копии нет — хост рядом с
    /// запущенным приложением.
    /// </summary>
    public static string ResolveHostExePath()
    {
        foreach (var root in new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var installed = Path.Combine(root, "BrainstormBuddy", HostExeName);
            if (File.Exists(installed)) return installed;
        }
        return Path.Combine(AppContext.BaseDirectory, HostExeName);
    }
}
