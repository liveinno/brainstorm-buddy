using System;
using System.Windows;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Mcp;

namespace BrainstormBuddy;

/// <summary>
/// Локализация MCP-статусов для UI: технические id (scope, transport, kind,
/// enum McpJobState) → строки ресурсов ru/en. Все методы безопасны к
/// отсутствию ключа — возвращают fallback, а не бросают.
/// </summary>
public static class McpStatusLocalizer
{
    public static string T(string key, string fallback) =>
        System.Windows.Application.Current.TryFindResource(key) as string ?? fallback;

    /// <summary>Имя подключения: пустое/техническое → «Мой агент», пользовательское не трогаем.</summary>
    public static string ConnectionName(string? name, string? clientKind)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        return clientKind switch
        {
            "claude-desktop" => "Claude Desktop",
            _ => T("L_McpW_DefaultAgent", "Мой агент"),
        };
    }

    /// <summary>scope-id → подпись («all-projects» → «Все проекты»).</summary>
    public static string Scope(string? scope) => scope switch
    {
        "all-projects" => T("L_Mcp_Scope_AllProjects", "Все проекты"),
        "existing-projects" => T("L_Mcp_Scope_Existing", "Существующие проекты"),
        "selected-projects" => T("L_Mcp_Scope_SelProjects", "Выбранные проекты"),
        "selected-meetings" => T("L_Mcp_Scope_SelMeetings", "Выбранные встречи"),
        "all" => T("L_Mcp_Scope_All", "Всё (проекты + память)"),
        _ when string.IsNullOrWhiteSpace(scope) => T("L_Mcp_Scope_AllProjects", "Все проекты"),
        _ => scope!,
    };

    /// <summary>transport-id → подпись.</summary>
    public static string Transport(string? transport) => transport switch
    {
        "stdio" => T("L_Mcp_Transport_Stdio", "Локальный процесс (stdio)"),
        "http" or "http+sse" or "streamable-http" => T("L_Mcp_Transport_Http", "HTTP-сервер"),
        _ when string.IsNullOrWhiteSpace(transport) => "",
        _ => transport!,
    };

    /// <summary>kind-id задания → подпись.</summary>
    public static string JobKind(string? kind) => kind switch
    {
        "search" => T("L_Mcp_JobKind_Search", "Поиск"),
        "research" => T("L_Mcp_JobKind_Research", "Исследование"),
        "tool-call" => T("L_Mcp_JobKind_ToolCall", "Вызов инструмента"),
        "extract" => T("L_Mcp_JobKind_Extract", "Извлечение фактов"),
        "transcribe" => T("L_Mcp_JobKind_Transcribe", "Транскрибация"),
        "custom" => T("L_Mcp_JobKind_Custom", "Задача"),
        _ when string.IsNullOrWhiteSpace(kind) => T("L_Mcp_JobKind_Custom", "Задача"),
        _ => kind!,
    };

    /// <summary>McpJobState → подпись (никаких enum-литералов в UI).</summary>
    public static string JobState(McpJobState state) => state switch
    {
        McpJobState.Queued => T("L_Mcp_Job_Queued", "В очереди"),
        McpJobState.Running => T("L_Mcp_Job_Running", "Выполняется"),
        McpJobState.WaitingApproval => T("L_Mcp_Job_WaitingApproval", "Ждёт подтверждения"),
        McpJobState.WaitingBudget => T("L_Mcp_Job_WaitingBudget", "Ждёт бюджета"),
        McpJobState.WaitingConnection => T("L_Mcp_Job_WaitingConnection", "Ждёт соединения"),
        McpJobState.Paused => T("L_Mcp_Job_Paused", "На паузе"),
        McpJobState.Succeeded => T("L_Mcp_Job_Succeeded", "Завершено"),
        McpJobState.Failed => T("L_Mcp_Job_Failed", "Ошибка"),
        McpJobState.Cancelled => T("L_Mcp_Job_Cancelled", "Отменено"),
        McpJobState.NeedsAttention => T("L_Mcp_Job_NeedsAttention", "Требует внимания"),
        _ => state.ToString(),
    };

    /// <summary>
    /// Честный статус подключения (спека §6.1): «Подключено» — только после
    /// реального обращения клиента (lastRequestUtc / LastSeenUtc не null).
    /// Возвращает текст и ключ кисти темы для бейджа.
    /// </summary>
    /// <param name="suspended">Доступ отозван/приостановлен.</param>
    /// <param name="lastRequestUtc">Время последнего реального запроса клиента
    /// (StatusInfo.LastRequestUtc / McpConnectionEntry.LastSeenUtc).</param>
    /// <param name="listenerError">Ошибка слушателя, если хост упал (null = слушатель жив или неизвестно).</param>
    public static (string Text, string BrushKey) FormatConnectionStatus(
        bool suspended, DateTime? lastRequestUtc, string? listenerError = null)
    {
        if (suspended)
            return (T("L_Mcp_Status_Revoked", "Доступ отозван"), "ErrorBrush");
        if (listenerError != null)
            return (string.Format(T("L_Mcp_Status_NoLinkFmt", "Нет связи — {0}"), listenerError), "ErrorBrush");
        if (lastRequestUtc == null)
            return (T("L_Mcp_Status_Waiting", "Ожидаем подключение агента"), "TextMutedBrush");
        var local = lastRequestUtc.Value.ToLocalTime();
        return (string.Format(T("L_Mcp_Status_ConnectedFmt", "Подключено · {0:dd.MM.yyyy HH:mm}"), local),
                "SuccessBrush");
    }

    /// <summary>Статус входящего подключения из записи конфига.</summary>
    public static string FormatConnectionStatus(McpConnectionEntry c)
        => FormatConnectionStatus(c.Suspended, c.LastSeenUtc).Text;

    /// <summary>
    /// Статус по ЖИВОМУ состоянию фасада (спека §6.1): «Подключено» — только
    /// после реального запроса клиента. Вид: ok|warn|error|muted — по нему UI
    /// красит строку статуса.
    /// </summary>
    public static (string Text, string Kind) FormatConnectionStatus(McpConnectionStatus st)
    {
        switch (st.Presence)
        {
            case McpConnectionPresence.Connected:
                return st.LastRequestUtc is { } t
                    ? (string.Format(T("L_Mcp_Status_ConnectedFmt", "Подключено · {0:dd.MM.yyyy HH:mm}"),
                        t.ToLocalTime()), "ok")
                    : (T("L_Mcp_Status_Connected", "Подключено"), "ok");
            case McpConnectionPresence.Revoked:
                return (T("L_Mcp_Status_Revoked", "Доступ отозван"), "error");
            case McpConnectionPresence.NoConnection:
                return (string.Format(T("L_Mcp_Status_NoLinkFmt", "Нет связи — {0}"),
                    st.DownReason ?? T("L_Mcp_Status_NoLinkReason", "причина неизвестна")), "error");
            default:
                return (T("L_Mcp_Status_Waiting", "Ожидаем подключение агента"), "muted");
        }
    }

    /// <summary>Краткое время запроса для карточки pending-клиента.</summary>
    public static string PendingSince(DateTime requestedUtc)
        => string.Format(T("L_Mcp_PendingSince", "запрошен {0}"), requestedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
}

/// <summary>
/// UI-строка pending OAuth-клиента. Контракт McpPendingClient (PendingId,
/// ClientId, ClientName?, RedirectUri, Scope, RequestedUtc) достраивает
/// агент McpHttp — From принимает поля параметрами, чтобы строка не имела
/// compile-зависимости на ещё не существующий тип.
/// </summary>
public sealed class McpPendingUiRow
{
    public string PendingId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string RedirectUri { get; init; } = "";
    public string RequestedText { get; init; } = "";

    public static McpPendingUiRow From(
        string pendingId, string clientId, string? clientName,
        string redirectUri, string? scope, DateTime requestedUtc)
    {
        var name = string.IsNullOrWhiteSpace(clientName)
            ? McpStatusLocalizer.T("L_Mcp_PendingNewClient", "Новый клиент")
            : clientName.Trim();
        var scopeSuffix = string.IsNullOrWhiteSpace(scope)
            ? ""
            : $" · {McpStatusLocalizer.Scope(scope)}";
        return new McpPendingUiRow
        {
            PendingId = pendingId,
            DisplayName = $"{name} ({clientId}){scopeSuffix}",
            RedirectUri = redirectUri,
            RequestedText = McpStatusLocalizer.PendingSince(requestedUtc),
        };
    }
}