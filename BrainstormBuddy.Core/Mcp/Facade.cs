using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>Разрешение, выданное подключению. Хранит ссылку на запись в config + проверки.</summary>
public sealed record McpResolvedConnection(
    string Id,
    string Name,
    McpTrustLevel Trust,
    string ClientKind,
    McpScopeKind Scope,
    IReadOnlyList<long> ScopeIds,
    IReadOnlySet<McpRight> Rights,
    bool MemoryIncluded,
    bool Suspended);

/// <summary>Проверка прав подключения. Вызывается ПЕРЕД каждым обращением к данным.</summary>
public static class McpScopeGuard
{
    /// <summary>
    /// Попадает ли сущность в область подключения — без проверки права read.
    /// Операции записи (добавление/изменение/удаление) требуют области, но не
    /// права read: подключение с правом add-fact пишет в память, не читая её.
    /// </summary>
    public static bool CheckScope(McpResolvedConnection c, long? projectId, long? meetingId)
    {
        if (c.Suspended) return false;
        return c.Scope switch
        {
            McpScopeKind.All => true,
            McpScopeKind.AllProjects => true,
            McpScopeKind.ExistingProjects => projectId == null || c.ScopeIds.Contains(projectId.Value),
            McpScopeKind.SelectedProjects => projectId != null && c.ScopeIds.Contains(projectId.Value),
            McpScopeKind.SelectedMeetings => meetingId != null && c.ScopeIds.Contains(meetingId.Value),
            _ => false,
        };
    }

    /// <summary>Чтение: право read И попадание в область подключения.</summary>
    public static bool CheckRead(McpResolvedConnection c, long? projectId, long? meetingId)
        => c.Rights.Contains(McpRight.Read) && CheckScope(c, projectId, meetingId);

    public static bool CheckRight(McpResolvedConnection c, McpRight right)
        => c.Rights.Contains(right) && !c.Suspended;

    /// <summary>Требует ли действие подтверждения при данном уровне доверия.</summary>
    public static bool NeedsApproval(McpResolvedConnection c, McpActionRisk risk)
    {
        if (risk == McpActionRisk.ReadOnly) return false;
        if (risk == McpActionRisk.Additive) return c.Trust == McpTrustLevel.ThirdParty;
        // Mutating/Destructive: third-party всегда, trusted — по списку, own — только destructive.
        return c.Trust switch
        {
            McpTrustLevel.Own => risk == McpActionRisk.Destructive,
            McpTrustLevel.TrustedRemote => risk >= McpActionRisk.Mutating,
            McpTrustLevel.ThirdParty => risk >= McpActionRisk.Additive,
            _ => true,
        };
    }
}

