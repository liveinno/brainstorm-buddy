using System.Security.Principal;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Имя named pipe входящего MCP-канала: buddy-mcp-{UserSid}. SID (S-1-5-21-…)
/// стабилен между перезапусками и не зависит от переименования пользователя;
/// ACL PipeOptions.CurrentUserOnly ограничивает доступ владельцем процесса.
/// Общая точка для сервера (McpIncomingHost) и клиента (McpHost.exe) —
/// несовпадение имён исключено.
/// </summary>
public static class McpPipeNaming
{
    public const string Prefix = "buddy-mcp-";

    public static string GetDefaultPipeName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var sid = WindowsIdentity.GetCurrent().User?.Value;
                if (!string.IsNullOrEmpty(sid))
                    return Prefix + sid;
            }
            catch { /* WindowsIdentity недоступен (restriced token) — fallback ниже */ }
        }
        // Fallback: имя пользователя, допустимые для pipe символы.
        var user = Environment.UserName;
        var chars = user.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
        return Prefix + "u-" + new string(chars);
    }
}
