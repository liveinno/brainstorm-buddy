namespace BrainstormBuddy.Ai;

public record AskResult(string? Content, int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static AskResult Empty => new(null, 0, 0, 0);

    /// <summary>Текст ошибки для показа юзеру (код + сообщение), когда запрос упал
    /// (401/402/404/429/сеть). Отличает реальную ошибку от «модель вернула пусто».</summary>
    public string? Error { get; init; }
    public static AskResult Failed(string error) => new(null, 0, 0, 0) { Error = error };

    /// <summary>Модель осознанно промолчала: пустой content при finish_reason="stop"
    /// (промпт суфлёра сам требует тишины на small talk/шуме). Это НЕ ошибка и НЕ
    /// обрезка лимитом — показывать нейтрально, без красной плашки.</summary>
    public bool IsSilent { get; init; }

    /// <summary>Запрос отменён токеном (пользователь отменил / закрытие приложения /
    /// cancel-токен memory-воркера). Отмена — НЕ ошибка и НЕ молчание: memory/UI по этому
    /// флагу не показывают красную плашку и не считают попытку провалом.</summary>
    public bool Canceled { get; init; }
    public static AskResult AsCanceled => new(null, 0, 0, 0) { Canceled = true };

    /// <summary>Ответ обрезан лимитом токенов (finish_reason="length"). Вызывающий
    /// (MeetingSummarizer) по этому флагу досылает «продолжи с места обрыва» —
    /// визуальный суффикс «…» в Content при этом остаётся как раньше.</summary>
    public bool WasTruncated { get; init; }
    public static AskResult Silent => new(null, 0, 0, 0) { IsSilent = true };
}

/// <summary>Подсистема, о здоровье которой сообщает клиент.</summary>
public enum ApiComponent { Stt, Llm }

/// <summary>Структурная причина недоступности STT: UI локализует деталь по ней
/// (RU-литералы в Message остаются для логов). None — не STT-ошибка/здоров.</summary>
public enum SttHealthReason { None, ServerError, Timeout, Unreachable }

/// <summary>
/// Событие смены доступности STT/LLM. Клиент шлёт его ТОЛЬКО при переходе
/// (доступен ↔ недоступен), чтобы UI не спамился на каждый чанк.
/// </summary>
public record ApiHealthEventArgs(ApiComponent Component, bool Healthy, string Message, int? HttpStatus = null, SttHealthReason Reason = SttHealthReason.None);
/// <summary>Один шаг пошаговой проверки LLM («Проверить подключение»): имя шага
/// (URL/Auth/Models/Chat), успех и человекочитаемая деталь/ошибка.</summary>
public sealed record CheckStepResult(string Name, bool Ok, string Detail);

public interface IApiClient
{
    Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default);
    /// <summary>onDelta != null → запрос уходит со stream:true и колбэк получает
    /// каждую дельту текста (SSE); провайдер/прокси без SSE → обычный буферный путь,
    /// колбэк не вызывается. null → поведение как раньше.
    /// background=true → фоновый вызов (memory-пайплайн и т.п.): ошибки идут в лог
    /// и в exchange-события, но НЕ дёргают HealthChanged — баннер «LLM недоступен»
    /// рисуется только по ответам юзеру.
    /// requestTimeout — дедлайн одной попытки (linked CTS); null → Api.RequestTimeoutSeconds.
    /// HttpClient.Timeout у клиента InfiniteTimeSpan: дедлайны задаются только здесь.</summary>
    Task<AskResult> AskAsync(string userText, string systemPrompt, int maxTokens, List<ChatMessage> history, CancellationToken ct = default, Action<string>? onDelta = null, bool background = false, TimeSpan? requestTimeout = null);
    Task<bool> CheckConnectionAsync(CancellationToken ct = default);

    /// <summary>Реальная проверка LLM для кнопки «Проверить подключение»: ключ/URL (GET /models)
    /// И чат-пинг выбранной моделью (POST /chat/completions). Ловит неверную/недоступную модель,
    /// политику данных free-моделей и лимиты — то, что GET /models не видит. Возвращает (ок, детали).</summary>
    Task<(bool ok, string detail)> CheckLlmConnectionAsync(CancellationToken ct = default);

    /// <summary>Та же проверка, но с БОЕВЫМИ параметрами (тот же max_tokens и системный промпт,
    /// что у реальных запросов) + контроль непустого content: reasoning-модель может отвечать
    /// на пинг (HTTP 200) и возвращать пустой ответ в бою — проверка обязана это ловить.</summary>
    Task<(bool ok, string detail)> CheckLlmConnectionAsync(int realMaxTokens, string? systemPrompt, CancellationToken ct = default);

    /// <summary>Пошаговая проверка LLM для отчёта в настройках: URL → Auth → Models → Chat.
    /// Models может отсутствовать у провайдера — шаг будет Ok=false, но это warn, не стоп.</summary>
    Task<List<CheckStepResult>> CheckLlmConnectionDetailedAsync(CancellationToken ct = default);

    /// <summary>GET {ModelsPath} провайдера → ID моделей для комбобокса; при ошибке — пусто.</summary>
    Task<List<string>> GetModelsAsync(CancellationToken ct = default);

    /// <summary>Срабатывает при смене доступности STT или LLM (для уведомлений юзеру).</summary>
    event EventHandler<ApiHealthEventArgs>? HealthChanged;
}