/// <summary>Запись фонового задания (MCP исследование, исходящая tool-задача и т.п.).</summary>
public sealed record McpJob
{
    public string Id { get; init; } = "";
    public string ConnectionId { get; init; } = "";
    /// <summary>research|search|transcribe|extract_facts|tool-call|custom</summary>
    public string Kind { get; init; } = "";
    /// <summary>Строкой по проводу ("Queued"), не числом — читаемость для AI-клиентов.
    /// Числа из старых журналов десериализуются (AllowIntegerValues по умолчанию).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<McpJobState>))]
    public McpJobState State { get; set; } = McpJobState.Queued;
    /// <summary>Человекочитаемая стадия («Ищу по встречам», «Изучено 18/46»).</summary>
    public string? Stage { get; set; }
    public int? Done { get; set; }
    public int? Total { get; set; }
    public string? ArgsJson { get; init; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public string? IdempotencyKey { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Бюджетные/прогресс-снимки для возобновления после рестарта.</summary>
    public string? ResumeToken { get; set; }
    /// <summary>Непрозрачный чекпоинт runner'а (JSON): позиция обхода,
    /// промежуточные итоги. Пишется McpJobStore.Update(checkpoint:…); смысл
    /// определяет исполнитель kind'а. Requeue сохраняет — resume продолжит с места.</summary>
    public string? Checkpoint { get; set; }
    /// <summary>Job переживает рестарт: Running при смерти процесса при реплее
    /// журнала → WaitingResume (или Queued при AutoResume), а не Failed.</summary>
    public bool Resumable { get; init; }
    /// <summary>true → после рестарта вернуться в Queued без вопроса пользователю
    /// (автоконтину разрешён постановщиком). Иначе — WaitingResume.</summary>
    public bool AutoResume { get; init; }
}

/// <summary>Фасад данных Buddy для MCP-входов (stdio pipe + HTTP). Единственная точка доступа.</summary>
public interface IMcpFacade
{
    Task<McpServerStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<McpProjectInfo>> ListProjectsAsync(McpResolvedConnection c, CancellationToken ct);
    Task<IReadOnlyList<McpMeetingInfo>> ListMeetingsAsync(McpResolvedConnection c, long? projectId, int limit, string? cursor, CancellationToken ct);
    Task<McpSearchResult> SearchAsync(McpResolvedConnection c, string query, McpSearchTypes types, long[]? projectIds, int limit, CancellationToken ct);
    Task<McpSourceContent> ReadSourceAsync(McpResolvedConnection c, string reference, CancellationToken ct);
    Task<McpJob> EnqueueJobAsync(McpResolvedConnection c, string kind, string argsJson, string idempotencyKey, CancellationToken ct);
    Task<McpJob?> GetJobAsync(McpResolvedConnection c, string jobId, CancellationToken ct);
    Task<bool> CancelJobAsync(McpResolvedConnection c, string jobId, CancellationToken ct);

    // ── пагинация/полнота + документы/факты/jobs (спека §6.3–6.4) ──────────

    /// <summary>Расширенный поиск: mode/filters/cursor. Базовый SearchAsync делегирует сюда.</summary>
    Task<McpSearchResult> SearchAsync(McpResolvedConnection c, McpSearchRequest req, CancellationToken ct);

    /// <summary>Постраничное чтение источника: offset/cursor + лимит байт UTF-8.
    /// cursor — непрозрачная позиция из прошлого ответа (nextCursor); приоритет над offset.
    /// В ответе HasMore/NextCursor/TotalChars; недоступный хвост — Truncated+TruncatedReason.</summary>
    Task<McpSourceContent> ReadSourcePageAsync(McpResolvedConnection c, string reference,
        long? offset, string? cursor, int? limitBytes, CancellationToken ct);

    /// <summary>Вложения встречи + статус извлечённого текста. meetingRef:
    /// "meeting:{id}", числовой id встречи или journal id файла.</summary>
    Task<IReadOnlyList<McpMeetingDocumentItem>> ListMeetingDocumentsAsync(
        McpResolvedConnection c, string meetingRef, CancellationToken ct);

    /// <summary>Идемпотентное добавление факта: повтор того же текста в тот же
    /// scope возвращает существующую запись (Duplicate=true).</summary>
    Task<McpFactAddResult> AddFactAsync(McpResolvedConnection c, string text,
        string? sourceRef, long? projectId, long? meetingId, CancellationToken ct);

    /// <summary>
    /// Изменение текста факта по его id (buddy_update_fact). Требует права
    /// change; факт должен попадать в область подключения, иначе отказ.
    /// </summary>
    Task<McpFactUpdateResult> UpdateFactAsync(McpResolvedConnection c, string factId,
        string text, CancellationToken ct);

    /// <summary>
    /// Удаление факта по его id (buddy_delete_fact): мягкое — статус меняется
    /// на «недействителен». Требует права delete, попадания в область и
    /// явного подтверждения владельца (SetApprovalHandler); без подтверждения
    /// удаление отклоняется, факт остаётся.
    /// </summary>
    Task<McpFactDeleteResult> DeleteFactAsync(McpResolvedConnection c, string factId,
        CancellationToken ct);

    /// <summary>
    /// Делегат подтверждения опасных операций (удаление факта) — карточка
    /// «Разрешить/Отклонить» в оверлее. null — подтверждение недоступно,
    /// удаление отклоняется явным отказом, а не выполняется молча.
    /// </summary>
    void SetApprovalHandler(Func<McpApprovalRequest, Task<bool>>? handler);

    /// <summary>Повторная постановка задания (Failed/Cancelled/Paused/NeedsAttention → Queued).
    /// Чужая джоба → ScopeDenied; нет такой → null; уже активна → текущая запись.</summary>
    Task<McpJob?> ResumeJobAsync(McpResolvedConnection c, string jobId, CancellationToken ct);

    /// <summary>Чтение сохранённого полного результата инструмента (tool-result store):
    /// id — из указателя обрезки, offset — позиция в символах, limitBytes — бюджет UTF-8.</summary>
    Task<McpToolResultPage> ReadToolResultAsync(McpResolvedConnection c, string id,
        long offset, int? limitBytes, CancellationToken ct);

    // ── входящий HTTP + карточки состояния (спека §6.1) ────────────────────

    /// <summary>
    /// Общий JSON-RPC dispatch для HTTP-транспорта: та же маршрутизация
    /// facade.*/auth.resolve, что и у named pipe. conn — подключение, уже
    /// извлечённое из Bearer access token (params.connectionToken не нужен).
    /// </summary>
    Task<string?> TryDispatchHttpJsonRpcAsync(string json, McpResolvedConnection conn, CancellationToken ct);

    /// <summary>Вызов facade-метода («listProjects» или «facade.listProjects») с готовым подключением.</summary>
    Task<JsonElement> InvokeFacadeAsync(string method, JsonElement prms, McpResolvedConnection conn, CancellationToken ct);

    /// <summary>Handshake/любой аутентичный контакт: transport — «pipe»|«http».</summary>
    void UpdateConnectionLastSeen(string connectionId, string transport);

    /// <summary>Реальный запрос после handshake — обновляет «время последнего обращения».</summary>
    void SetLastRequest(string connectionId);

    /// <summary>
    /// Реальный запрос клиента (после handshake) — сигнал UI: карточка
    /// подключения должна показать «Подключено» и время обращения.
    /// Параметры: id подключения, UTC момента запроса.
    /// </summary>
    event Action<string, DateTime>? RequestObserved;

    /// <summary>Состояния всех сконфигурированных входящих подключений.</summary>
    IReadOnlyList<McpConnectionStatus> GetConnectionStatuses();

    /// <summary>Состояние HTTP-ingress/tunnel (listener упал → причина в DownReason).</summary>
    McpHttpStatus GetHttpStatus();

    /// <summary>Транспорты сообщают сюда своё здоровье; «Подключено» этим не ставится.</summary>
    void ReportTransportState(string transport, bool alive, string? reason = null);

    // ── OAuth consent gate (HTTP first-contact, спека §6.1 + advisory) ──────

    /// <summary>
    /// Подключение источника pending-консентов HTTP-слушателем при старте
    /// (ingress → auth store). Повторный вызов заменяет источник; null — снять.
    /// </summary>
    void SetPendingClientSource(IPendingClientSource? source);

    /// <summary>Ожидающие решения владельца OAuth-клиенты (первый контакт).</summary>
    IReadOnlyList<McpPendingClient> ListPendingClients();

    /// <summary>
    /// Решение владельца по pending-консенту: approve → клиент запоминается
    /// (approvedClients) и поллинг /oauth/consent-status вернёт code-редирект;
    /// deny → редирект с error=access_denied. false — неизвестный id или решение
    /// уже принято.
    /// </summary>
    bool ResolvePendingClient(string pendingId, bool approve);

    /// <summary>Новый pending-консент (сигнал UI: тост + карточка в Настройки→MCP).</summary>
    event Action<McpPendingClient>? PendingClientAdded;
}

/// <summary>Состояние входящего подключения для карточки (спека §6.1).</summary>
public enum McpConnectionPresence
{
    /// <summary>Конфигурация выдана, реальных обращений ещё не было.</summary>
    AwaitingConnection,
    /// <summary>Handshake завершён и был разрешённый запрос.</summary>
    Connected,
    /// <summary>Связь потеряна — причина в McpConnectionStatus.DownReason.</summary>
    NoConnection,
    /// <summary>Доступ отозван/токена больше нет — старое разрешение не работает.</summary>
    Revoked,
}


/// <summary>OAuth-клиент, запросивший доступ первый раз: ждёт решения владельца.</summary>
public sealed record McpPendingClient(
    string PendingId,
    string ClientId,
    string? ClientName,
    string RedirectUri,
    /// <summary>Человекочитаемое описание scope: «{имя подключения} ({scope})».</summary>
    string Scope,
    DateTime RequestedUtc);

/// <summary>
/// Источник pending-консентов (реализация — McpHttpAuthStore). Нужен фасаду,
/// чтобы UI видел запросы без прямой зависимости от HTTP-слоя.
/// </summary>
public interface IPendingClientSource
{
    IReadOnlyList<McpPendingClient> ListPending();
    /// <summary>Решение владельца: approve → клиент approved + pending получает code-редирект.</summary>
    bool ResolvePending(string pendingId, bool approve);
    event Action<McpPendingClient>? PendingAdded;
}
/// <summary>Карточка состояния входящего подключения: имя, транспорт, реальное обращение.</summary>
public sealed record McpConnectionStatus(
    string Id,
    string Name,
    /// <summary>pipe|http|unknown — каким каналом шли реальные обращения.</summary>
    string Transport,
    McpConnectionPresence Presence,
    /// <summary>UTC последнего реального запроса (после handshake), null — ещё не было.</summary>
    DateTime? LastRequestUtc,
    /// <summary>Причина при Presence = NoConnection, иначе null.</summary>
    string? DownReason);

/// <summary>Живое состояние входящего HTTP-сервера для статуса/карточек.</summary>
public sealed record McpHttpStatus(
    bool ListenerRunning,
    string? LocalBaseUrl,
    /// <summary>Публичный URL туннеля (funnel/manual), null — нет внешнего доступа.</summary>
    string? PublicBaseUrl,
    /// <summary>off|tailscale-funnel|manual.</summary>
    string TunnelMode,
    /// <summary>Причина недоступности (listener упал, tailscale не найден и т.п.).</summary>
    string? DownReason);

[Flags]
public enum McpSearchTypes { Meetings = 1, Memory = 2, Files = 4, Projects = 8, All = Meetings | Memory | Files | Projects }

public sealed record McpServerStatus(
    string Version, string ProfileName, bool HttpEnabled, bool TunnelActive,
    int ConnectionsCount, int PendingApprovals, DateTime UtcNow);

public sealed record McpProjectInfo(long Id, string Name, string? Description, int MeetingCount, DateTime CreatedUtc);

public sealed record McpMeetingInfo(
    long Id, long? ProjectId, string Title, DateTime StartedUtc, int DurationSec,
    int SegmentCount, int AttachmentCount, string[] Tags);

public sealed record McpSearchResult(
    IReadOnlyList<McpSearchHit> Hits, string? NextCursor, int TotalScanned,
    /// <summary>true — обошли весь доступный индекс; false — покрытие неполное
    /// (IncompleteReason поясняет), «ничего не найдено» безусловным не считать.</summary>
    bool Exhaustive = true,
    /// <summary>"memory-disabled"|"meeting-text-capped"|"fts-partial" и т.п.</summary>
    string? IncompleteReason = null,
    /// <summary>Всего совпадений до обрезки лимитом. В mode=all счётчик
    /// накапливается по страницам: на последней странице — полное число
    /// совпадений, на промежуточных — найденное на этот момент.</summary>
    int? TotalMatches = null,
    /// <summary>Есть ли ещё выдача по NextCursor.</summary>
    bool HasMore = false);

public sealed record McpSearchHit(
    string Type, string Reference, string Title, string Snippet,
    double Score, DateTime? TimestampUtc, long? ProjectId, long? MeetingId,
    /// <summary>Позиция первого совпадения в тексте источника (символы), null — имя/метаданные.</summary>
    int? CharOffset = null,
    /// <summary>Для встреч: секунды внутри реплики-совпадения (reference#t=…).</summary>
    double? TimeSec = null,
    /// <summary>Сколько раз запрос встретился в источнике (≥1 при текстовом совпадении).</summary>
    int? Occurrences = null);

public sealed record McpSourceContent(
    string Reference, string Title, string? TextContent, byte[]? BlobRef,
    string? MimeType, long? ProjectId, long? MeetingId,
    /// <summary>Позиция начала выданного фрагмента (символы) в полном тексте.</summary>
    long Offset = 0,
    /// <summary>Длина полного текста источника в символах; null — старый вызов без пагинации.</summary>
    int? TotalChars = null,
    bool HasMore = false,
    /// <summary>Курсор продолжения для ReadSourcePageAsync.</summary>
    string? NextCursor = null,
    /// <summary>Источник отдан не весь и продолжение получить нельзя — причина в TruncatedReason.</summary>
    bool Truncated = false,
    /// <summary>"no-extracted-text"|"binary"|"max-lines" — почему Truncated.</summary>
    string? TruncatedReason = null);

/// <summary>Расширенный запрос buddy_search: фильтры периода, режим обхода и курсор.</summary>
public sealed record McpSearchRequest(
    string Query,
    McpSearchTypes Types = McpSearchTypes.Meetings | McpSearchTypes.Memory,
    IReadOnlyList<long>? ProjectIds = null,
    int Limit = 50,
    /// <summary>"relevant" — топ-N по рангу (умолчание); "all" — обход всех
    /// совпадений с курсорной продолжаемостью.</summary>
    string? Mode = null,
    /// <summary>Фильтры по времени сущности (встреча — StartedUtc, память — UpdatedAtUtc, документ — CreatedUtc).</summary>
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    /// <summary>Курсор продолжения из McpSearchResult.NextCursor (режим all).</summary>
    string? Cursor = null);

/// <summary>Элемент ответа buddy_list_documents — вложение встречи со статусом извлечения.</summary>
public sealed record McpMeetingDocumentItem(
    /// <summary>"attachment:{documentId}".</summary>
    string Reference,
    string DocumentId,
    string DisplayName,
    /// <summary>Расширение без точки.</summary>
    string Extension,
    string Kind,
    long SizeBytes,
    int? PageCount,
    /// <summary>Символов извлечённого текста; null — извлечение не запускалось.</summary>
    int? CharCount,
    /// <summary>"ok"|"not-extracted"|"pending"|"deleted".</summary>
    string ExtractionStatus,
    long? ProjectId,
    long? MeetingId,
    DateTime AddedUtc);

/// <summary>Ответ buddy_add_fact.</summary>
public sealed record McpFactAddResult(
    /// <summary>id mem_item (строкой — JS-safe).</summary>
    string Id,
    /// <summary>true — такой факт уже был, записи не дублируются.</summary>
    bool Duplicate,
    /// <summary>Фактическая область памяти: "global"|"project"|"meeting".</summary>
    string Scope,
    /// <summary>reference сохранённого элемента ("memory:{id}").</summary>
    string Reference);

/// <summary>Страница сохранённого результата инструмента (buddy_read_tool_result).</summary>
public sealed record McpToolResultPage(
    string Id,
    string Text,
    long Offset,
    int TotalChars,
    bool HasMore,
    string? NextCursor,
    /// <summary>Вызывавший инструмент (server.tool), если известен.</summary>
    string? ToolName,
    /// <summary>Исходный сервер сам ограничил ответ ещё до того, как его увидел loop.</summary>
    bool ServerTruncated);
