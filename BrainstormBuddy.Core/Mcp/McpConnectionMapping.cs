using BrainstormBuddy.Config;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Маппинг McpConnectionEntry (config.json, секреты снаружи) → McpResolvedConnection
/// (проверяемый объект для McpScopeGuard). Парсинг строковых enum-полей —
/// единственная точка: конфиг пишется UI-визардом, значения фиксированы комментами.
/// </summary>
public static class McpConnectionMapping
{
    public static McpResolvedConnection ToResolved(McpConnectionEntry e)
        => new(
            Id: e.Id,
            Name: e.Name,
            Trust: ParseTrust(e.Trust),
            ClientKind: e.ClientKind,
            Scope: ParseScope(e.Scope),
            ScopeIds: e.ScopeIds.ToArray(),
            Rights: e.Rights.Select(ParseRight).Where(r => r.HasValue).Select(r => r!.Value).ToHashSet(),
            MemoryIncluded: e.MemoryIncluded,
            Suspended: e.Suspended);

    public static McpTrustLevel ParseTrust(string s) => s.Trim().ToLowerInvariant() switch
    {
        "own" => McpTrustLevel.Own,
        "trusted-remote" or "trusted_remote" or "trusted" => McpTrustLevel.TrustedRemote,
        "third-party" or "third_party" or "thirdparty" => McpTrustLevel.ThirdParty,
        _ => McpTrustLevel.ThirdParty, // неизвестное — строжайший уровень
    };

    public static McpScopeKind ParseScope(string s) => s.Trim().ToLowerInvariant() switch
    {
        "all" => McpScopeKind.All,
        "all-projects" or "all_projects" => McpScopeKind.AllProjects,
        "existing-projects" or "existing_projects" => McpScopeKind.ExistingProjects,
        "selected-projects" or "selected_projects" => McpScopeKind.SelectedProjects,
        "selected-meetings" or "selected_meetings" => McpScopeKind.SelectedMeetings,
        _ => McpScopeKind.SelectedMeetings, // неизвестное — ничего не покажет без ScopeIds
    };

    public static McpRight? ParseRight(string s) => s.Trim().ToLowerInvariant() switch
    {
        "read" => McpRight.Read,
        "add-fact" or "add_fact" or "addfact" => McpRight.AddFact,
        "change" => McpRight.Change,
        "delete" => McpRight.Delete,
        _ => null, // неизвестные права молча пропускаем — не расширяем
    };

    public static string TrustToString(McpTrustLevel t) => t switch
    {
        McpTrustLevel.Own => "own",
        McpTrustLevel.TrustedRemote => "trusted-remote",
        _ => "third-party",
    };

    public static string ScopeToString(McpScopeKind s) => s switch
    {
        McpScopeKind.All => "all",
        McpScopeKind.AllProjects => "all-projects",
        McpScopeKind.ExistingProjects => "existing-projects",
        McpScopeKind.SelectedProjects => "selected-projects",
        _ => "selected-meetings",
    };

    public static string RightToString(McpRight r) => r switch
    {
        McpRight.Read => "read",
        McpRight.AddFact => "add-fact",
        McpRight.Change => "change",
        _ => "delete",
    };
}
