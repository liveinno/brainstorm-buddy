using System.Text;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Mcp.Http;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>Уровень одной проверки диагностики MCP.</summary>
public enum McpDiagLevel
{
    Ok,
    Warn,
    Fail,
}

/// <summary>Результат одной проверки: заголовок + подробности (пути, токен, права).</summary>
public sealed record McpDiagItem(McpDiagLevel Level, string Title, string Detail);

/// <summary>Сводка проверок MCP.</summary>
public sealed record McpDiagReport(IReadOnlyList<McpDiagItem> Items)
{
    public int Failures => Items.Count(i => i.Level == McpDiagLevel.Fail);
    public int Warnings => Items.Count(i => i.Level == McpDiagLevel.Warn);
    public bool Ok => Failures == 0;

    public string Summary => Failures > 0
        ? $"Проблем: {Failures}, предупреждений: {Warnings}"
        : Warnings > 0
            ? $"Готово, предупреждений: {Warnings}"
            : "Всё в порядке";

    /// <summary>Человекочитаемый отчёт для окна настроек.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var i in Items)
        {
            var mark = i.Level switch
            {
                McpDiagLevel.Ok => "OK  ",
                McpDiagLevel.Warn => "WARN",
                _ => "FAIL",
            };
            sb.Append(mark).Append(" | ").Append(i.Title);
            if (!string.IsNullOrWhiteSpace(i.Detail))
                sb.Append(" — ").Append(i.Detail);
            sb.AppendLine();
        }
        sb.Append(Summary);
        return sb.ToString();
    }
}

