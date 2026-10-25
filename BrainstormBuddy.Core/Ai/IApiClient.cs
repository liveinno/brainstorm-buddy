namespace BrainstormBuddy.Ai;

/// <summary>Причина пустого ответа модели (Content=null) при HTTP 200.
/// Разводит «модель осознанно промолчала» и «ответа нет по технической причине» —
/// от неё зависит, что покажет UI (нейтральное молчание / «нет ответа, повторить» /
/// «бюджет исчерпан» / «внутренний лимит провайдера»).</summary>
public enum EmptyAnswerKind
{
    /// <summary>Ответ непустой или запрос завершился иначе (ошибка/отмена).</summary>
    None,
    /// <summary>Осознанное молчание: фоновая речь (background=true) с finish_reason="stop"
    /// и пустым content, словесный маркер молчания либо reasoning, отклонённый эвристикой
    /// «финальный ответ». Это НЕ ошибка и НЕ обрезка лимитом.</summary>
    ExpectedSilence,
    /// <summary>Прямой запрос (background=false): finish_reason="stop" при пустом content
    /// и после одного повторного запроса. Правило «молчи на small talk» на прямые
    /// сообщения не распространяется — показывать «ответа не получено», а не совет
    /// про лимит токенов.</summary>
    StopEmpty,
    /// <summary>finish_reason="length"/"max_tokens" при пустом content и usage,
    /// соответствующем запрошенному лимиту: бюджет реально съеден (reasoning-модель
    /// потратила его на размышления).</summary>
    LengthExhausted,
    /// <summary>Пустой content без достоверного finish_reason: битое тело 200-го ответа,
    /// нестандартный finish_reason или потерянные данные при SSE — подозрение на
    /// транспорт/парсинг, а не на решение модели.</summary>
    TransportOrParse,
    /// <summary>Модель закончила по внутреннему лимиту провайдера: finish_reason=
    /// "length"/"max_tokens", но usage.completion существенно ниже запрошенного
    /// max_tokens — сервер обрезал генерацию раньше нашего бюджета.</summary>
    ProviderCap
}

public record AskResult(string? Content, int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static AskResult Empty => new(null, 0, 0, 0);

    /// <summary>Текст ошибки для показа юзеру (код + сообщение), когда запрос упал
    /// (401/402/404/429/сеть). Отличает реальную ошибку от «модель вернула пусто».</summary>
    public string? Error { get; init; }
    public static AskResult Failed(string error) => new(null, 0, 0, 0) { Error = error };

    /// <summary>Модель осознанно промолчала (EmptyKind == ExpectedSilence): пустой
    /// content, который промпт суфлёра допускает. Это НЕ ошибка и НЕ обрезка лимитом —
    /// показывать нейтрально, без красной плашки.</summary>
    public bool IsSilent => EmptyKind == EmptyAnswerKind.ExpectedSilence;

    /// <summary>Структурная причина пустого ответа (Content=null при HTTP 200):
    /// None — ответ есть/ошибка/отмена; ExpectedSilence — допустимое молчание фона;
    /// StopEmpty — прямой запрос промолчал и после ретрая; LengthExhausted — бюджет
    /// съеден по usage; TransportOrParse — подозрение на транспорт/парсинг;
    /// ProviderCap — провайдер обрезал вывод раньше запрошенного max_tokens.</summary>
    public EmptyAnswerKind EmptyKind { get; init; }

    /// <summary>Сырой finish_reason провайдера (null — не пришёл/не распарсился).
    /// Только для диагностики на стороне вызывающего.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Запрос отменён токеном (пользователь отменил / закрытие приложения /
    /// cancel-токен memory-воркера). Отмена — НЕ ошибка и НЕ молчание: memory/UI по этому
    /// флагу не показывают красную плашку и не считают попытку провалом.</summary>
    public bool Canceled { get; init; }
    public static AskResult AsCanceled => new(null, 0, 0, 0) { Canceled = true };

    /// <summary>Ответ обрезан лимитом токенов (finish_reason="length"). Вызывающий
    /// (MeetingSummarizer) по этому флагу досылает «продолжи с места обрыва» —
    /// визуальный суффикс «…» в Content при этом остаётся как раньше.</summary>
    public bool WasTruncated { get; init; }

    /// <summary>Провайдер прислал блок <c>usage</c> (поля prompt/completion/total).
    /// false = usage в ответе ОТСУТСТВОВАЛ — нули в Prompt/CompletionTokens значат
    /// «не сообщён», а не «ноль потрачено». Учёт токенов по такому запросу обязан
    /// показать «расход недоступен» (NA), а не ложный 0. Дефолт true: старые
    /// фейки/оценки не должны выглядеть как «usage потерян».</summary>
    public bool UsageReported { get; init; } = true;
    public static AskResult Silent => new(null, 0, 0, 0) { EmptyKind = EmptyAnswerKind.ExpectedSilence };
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
    /// HttpClient.Timeout у клиента InfiniteTimeSpan: дедлайны задаются только здесь.
    /// images — готовые к отправке картинки текущего user-сообщения (ТЗ image-chat §7):
    /// content уходит массивом частей text+image_url. Поддерживает только протокол
    /// openai-chat; прочие адаптеры отвечают Failed с префиксом IMG_ADAPTER_UNSUPPORTED.
    /// imageResolver — достаёт payload по ChatImageRef для сообщений истории
    /// (у которых Images != null); null → история идёт как раньше, строковым content.</summary>
    Task<AskResult> AskAsync(string userText, string systemPrompt, int maxTokens, List<ChatMessage> history, CancellationToken ct = default, Action<string>? onDelta = null, bool background = false, TimeSpan? requestTimeout = null, string? attachmentsContext = null, IReadOnlyList<Core.Images.ChatImagePayload>? images = null, Func<Core.Images.ChatImageRef, Core.Images.ChatImagePayload?>? imageResolver = null);
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
