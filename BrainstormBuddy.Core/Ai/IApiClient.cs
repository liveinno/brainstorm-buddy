namespace BrainstormBuddy.Ai;

public record AskResult(string? Content, int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static AskResult Empty => new(null, 0, 0, 0);

    /// <summary>Текст ошибки для показа юзеру (код + сообщение), когда запрос упал
    /// (401/402/404/429/сеть). Отличает реальную ошибку от «модель вернула пусто».</summary>
    public string? Error { get; init; }
    public static AskResult Failed(string error) => new(null, 0, 0, 0) { Error = error };
}

/// <summary>Подсистема, о здоровье которой сообщает клиент.</summary>
public enum ApiComponent { Stt, Llm }

/// <summary>
/// Событие смены доступности STT/LLM. Клиент шлёт его ТОЛЬКО при переходе
/// (доступен ↔ недоступен), чтобы UI не спамился на каждый чанк.
/// </summary>
public record ApiHealthEventArgs(ApiComponent Component, bool Healthy, string Message);

public interface IApiClient
{
    Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default);
    Task<AskResult> AskAsync(string userText, string systemPrompt, int maxTokens, List<ChatMessage> history, CancellationToken ct = default);
    Task<bool> CheckConnectionAsync(CancellationToken ct = default);

    /// <summary>Реальная проверка LLM для кнопки «Проверить подключение»: ключ/URL (GET /models)
    /// И чат-пинг выбранной моделью (POST /chat/completions). Ловит неверную/недоступную модель,
    /// политику данных free-моделей и лимиты — то, что GET /models не видит. Возвращает (ок, детали).</summary>
    Task<(bool ok, string detail)> CheckLlmConnectionAsync(CancellationToken ct = default);

    /// <summary>Та же проверка, но с БОЕВЫМИ параметрами (тот же max_tokens и системный промпт,
    /// что у реальных запросов) + контроль непустого content: reasoning-модель может отвечать
    /// на пинг (HTTP 200) и возвращать пустой ответ в бою — проверка обязана это ловить.</summary>
    Task<(bool ok, string detail)> CheckLlmConnectionAsync(int realMaxTokens, string? systemPrompt, CancellationToken ct = default);

    /// <summary>Срабатывает при смене доступности STT или LLM (для уведомлений юзеру).</summary>
    event EventHandler<ApiHealthEventArgs>? HealthChanged;
}
