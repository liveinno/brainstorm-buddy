using System.Text.Json.Serialization;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>Уровень доверия подключения.</summary>
public enum McpTrustLevel
{
    /// <summary>Подключение самого владельца (по умолчанию).</summary>
    Own,
    /// <summary>Доверенное удалённое устройство владельца.</summary>
    TrustedRemote,
    /// <summary>Стороннее подключение — самые строгие проверки.</summary>
    ThirdParty,
}

/// <summary>Область данных, доступная подключению.</summary>
public enum McpScopeKind
{
    /// <summary>Все данные профиля (проекты, встречи, память).</summary>
    All,
    /// <summary>Все проекты (существующие и новые).</summary>
    AllProjects,
    /// <summary>Только существующие на момент выдачи проекты.</summary>
    ExistingProjects,
    /// <summary>Выбранные проекты.</summary>
    SelectedProjects,
    /// <summary>Выбранные встречи.</summary>
    SelectedMeetings,
}

/// <summary>Права на данные.</summary>
public enum McpRight
{
    /// <summary>Чтение.</summary>
    Read,
    /// <summary>Добавление фактов в память / заметок.</summary>
    AddFact,
    /// <summary>Изменение существующих данных.</summary>
    Change,
    /// <summary>Удаление данных.</summary>
    Delete,
}

/// <summary>Состояние фонового задания MCP (входящего или исходящего).</summary>
public enum McpJobState
{
    Queued,
    Running,
    /// <summary>Ждёт пользовательского подтверждения опасного действия.</summary>
    WaitingApproval,
    /// <summary>Ждёт решения по бюджету.</summary>
    WaitingBudget,
    /// <summary>Ждёт восстановления связи (сеть/туннель).</summary>
    WaitingConnection,
    /// <summary>Остановлено паузой LLM.</summary>
    Paused,
    Succeeded,
    Failed,
    Cancelled,
    /// <summary>Нужно внимание пользователя (неожиданное состояние).</summary>
    NeedsAttention,
    /// <summary>Прерван перезапуском процесса; ждёт «Продолжить» (checkpoint
    /// в поле Checkpoint). В КОНЦЕ enum: числовые значения прежних членов
    /// в jobs.jsonl не смещаются.</summary>
    WaitingResume,
}

/// <summary>Класс задачи/инструмента по риску.</summary>
public enum McpActionRisk
{
    /// <summary>Чтение — без подтверждения.</summary>
    ReadOnly,
    /// <summary>Добавление данных — без подтверждения если в scope.</summary>
    Additive,
    /// <summary>Изменение/вызов инструмента — по доверию/разрешениям.</summary>
    Mutating,
    /// <summary>Деструктивное — всегда подтверждение.</summary>
    Destructive,
}

/// <summary>Ответ buddy_update_fact: изменение текста факта по его id.</summary>
public sealed record McpFactUpdateResult(
    /// <summary>id mem_item (строкой — JS-safe).</summary>
    string Id,
    /// <summary>true — текст факта изменён.</summary>
    bool Updated,
    /// <summary>true — факта нет либо он уже неактивен (удалён/заменён).</summary>
    bool NotFound,
    /// <summary>Фактическая область памяти: "global"|"project"|"meeting".</summary>
    string Scope,
    /// <summary>reference факта ("memory:{id}").</summary>
    string Reference,
    /// <summary>Человекочитаемое сообщение для агента.</summary>
    string Message);

/// <summary>Ответ buddy_delete_fact: мягкое удаление факта (статус → недействителен).</summary>
public sealed record McpFactDeleteResult(
    /// <summary>id mem_item (строкой — JS-safe).</summary>
    string Id,
    /// <summary>true — факт помечен удалённым.</summary>
    bool Deleted,
    /// <summary>true — факта нет либо он уже был удалён.</summary>
    bool NotFound,
    /// <summary>Фактическая область памяти: "global"|"project"|"meeting".</summary>
    string Scope,
    /// <summary>reference факта ("memory:{id}").</summary>
    string Reference,
    /// <summary>Человекочитаемое сообщение для агента.</summary>
    string Message);

/// <summary>
/// Запрос подтверждения опасной операции (удаление факта) — показывается
/// владельцу карточкой в оверлее. Делегат подтверждения живёт в фасаде
/// (SetApprovalHandler): без него удаление отклоняется, а не выполняется.
/// </summary>
public sealed record McpApprovalRequest(
    /// <summary>Короткое описание действия («Удалить факт #42»).</summary>
    string Summary,
    /// <summary>Детали действия (текст факта, обрезанный) — показываются в карточке.</summary>
    string Details,
    /// <summary>Тип операции: "delete-fact".</summary>
    string Operation = "delete-fact");