/// <summary>
/// Проверка живости входящего MCP-канала: что включено, какой канал слушает,
/// какие подключения и токены выданы, где журнал и данные. Ничего не меняет
/// и не перезапускает — только читает состояние.
/// </summary>
public static class McpDiagnostics
{
    /// <summary>Собрать отчёт по текущему состоянию MCP.</summary>
    public static McpDiagReport Run(
        McpConfig config,
        McpIncomingHost? host,
        McpPermissionStore? permissions,
        McpIncomingJournal? journal,
        string appDataDir)
    {
        var items = new List<McpDiagItem>();

        // 1. Включён ли MCP и слушает ли канал.
        if (!config.Enabled)
            items.Add(new McpDiagItem(McpDiagLevel.Warn, "MCP выключен",
                "Включите «MCP — подключения и инструменты», чтобы принимать вызовы агентов."));
        else if (config.StdioHostEnabled)
            items.Add(new McpDiagItem(McpDiagLevel.Ok, "MCP включён",
                "входящий канал (named pipe) + исходящие инструменты"));
        else
            items.Add(new McpDiagItem(McpDiagLevel.Warn, "MCP включён",
                "входящий named-pipe-хост отключён (StdioHostEnabled=false)"));

        // 2. Канал.
        if (host == null)
            items.Add(new McpDiagItem(
                config.Enabled ? McpDiagLevel.Fail : McpDiagLevel.Warn,
                "Канал не запущен",
                "named pipe не слушается: вызовы агента не пройдут"));
        else
            items.Add(new McpDiagItem(McpDiagLevel.Ok, "Канал слушает",
                $"named pipe: {host.PipeName}"));

        // 3. HTTP-вход.
        if (config.HttpEnabled)
            items.Add(new McpDiagItem(McpDiagLevel.Ok, "HTTP-вход включён",
                $"http://127.0.0.1:{config.HttpPort}"));
        else
            items.Add(new McpDiagItem(McpDiagLevel.Warn, "HTTP-вход выключен",
                $"порт {config.HttpPort} (облачный доступ в настройках не активен)"));

        // 4. Подключения: права, область, токен.
        if (config.Connections.Count == 0)
        {
            items.Add(new McpDiagItem(McpDiagLevel.Warn, "Подключений нет",
                " ни один агент ещё не подключён к Buddy"));
        }
        else
        {
            foreach (var c in config.Connections)
            {
                var name = string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name;
                var rights = c.Rights.Count == 0 ? "нет прав" : string.Join(", ", c.Rights);
                var scope = c.Scope;
                var mem = c.MemoryIncluded ? " + память" : "";
                var detail = $"права: {rights}; область: {scope}{mem}";
                if (c.Suspended)
                    items.Add(new McpDiagItem(McpDiagLevel.Warn, $"Подключение «{name}» приостановлено", detail));
                else
                    items.Add(new McpDiagItem(McpDiagLevel.Ok, $"Подключение «{name}»", detail));

                // Токен: выдан? где лежит? (сам токен не показываем — только маску)
                if (permissions == null)
                    continue;
                bool granted;
                try { granted = permissions.HasGrant(c.Id); }
                catch (Exception ex) { granted = false; AddTokenError(items, name, ex); continue; }
                if (!granted)
                {
                    items.Add(new McpDiagItem(McpDiagLevel.Warn, $"Токен «{name}» не выдан",
                        "агент не сможет пройти авторизацию — выдайте токен заново"));
                    continue;
                }
                var token = TryReveal(permissions, c.Id);
                var tokenPath = TryTokenPath(permissions, c.Id);
                var tokenDetail = token == null
                    // DPAPI-блока нет (грант выдан сторонним инструментом) - честно говорим об этом.
                    ? "маска недоступна (в файле прав нет DPAPI-блока)"
                    : $"…{Mask(token)}";
                if (tokenPath != null) tokenDetail += $"; файл: {tokenPath}";
                items.Add(new McpDiagItem(McpDiagLevel.Ok, $"Токен «{name}» выдан", tokenDetail));
            }
        }

        // 5. Журнал: путь, размер, свежесть.
        if (journal == null)
            items.Add(new McpDiagItem(McpDiagLevel.Warn, "Журнал недоступен", ""));
        else
        {
            var path = journal.FilePath;
            string detail;
            var level = McpDiagLevel.Ok;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) detail = $"файл ещё не создан: {path}";
                else
                    detail = $"{path} ({fi.Length / 1024} КБ, изм. {fi.LastWriteTime:yyyy-MM-dd HH:mm})";
            }
            catch (Exception ex)
            {
                level = McpDiagLevel.Warn;
                detail = $"{path} — {ex.Message}";
            }
            items.Add(new McpDiagItem(level, "Журнал вызовов", detail));
        }

        // 5b. Журнал McpHost (сторона stdio-клиента): каталог данных хост
        // выводит из пути к файлу токена, поэтому ищем его там же.
        var hostLog = Path.Combine(ResolveHostDataDir(config, permissions, appDataDir), "logs", "mcphost.log");
        items.Add(new McpDiagItem(McpDiagLevel.Ok, "Журнал McpHost", DescribeFile(hostLog)));

        // 6. Поверхность инструментов для агента.
        var tools = McpHttpToolBridge.ToolList;
        items.Add(new McpDiagItem(McpDiagLevel.Ok, "Инструменты",
            $"{tools.Count} шт.: {string.Join(", ", tools.Select(t => t.Name))}"));

        // 7. Пути данных.
        var paths = new List<string> { $"данные: {appDataDir}" };
        if (permissions != null)
        {
            paths.Add($"права: {permissions.FilePath}");
            paths.Add($"токены: {permissions.McpDirectory}");
        }
        if (journal != null) paths.Add($"журнал: {journal.LogDirectory}");
        items.Add(new McpDiagItem(McpDiagLevel.Ok, "Пути", string.Join("; ", paths)));

        return new McpDiagReport(items);
    }

    private static void AddTokenError(List<McpDiagItem> items, string name, Exception ex)
        => items.Add(new McpDiagItem(McpDiagLevel.Warn, $"Токен «{name}»: ошибка чтения", ex.Message));

    /// <summary>
    /// Каталог данных McpHost: хост выводит его из пути к файлу токена
    /// ({appData}/mcp/tokens/{id}.token → {appData}); если токенов нет - берём
    /// каталог приложения.
    /// </summary>
    private static string ResolveHostDataDir(
        McpConfig config, McpPermissionStore? permissions, string appDataDir)
    {
        if (permissions != null)
        {
            foreach (var c in config.Connections)
            {
                var tokenPath = TryTokenPath(permissions, c.Id);
                if (tokenPath == null) continue;
                var resolved = McpIncomingJournal.ResolveDataDirFromTokenFile(tokenPath);
                if (resolved != null) return resolved;
            }
        }
        return appDataDir;
    }

    /// <summary>Описание файла журнала: путь, размер, время изменения.</summary>
    private static string DescribeFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return $"{path} (файл ещё не создан)";
            return $"{path} ({fi.Length / 1024} КБ, изм. {fi.LastWriteTime:yyyy-MM-dd HH:mm})";
        }
        catch (Exception ex)
        {
            return $"{path} — {ex.Message}";
        }
    }

    private static string? TryReveal(McpPermissionStore store, string connectionId)
    {
        try { return store.TryRevealToken(connectionId); }
        catch { return null; }
    }

    private static string? TryTokenPath(McpPermissionStore store, string connectionId)
    {
        try { return store.TokenFilePath(connectionId); }
        catch { return null; }
    }

    /// <summary>Маска токена для показа: первые/последние символы не публикуются целиком.</summary>
    public static string Mask(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "нет";
        if (token.Length <= 8) return new string('•', token.Length);
        return $"••••{token[^4..]}";
    }
}
