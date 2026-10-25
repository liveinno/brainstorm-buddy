using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Images;
using BrainstormBuddy.Core.Mcp.Outgoing;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Ai;


public class OpenAiClient : IApiClient, IMcpToolLlm
{
    private readonly ApiConfig _config;
    private readonly HttpClient _http;
    private readonly LoggingService _logger;
    private IImageCapabilityService? _imageCapabilities;
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
    // Обновление access_token подписки ChatGPT (по refresh_token из конфига).
    private readonly Func<CodexCredentials, CancellationToken, Task<CodexCredentials?>> _codexRefresh;
    // Стабильный per-client ид для "{session}"-плейсхолдера в ExtraHeaders провайдера
    // (например x-opencode-session у OpenCode Go): генерится один раз и кешируется.
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private DateTime _lastAskUtc = DateTime.MinValue;
    // Фолбэк со стрима на буферный ответ до конца сессии: поднимается, когда SSE-
    // ответ показал, что шлюз игнорит max_tokens (completion заметно больше капа)
    // или теряет finish_reason у пустого ответа (2.7.1: латентность ×3 на таких
    // шлюзах). После установки stream:true в запрос не добавляется вообще.
    private volatile bool _streamDisabled;

    // Последнее сообщённое состояние здоровья (null = ещё не знаем).
    // Событие шлём только при переходе, чтобы не спамить UI на каждый чанк.
    private bool? _sttHealthy;
    private bool? _llmHealthy;
    private string _sttMsg = "";
    private string _llmMsg = "";

    public event EventHandler<ApiHealthEventArgs>? HealthChanged;

    private void ReportHealth(ApiComponent component, bool healthy, string message, int? httpStatus = null, SttHealthReason reason = SttHealthReason.None)
    {
        ref bool? last = ref (component == ApiComponent.Stt ? ref _sttHealthy : ref _llmHealthy);
        ref string lastMsg = ref (component == ApiComponent.Stt ? ref _sttMsg : ref _llmMsg);
        // Шлём событие при смене статуса ИЛИ текста ошибки (иначе новая 401/429 не покажется).
        if (last == healthy && lastMsg == message) return;
        last = healthy;
        lastMsg = message;
        _logger.Info($"Health[{component}] → {(healthy ? "OK" : "DOWN")}: {message}", "Ai");
        HealthChanged?.Invoke(this, new ApiHealthEventArgs(component, healthy, message, httpStatus, reason));
    }

    // LLM-здоровье только для фоновых либо пользовательских запросов: background-
    // вызовы (memory-пайплайн, суммаризер) не должны поднимать баннер «нет соединения»
    // и менять статус-точку — их падения видны только в логе.
    private void ReportLlmHealth(bool background, bool healthy, string message, int? httpStatus = null)
    {
        if (!background) ReportHealth(ApiComponent.Llm, healthy, message, httpStatus);
    }

    // Актуализирует Authorization/extra-заголовки LLM-профиля перед каждым LLM-запросом:
    // ключ/провайдер могли измениться в настройках уже после создания клиента.
    // «{session}» и «{model}» в ExtraHeaders подставляет сам LlmRequestHeaders.Apply.
    private void ApplyHeaders()
        => LlmRequestHeaders.Apply(_http, _config, _sessionId);

    // STT-запросы идут к отдельному сервису — только Bearer, без LLM-профильных заголовков.
    private void ApplyAuth()
        => _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(_config.ApiKey) ? null : new AuthenticationHeaderValue("Bearer", _config.ApiKey);

    /// <summary>
    /// Обновляет истёкший access_token подписки ChatGPT (Codex) по сохранённому
    /// refresh_token. Токен живёт ~10 дней; без этого шага провайдер отдаёт 401 и
    /// пользователю пришлось бы заново прогонять «обнаружение провайдеров».
    /// Файл <c>~/.codex/auth.json</c> не трогаем — новые токены остаются в конфиге
    /// Buddy (приложение сохраняет конфиг при выходе). Для остальных профилей шаг
    /// ничего не делает.
    /// </summary>
    private async Task EnsureCodexTokenAsync(CancellationToken ct)
    {
        if (!string.Equals(Profile.Id, CodexProfileId, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(_config.CodexRefreshToken)) return;

        var expires = CodexAuth.ExpiresAt(_config.ApiKey);
        if (expires == null || expires > DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)) return;

        var refreshToken = _config.CodexRefreshToken;
        // Недавняя неудача по этому же refresh_token — не бьём эндпоинт снова.
        if (IsCodexRefreshPaused(refreshToken)) return;

        await _codexRefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Пока ждали гейт, токен мог обновить другой поток — второй раз
            // обновлять нельзя (иначе параллельные запросы размножат refresh).
            var currentExpiry = CodexAuth.ExpiresAt(_config.ApiKey);
            if (currentExpiry == null || currentExpiry > DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)) return;
            if (IsCodexRefreshPaused(refreshToken)) return;

            // Тот же refresh_token мог обменять другой клиент процесса (например,
            // отдельный клиент памяти с копией конфига). Refresh_token ротируется,
            // второй обмен уже использованного токена недопустим — берём готовые
            // креды из общего кэша обмена.
            if (TryAdoptCodexRefresh(refreshToken))
            {
                ApplyAuth();
                return;
            }

            var current = new CodexCredentials(
                _config.ApiKey,
                refreshToken,
                CodexAuth.AccountId(_config.ApiKey) ?? "",
                CodexAuth.PlanType(_config.ApiKey) ?? "",
                CodexAuth.Email(_config.ApiKey) ?? "",
                currentExpiry,
                "");
            var refreshed = await _codexRefresh(current, ct).ConfigureAwait(false);
            if (refreshed == null)
            {
                MarkCodexRefreshFailed(refreshToken);
                _logger.Warn("Codex: обновить access_token по refresh_token не удалось — запрос уйдёт со старым токеном", "Ai");
                return;
            }

            _config.ApiKey = refreshed.AccessToken;
            if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
                _config.CodexRefreshToken = refreshed.RefreshToken;
            RememberCodexRefresh(refreshToken, refreshed);
            ClearCodexRefreshFailure();
            ApplyAuth();
            _logger.Info("Codex: access_token обновлён по refresh_token", "Ai");
        }
        finally
        {
            _codexRefreshGate.Release();
        }
    }

    // Гейт обновления токена: обмен refresh_token идёт один раз на процесс —
    // параллельные запросы не должны каждый дёргать OAuth-эндпоинт.
    private static readonly SemaphoreSlim _codexRefreshGate = new(1, 1);

    // Результаты успешных обменов refresh_token в этом процессе: ключ — сам
    // использованный refresh_token, значение — выданные креды. Нужен, чтобы
    // второй клиент (отдельный клиент памяти с копией конфига) не обменивал
    // уже ротированный токен повторно: такой обмен отклоняется (invalid_grant)
    // и может обесценить всю цепочку токенов.
    private static readonly Dictionary<string, CodexCredentials> _codexRefreshedByToken = new(StringComparer.Ordinal);
    private static readonly object _codexRefreshedLock = new();

    /// <summary>Запомнить успешный обмен: результат увидит любой клиент процесса.</summary>
    private static void RememberCodexRefresh(string usedRefreshToken, CodexCredentials refreshed)
    {
        if (string.IsNullOrWhiteSpace(usedRefreshToken)) return;
        lock (_codexRefreshedLock)
        {
            // Ограничиваем кэш: в процессе ротируется единицы токенов.
            if (_codexRefreshedByToken.Count >= 8)
                _codexRefreshedByToken.Clear();
            _codexRefreshedByToken[usedRefreshToken] = refreshed;
        }
    }

    /// <summary>
    /// Взять креды из общего кэша обмена, если этот refresh_token уже обменивали
    /// в текущем процессе. Возвращает false, если обмена не было или выданный
    /// access_token уже не годен.
    /// </summary>
    private bool TryAdoptCodexRefresh(string usedRefreshToken)
    {
        CodexCredentials? cached;
        lock (_codexRefreshedLock)
        {
            if (!_codexRefreshedByToken.TryGetValue(usedRefreshToken, out cached)) return false;
        }

        var expiry = CodexAuth.ExpiresAt(cached.AccessToken);
        if (expiry == null || expiry <= DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)) return false;

        _config.ApiKey = cached.AccessToken;
        if (!string.IsNullOrWhiteSpace(cached.RefreshToken))
            _config.CodexRefreshToken = cached.RefreshToken;
        _logger.Info("Codex: access_token взят из обмена другого клиента процесса", "Ai");
        return true;
    }

    // Пауза после неудачного обновления: тот же refresh_token не пробуем
    // раньше, чем через минуту (иначе каждый запрос долбит эндпоинт).
    // Состояние общее для процесса: неудачный обмен одним клиентом не должен
    // приводить к повторным попыткам от других клиентов с тем же токеном.
    private static readonly object _codexFailureLock = new();
    private static string _codexFailedRefreshToken = "";
    private static DateTimeOffset _codexFailedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan CodexRefreshRetryPause = TimeSpan.FromMinutes(1);

    /// <summary>Обновление по этому refresh_token недавно провалилось — ждём паузу.</summary>
    private static bool IsCodexRefreshPaused(string refreshToken)
    {
        lock (_codexFailureLock)
        {
            return _codexFailedRefreshToken.Length > 0
                && string.Equals(_codexFailedRefreshToken, refreshToken, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow - _codexFailedAt < CodexRefreshRetryPause;
        }
    }

    private static void MarkCodexRefreshFailed(string refreshToken)
    {
        lock (_codexFailureLock)
        {
            _codexFailedRefreshToken = refreshToken ?? "";
            _codexFailedAt = DateTimeOffset.UtcNow;
        }
    }

    private static void ClearCodexRefreshFailure()
    {
        lock (_codexFailureLock)
        {
            _codexFailedRefreshToken = "";
            _codexFailedAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Id профиля подписки ChatGPT (Codex) в реестре провайдеров.</summary>
    private const string CodexProfileId = "codex-chatgpt";

    // Профиль провайдера по ProviderId/CustomProviders, иначе синтетический из BaseUrl.
    private LlmProviderProfile Profile => LlmProviderRegistry.Resolve(_config);

    // Ключ кэша «модель умеет картинки» для текущих настроек (тот же контракт,
    // что ImageCapabilityService.KeyFor — тут вызываем напрямую).
    private ImageCapabilityKey CurrentImageKey() => ImageCapabilityService.KeyFor(_config);

    /// <summary>Позднее подключение кэпабилити-сервиса (App создаёт его после
    /// AttachmentStore — позже конструктора клиента).</summary>
    public void SetImageCapabilityService(IImageCapabilityService? svc)
        => _imageCapabilities = svc;

    // URL эндпойнта: BaseUrl конфига (без хвостового слеша) + путь из профиля.
    private string EndpointUrl(string path)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_config.BaseUrl) ? Profile.BaseUrl : _config.BaseUrl;
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    // Нормализация ID модели (A7): trim + zero-width/BOM/NBSP — иначе копипаст из UI
    // тащит невидимые символы и провайдер отвечает «model not supported».
    private static string NormalizeModelId(string model)
        => string.Concat((model ?? "").Trim().Where(c =>
            c != '​' && c != '‎' && c != '‏' && c != (char)0xFEFF && c != ' '));

    // Короткий человекочитаемый текст ошибки API для плашки: «401 Unauthorized: <сообщение>».
    private static string ShortApiError(HttpStatusCode code, string body)
    {
        string msg = "";
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) msg = m.GetString() ?? "";
                else if (e.ValueKind == JsonValueKind.String) msg = e.GetString() ?? "";
            }
            else if (d.RootElement.TryGetProperty("message", out var m2)) msg = m2.GetString() ?? "";
        }
        catch { /* тело не JSON — покажем как есть */ }
        if (string.IsNullOrWhiteSpace(msg)) msg = body;
        return $"{(int)code} {code}: {Truncate(msg.Replace("\n", " ").Trim(), 140)}";
    }

    // Сырой текст провайдера («Payment required», «Rate limit exceeded, try again in 17424
    // seconds») юзеру ни о чём — живой тест: владелец принял лимиты free-тарифа OpenRouter
    // за баг приложения. Первой строкой — человеческое объяснение и что делать,
    // технический текст оставляем второй строкой (для поддержки/логов).
    private static string HumanizeApiError(HttpStatusCode code, string apiErr) => (int)code switch
    {
        // 400 MissingSessionID — шлюзы вроде opencode.ai требуют session-заголовок для POST.
        400 when apiErr.Contains("MissingSessionID", StringComparison.OrdinalIgnoreCase)
            => "Шлюз требует заголовок сессии (MissingSessionID): включите провайдер с x-opencode-session или добавьте его в ExtraHeaders.\n" + apiErr,
        401 => "Неверный ключ, модель не поддерживается у провайдера или лишний префикс в ID модели (напр. opencode-zen/…) — сверьте ключ и ID модели.\n" + apiErr,
        402 => "Исчерпан лимит/баланс у провайдера — пополните счёт или переключитесь на другую модель (Настройки → API).\n" + apiErr,
        403 => "Нет доступа к модели: региональное ограничение (region-gate) или требуется opt-in на сайте провайдера.\n" + apiErr,
        404 => "Эндпойнт или модель не найдены: проверьте BaseUrl (только база вида https://host/v1) и ID модели.\n" + apiErr,
        429 => "Провайдер ограничил частоту запросов (rate-limit) — подождите или смените модель/тариф.\n" + apiErr,
        _ => apiErr
    };

    // Сессионный LLM-лог (вкладка «Диагностика»): клиент сообщает о каждом запросе («→LLM»)
    // и ответе/ошибке («←LLM»), а App собирает строки. Событие летит с фоновых потоков —
    // подписчик обязан сам уходить в Dispatcher.
    public event EventHandler<LlmExchangeEventArgs>? Exchange;

    private void RaiseExchange(bool isRequest, string text, double elapsedSeconds = 0, int totalTokens = 0, bool isError = false)
    {
        Exchange?.Invoke(this, new LlmExchangeEventArgs
        {
            Timestamp = DateTime.Now,
            IsRequest = isRequest,
            Model = _config.ChatModel,
            Text = Truncate(text.Replace("\n", " ").Trim(), 200),
            ElapsedSeconds = elapsedSeconds,
            TotalTokens = totalTokens,
            IsError = isError
        });
    }

    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiClient(ApiConfig config, LoggingService logger, HttpMessageHandler? handler = null,
        IImageCapabilityService? imageCapabilities = null,
        Func<CodexCredentials, CancellationToken, Task<CodexCredentials?>>? codexTokenRefresher = null)
    {
        _config = config;
        _logger = logger;
        _imageCapabilities = imageCapabilities;
        // Обновление токена подписки ChatGPT — по умолчанию бьёмся в OAuth-эндпоинт;
        // тесты подставляют свою функцию (сеть в юнит-тестах не используется).
        _codexRefresh = codexTokenRefresher
            ?? ((creds, token) => CodexAuth.RefreshAsync(creds, null, token));
        // HttpClient.Timeout = Infinite: дедлайны задаются per-call linked CTS
        // (Transcribe — 300с, Ask — requestTimeout/RequestTimeoutSeconds за попытку,
        // checks — RequestTimeoutSeconds). Timeout у клиента нельзя трогать после
        // первого запроса, а единый лимит резал длинные вызовы раньше их CTS.
        _http = handler == null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        ApplyAuth(); // стартовая авторизация; LLM-заголовки надеваются перед каждым запросом
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            _logger.Debug($"OpenAiClient: BaseUrl={config.BaseUrl}, Chat={config.ChatModel}, Stt={config.SttModel}, Key=***{(config.ApiKey.Length > 4 ? config.ApiKey[^4..] : "<short>")}", "Ai");
        }
        else if (Profile.AuthKind != "none")
        {
            _logger.Warn("OpenAiClient: ApiKey is empty!", "Ai");
        }
    }

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default)
    {
        if (wavAudio == null || wavAudio.Length == 0) return null;
        ApplyAuth();

        var baseUrl = string.IsNullOrEmpty(_config.SttBaseUrl) ? _config.BaseUrl : _config.SttBaseUrl;
        var url = $"{baseUrl.TrimEnd('/')}/audio/transcriptions";
        _logger.Debug($"STT → POST {url} ({wavAudio.Length} bytes, model={_config.SttModel})", "Ai");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(300));
        var token = timeoutCts.Token;

        for (int attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            try
            {
                using var content = new MultipartFormDataContent();
                var audioContent = new ByteArrayContent(wavAudio);
                audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(audioContent, "file", "audio.wav");
                content.Add(new StringContent(_config.SttModel), "model");
                content.Add(new StringContent("json"), "response_format");
                if (!string.IsNullOrWhiteSpace(_config.SttLanguage))
                    content.Add(new StringContent(_config.SttLanguage), "language");
                content.Add(new StringContent(_config.SttTemperature.ToString(System.Globalization.CultureInfo.InvariantCulture)), "temperature");
                content.Add(new StringContent(_config.SttBeamSize.ToString()), "beam_size");
                content.Add(new StringContent(_config.SttVadFilter ? "true" : "false"), "vad_filter");

                var sw = Stopwatch.StartNew();
                using var response = await _http.PostAsync(url, content, token);
                var body = await response.Content.ReadAsStringAsync(token);
                sw.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"STT ← {(int)response.StatusCode} {response.StatusCode} in {sw.ElapsedMilliseconds}ms: {Truncate(body, 200)}", "Ai");
                    if (IsRetryable(response.StatusCode) && attempt < _config.MaxRetries)
                    {
                        _logger.Info($"STT retry {attempt + 1}/{_config.MaxRetries} after backoff", "Ai");
                        await Backoff(attempt, token);
                        continue;
                    }
                    ReportHealth(ApiComponent.Stt, false, $"STT-сервер ответил ошибкой {(int)response.StatusCode}", (int)response.StatusCode, SttHealthReason.ServerError);
                    return null;
                }

                // Дошёл ответ 200 — сервер распознавания жив.
                ReportHealth(ApiComponent.Stt, true, "STT-сервер отвечает");
                _logger.Debug($"STT ← 200 in {sw.ElapsedMilliseconds}ms, body={Truncate(body, 200)}", "Ai");

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    return text.Trim();
                }
                _logger.Warn("STT response has no 'text' field", "Ai");
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.Info("STT cancelled by parent token", "Ai");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                // token живёт только от timeoutCts(300с) и ct: раз сюда дошло при
                // живом ct — общий дедлайн вызова сгорел, ретраить нечего (новый
                // PostAsync всё равно получит уже отменённый token).
                _logger.Warn($"STT timeout attempt {attempt + 1}: {ex.Message}", "Ai");
                ReportHealth(ApiComponent.Stt, false, "STT-сервер не отвечает (таймаут)", reason: SttHealthReason.Timeout);
                return null;
            }
            catch (HttpRequestException ex)
            {
                if (IsConnectivityError(ex, attempt))
                {
                    ReportHealth(ApiComponent.Stt, false, "STT-сервер недоступен (нет соединения)", reason: SttHealthReason.Unreachable);
                    return null;
                }
                _logger.Warn($"STT HTTP error attempt {attempt + 1}: {ex.Message}", "Ai");
                if (attempt < _config.MaxRetries)
                {
                    await Backoff(attempt, token);
                    continue;
                }
                ReportHealth(ApiComponent.Stt, false, "STT-сервер недоступен (нет соединения)", reason: SttHealthReason.Unreachable);
                return null;
            }
        }
        return null;
    }

    public Task<AskResult> AskAsync(string userText, string systemPrompt, int maxTokens, List<ChatMessage> history, CancellationToken ct = default, Action<string>? onDelta = null, bool background = false, TimeSpan? requestTimeout = null, string? attachmentsContext = null, IReadOnlyList<ChatImagePayload>? images = null, Func<ChatImageRef, ChatImagePayload?>? imageResolver = null)
        => AskCoreAsync(userText, systemPrompt, maxTokens, history, ct, onDelta, background, requestTimeout, attachmentsContext, allowEmptyRetry: true, images, imageResolver);

    // allowEmptyRetry — бюджет «пустых» повторов AskAsync: максимум ОДИН дополнительный
    // запрос на пустой ответ (не считая транспортных ретраев цикла MaxRetries). Повтор
    // идёт без SSE (onDelta=null) и с тем же бюджетом токенов/вложениями/историей —
    // побочные действия (запись фактов, реплики) не дублируются, т.к. ретрай внутри.
    private async Task<AskResult> AskCoreAsync(string userText, string systemPrompt, int maxTokens, List<ChatMessage> history, CancellationToken ct, Action<string>? onDelta, bool background, TimeSpan? requestTimeout, string? attachmentsContext, bool allowEmptyRetry, IReadOnlyList<ChatImagePayload>? images = null, Func<ChatImageRef, ChatImagePayload?>? imageResolver = null)
    {
        var hasImages = images is { Count: > 0 };
        // Мультимодальный content-массив умеет только протокол openai-chat —
        // остальные адаптеры честно отказывают, UI парсит код-префикс (ТЗ §7).
        if (hasImages && Profile.Protocol != "openai-chat")
            return AskResult.Failed("IMG_ADAPTER_UNSUPPORTED: выбранный адаптер API не принимает изображения");
        if (string.IsNullOrWhiteSpace(userText) && !hasImages) return AskResult.Empty;

        // Вложения чата — данные в том же user-сообщении, но в отдельном
        // разграниченном блоке (AttachmentContextBuilder). userText остаётся чистым
        // для отображения/журнала; в запрос уходит склейка.
        var requestText = string.IsNullOrEmpty(attachmentsContext)
            ? userText
            : attachmentsContext + userText;
        // Истёкший токен подписки ChatGPT обновляем ДО сборки запроса — иначе
        // провайдер ответит 401 и запрос уйдёт с заведомо мёртвым ключом.
        await EnsureCodexTokenAsync(ct).ConfigureAwait(false);
        ApplyHeaders();

        var url = EndpointUrl(Profile.ChatPath);
        _logger.Debug($"Ask → POST {url} (text={requestText.Length} chars, history={history.Count}, maxTokens={maxTokens})", "Ai");

        if (_config.RateLimitSeconds > 0)
        {
            // Отмена во время ожидания rate-limit — не ошибка: выходим AsCanceled,
            // чтобы caller не рисовал красную плашку.
            try
            {
                await _rateLimitGate.WaitAsync(ct);
                try
                {
                    var elapsed = (DateTime.UtcNow - _lastAskUtc).TotalSeconds;
                    var wait = _config.RateLimitSeconds - elapsed;
                    if (wait > 0)
                    {
                        _logger.Debug($"Ask rate-limit: sleeping {wait:F1}s (limit={_config.RateLimitSeconds}s, last={elapsed:F1}s ago)", "Ai");
                        await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    }
                }
                finally
                {
                    _rateLimitGate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.Info("Ask cancelled during rate-limit wait", "Ai");
                return AskResult.AsCanceled;
            }
        }

        // «→LLM» — один раз на вызов (авто-ретраи 402/пустого ответа идут рекурсией и честно
        // дадут свою строку — в логе видно, что запрос ушёл повторно с другим лимитом).
        RaiseExchange(isRequest: true, requestText);

        for (int attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            // Дедлайн ПОПЫТКИ отдельным CTS поверх ct вызывающего: таймаут
            // отличим от отмены (attemptCts.IsCancellationRequested), а
            // HttpClient.Timeout=Infinite сам ничего не режет.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));
            var rct = attemptCts.Token;

            try
            {
                // Протокол Responses (OpenAI Responses API) — отдельная ветка: свой формат
                // тела (instructions/input) и свой разбор ответа. Ветка chat/completions
                // ниже остаётся без изменений.
                var isResponses = Profile.Protocol == ResponsesProtocol.Protocol;
                var chatModel = NormalizeModelId(_config.ChatModel);
                var modelLower = chatModel.ToLowerInvariant();
                // Effort: глобальный override (Api.ReasoningEffortOverride) приоритетнее
                // профиля провайдера; итог пуст — поле reasoning не шлём вообще.
                var reasoningEffort = _config.ReasoningEffortOverride ?? Profile.ReasoningEffort;
                // Стриминг — безопасный upgrade: только когда вызывающий дал onDelta
                // и протокол умеет SSE (openai-chat и openai-responses). Прокси,
                // проигнорировавший stream:true и вернувший обычный JSON, уходит в
                // буферный парсинг ниже без ошибки. _streamDisabled: шлюз уже доказал,
                // что портит SSE — дальше только буфер.
                // Профили с RequiresStreaming (бэкенд подписки ChatGPT/Codex) поток
                // требуют всегда: stream=false → 400 «Stream must be set to true».
                var streaming = (onDelta != null || Profile.RequiresStreaming)
                    && (Profile.Protocol == "openai-chat" || isResponses)
                    && (Profile.RequiresStreaming || !_streamDisabled);

                // Тело запроса: у Responses-протокола — свой формат (ResponsesProtocol),
                // у чат-протокола — messages[] (ниже, как раньше).
                Dictionary<string, object>? req = null;
                string? responsesBody = null;
                if (isResponses)
                {
                    // Responses-тело: model/instructions/input/stream/store (+reasoning.effort).
                    // Sampling-параметры сюда НЕ идут: бэкенд подписки ChatGPT/Codex их
                    // отвергает («Unsupported parameter: temperature»). Лимит вывода шлём
                    // только там, где бэкенд его принимает.
                    var responsesCap = ResponsesProtocol.AcceptsOutputCaps(Profile.BaseUrl) ? maxTokens : (int?)null;
                    responsesBody = ResponsesProtocol.BuildRequest(chatModel, systemPrompt, requestText,
                        history, reasoningEffort, streaming, responsesCap);
                }
                else
                {
                    var messages = new List<object>
                    {
                        new { role = "system", content = systemPrompt }
                    };
                    foreach (var m in history)
                    {
                        // У сообщения с картинками (история после вставки) content —
                        // массив частей по ChatImageRef; резолвер null или ничего не
                        // резолвил — строковый content, запрос не ломаем (ТЗ §7).
                        object histContent = m.Images is { Count: > 0 } && imageResolver != null
                            ? ChatImageTransport.BuildMessageContent(m, imageResolver)
                            : m.Content;
                        messages.Add(new { role = m.Role, content = histContent });
                    }
                    object userContent = hasImages
                        ? ChatImageTransport.BuildUserContent(requestText, images)
                        : requestText;
                    messages.Add(new { role = "user", content = userContent });

                    // Для reasoning-моделей (gpt-oss, o1, etc.) минимизируем reasoning,
                    // чтобы получить быстрый прямой ответ. По умолчанию для не-reasoning — отключаем.
                    var isReasoning = IsReasoningModel(modelLower);
                    // Whisper-промпт: temperature 0.1 для строгости (без галлюцинаций и вопросов)
                    var isWhisperPrompt = systemPrompt.Contains("шепот", StringComparison.OrdinalIgnoreCase) ||
                                          systemPrompt.Contains("whisper", StringComparison.OrdinalIgnoreCase);
                    var temperature = isWhisperPrompt ? 0.1 : 0.3;
                    req = new Dictionary<string, object>
                    {
                        { "model", chatModel },
                        { "messages", messages },
                        { "temperature", temperature },
                        { "max_tokens", maxTokens }
                    };
                    if (isReasoning && !string.IsNullOrEmpty(reasoningEffort))
                    {
                        req["reasoning"] = new { effort = reasoningEffort };
                    }
                    if (streaming)
                    {
                        req["stream"] = true;
                        req["stream_options"] = new { include_usage = true };
                    }
                }

                var sw = Stopwatch.StartNew();
                HttpResponseMessage response;
                string? body = null;
                var isSse = false;
                // Reader тела Responses-запроса: нужен, когда бэкенд отдал SSE
                // без Content-Type (подписка ChatGPT/Codex) — поток читаем из него.
                TextReader? responsesReader = null;
                if (isResponses)
                {
                    // Responses-тело уже собрано (ResponsesProtocol) — шлём его как есть.
                    using var msg = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(responsesBody!, Encoding.UTF8, "application/json")
                    };
                    response = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, rct);
                    var ctHeader = response.Content.Headers.ContentType?.MediaType;
                    if (response.IsSuccessStatusCode &&
                        ctHeader != null && ctHeader.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        isSse = true;
                        responsesReader = await OpenReaderAsync(response, rct);
                    }
                    else
                    {
                        // Бэкенд подписки ChatGPT/Codex отдаёт SSE БЕЗ заголовка
                        // Content-Type — формат определяем по первому символу тела.
                        var (sse, reader) = await OpenResponsesBodyAsync(response, rct);
                        if (sse)
                        {
                            isSse = true;
                            responsesReader = reader;
                        }
                        else
                        {
                            body = await reader.ReadToEndAsync(rct);
                            reader.Dispose();
                        }
                    }
                }
                else if (streaming)
                {
                    using var msg = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(req, _jsonOpts), Encoding.UTF8, "application/json")
                    };
                    response = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, rct);
                    var ctHeader = response.Content.Headers.ContentType?.MediaType;
                    if (response.IsSuccessStatusCode &&
                        ctHeader != null && ctHeader.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                        isSse = true;
                    else
                        body = await response.Content.ReadAsStringAsync(rct);
                }
                else
                {
                    response = await _http.PostAsJsonAsync(url, req, _jsonOpts, rct);
                    body = await response.Content.ReadAsStringAsync(rct);
                }
                using (response)
                {
                if (!isSse) sw.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"Ask ← {(int)response.StatusCode} {response.StatusCode} in {sw.ElapsedMilliseconds}ms: {Truncate(body ?? "", 300)}", "Ai");
                    var apiErr = ShortApiError(response.StatusCode, body ?? "");
                    if (hasImages && _imageCapabilities != null)
                    {
                        // Вердикт по изображениям (ТЗ §8): только структурированные
                        // отказы API (код/тип про image) ложатся в кэш как Unsupported.
                        var (ecode, etype, emsg) = ImageCapabilityService.ParseApiError(body);
                        var verdict = ImageApiErrorClassifier.Classify((int)response.StatusCode, ecode, etype, emsg);
                        _imageCapabilities.RecordApiResult(CurrentImageKey(), verdict, emsg);
                    }
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        _logger.Error("Auth error: invalid API key (401)", null, "Ai");
                        ReportLlmHealth(background, false, apiErr, (int)response.StatusCode);
                        RaiseExchange(isRequest: false, apiErr, isError: true);
                        return AskResult.Failed(apiErr);
                    }
                    if ((int)response.StatusCode == 402 && maxTokens > 50)
                    {
                        _logger.Warn($"Payment required (402): reducing max_tokens from {maxTokens} to 50 and retrying", "Ai");
                        return await AskCoreAsync(userText, systemPrompt, 50, history, ct, onDelta, background, requestTimeout, attachmentsContext, allowEmptyRetry, images, imageResolver);
                    }
                    if ((int)response.StatusCode == 402 && maxTokens <= 50)
                    {
                        _logger.Error("Payment required (402): no credits left. Top up at openrouter.ai/settings/credits", null, "Ai");
                        // №5 бэклога: человеческое объяснение первой строкой, сырой текст — второй.
                        var friendly402 = HumanizeApiError(response.StatusCode, apiErr);
                        ReportLlmHealth(background, false, friendly402, (int)response.StatusCode);
                        RaiseExchange(isRequest: false, apiErr, isError: true);
                        return AskResult.Failed(friendly402);
                    }
                    if (body != null && body.Contains("context_length_exceeded") && history.Count > 2)
                    {
                        _logger.Warn($"Context length exceeded (history={history.Count}), trimming to 2", "Ai");
                        history.RemoveRange(0, history.Count - 2);
                        return await AskCoreAsync(userText, systemPrompt, maxTokens, history, ct, onDelta, background, requestTimeout, attachmentsContext, allowEmptyRetry, images, imageResolver);
                    }
                    if (IsRetryable(response.StatusCode) && attempt < _config.MaxRetries)
                    {
                        _logger.Info($"Ask retry {attempt + 1}/{_config.MaxRetries} after backoff", "Ai");
                        await Backoff(attempt, ct);
                        continue;
                    }
                    // Сюда доходит и 429 после исчерпанных ретраев — тоже даём человеческий текст.
                    var friendly = HumanizeApiError(response.StatusCode, apiErr);
                    ReportLlmHealth(background, false, friendly, (int)response.StatusCode);
                    RaiseExchange(isRequest: false, apiErr, isError: true);
                    return AskResult.Failed(friendly);
                }

                // Дошёл ответ 200 — LLM-сервер жив.
                ReportLlmHealth(background, true, "LLM-сервер отвечает");
                // Запрос с картинками принят API (200) → транспорт подтверждён (ТЗ §8).
                if (hasImages)
                    _imageCapabilities?.RecordApiResult(CurrentImageKey(), ImageApiVerdict.Accepted, "accepted-request");


                string? content_str = null;
                int promptTokens = 0, completionTokens = 0, totalTokens = 0;
                // Провайдер прислал блок usage (не «нули», а «не сообщил») —
                // счётчик токенов по такому запросу показывает «расход недоступен».
                var usageSeen = false;
                string? finishReason = null;
                var sawReasoningOnly = false;
                string? reasoningText = null;
                // 200, но тело не JSON/без ожидаемых полей — классифицируем пустой
                // ответ как TransportOrParse (это сбой формата, а не решение модели).
                var parseFailed = false;
                // Ошибка провайдера внутри 200-ответа Responses ({"error":{…}}) —
                // её показываем юзеру, а не прячем в «пустой ответ».
                string? responsesError = null;

                if (isSse)
                {
                    // SSE-стрим: читаем дельты построчно, onDelta — на потоке
                    // вызывающего. Финальный content = склейка дельт → дальше те же
                    // ветки (пустой/stop=молчание/length), что у буферного ответа.
                    SseResult sse;
                    try
                    {
                        // onDelta может быть null, когда поток обязателен
                        // (профиль RequiresStreaming) — тогда дельты никому не
                        // отдаём, но поток читаем целиком.
                        var sink = onDelta ?? (static _ => { });
                        sse = isResponses
                            ? await ReadResponsesSseAsync(responsesReader!, sink, rct)
                            : await ReadSseStreamAsync(response, sink, rct);
                    }
                    // Обрыв живого потока (таймаут/разрыв посреди ответа) — это НЕ
                    // «нет соединения»: connectivity-ветка ниже дала бы юзеру ложный
                    // диагноз и ReportHealth(Llm,false). Возвращаем обрыв как есть.
                    catch (Exception ex) when ((ex is TaskCanceledException or HttpRequestException) && !ct.IsCancellationRequested)
                    {
                        sw.Stop();
                        var timedOut = attemptCts.IsCancellationRequested;
                        var sseErr = timedOut ? "LLM-сервер не отвечает (таймаут)" : "Ответ LLM оборвался (поток прерван)";
                        _logger.Warn($"Ask: SSE-поток оборвался ({(timedOut ? "таймаут" : ex.GetType().Name)}: {ex.Message})", "Ai");
                        ReportLlmHealth(background, false, sseErr);
                        RaiseExchange(isRequest: false, sseErr, sw.Elapsed.TotalSeconds, 0, isError: true);
                        return AskResult.Failed(sseErr);
                    }
                    sw.Stop();
                    // Ошибка провайдера в потоке Responses (response.failed / event «error») —
                    // это НЕ «пустой ответ»: текст ошибки показываем юзеру (в лог, в плашку,
                    // в результат), иначе он увидит «молчание» вместо причины.
                    if (!string.IsNullOrEmpty(sse.Error))
                    {
                        _logger.Warn($"Ask: Responses-ответ вернул ошибку провайдера: {sse.Error}", "Ai");
                        ReportLlmHealth(background, false, sse.Error);
                        RaiseExchange(isRequest: false, sse.Error, sw.Elapsed.TotalSeconds, sse.TotalTokens, isError: true);
                        return AskResult.Failed(sse.Error);
                    }
                    if (sse.Aborted)
                    {
                        _logger.Warn("Ask: SSE-поток оборвался без data: [DONE]", "Ai");
                        ReportLlmHealth(background, false, "Ответ LLM оборвался");
                        RaiseExchange(isRequest: false, "Ответ LLM оборвался (поток прерван)", sw.Elapsed.TotalSeconds, sse.TotalTokens, isError: true);
                        return AskResult.Failed("Ответ LLM оборвался (поток прерван)");
                    }
                    _logger.Debug($"Ask ← 200 SSE in {sw.ElapsedMilliseconds}ms, deltas={sse.DeltaCount}, len={sse.Content.Length}, finish={sse.FinishReason ?? "<null>"}, completion={sse.CompletionTokens}/{maxTokens}", "Ai");
                    content_str = sse.Content;
                    promptTokens = sse.PromptTokens;
                    completionTokens = sse.CompletionTokens;
                    totalTokens = sse.TotalTokens;
                    finishReason = sse.FinishReason;
                    // gpt-oss-стрим: ответ может быть целиком в delta.reasoning при
                    // пустом content — ниже та же ветка фолбэка, что у буферного.
                    reasoningText = sse.Reasoning;
                    sawReasoningOnly = sse.Reasoning != null;

                    // Авто-фолбэк со стрима на буферный запрос (2.7.1). Два симптома
                    // глюкого шлюза: completion > maxTokens*1.25 — кап проигнорирован
                    // (в SSE opencode.ai/zen жгло 820-883 токена при лимите 180,
                    // латентность ×3); пустой content без finish_reason — молчание не
                    // распознаётся и уходит в авто-ретрай с max_tokens=1024. Стрим
                    // выключаем до конца сессии и переиздаём тот же запрос буфером —
                    // один раз: рекурсивный вызов уже идёт без stream:true (флаг
                    // поднят), на стрим-путь не вернётся.
                    // У Responses-протокола этот фолбэк НЕ применяется: лимита вывода
                    // в теле запроса нет (бэкенд его не принимает), поэтому «кап проигнорирован»
                    // там — норма, а не сбой шлюза; повтор с тем же лимитом бессмысленен.
                    // Пустой ответ у Responses разбирает общая ветка классификации ниже.
                    var capIgnored = sse.CompletionTokens > maxTokens * 1.25;
                    var finishLost = string.IsNullOrWhiteSpace(sse.Content) && sse.FinishReason == null;
                    if (!isResponses && (capIgnored || finishLost))
                    {
                        var why = capIgnored
                            ? $"provider ignored max_tokens in SSE (completion={sse.CompletionTokens}/{maxTokens})"
                            : "SSE ответ без finish_reason при пустом content";
                        _logger.Warn($"Ask: {why} — reissue без стрима", "Ai");
                        _streamDisabled = true;
                        _logger.Info($"stream disabled for session ({why})", "Ai");
                        // finishLost — это и есть повтор пустого ответа (SSE потерял
                        // finish_reason): бюджет пустых ретраев тратим здесь.
                        // R4: перевыпуск буфером — ОТДЕЛЬНЫЙ оплаченный запрос, а usage
                        // SSE-попытки уже пришёл; складываем, иначе потеряем его.
                        var reissued = await AskCoreAsync(userText, systemPrompt, maxTokens, history, ct, onDelta, background, requestTimeout, attachmentsContext, allowEmptyRetry: !finishLost && allowEmptyRetry, images, imageResolver);
                        return AccumulateAttemptUsage(promptTokens, completionTokens, totalTokens, reissued);
                    }
                }
                else
                {
                _logger.Debug($"Ask ← 200 in {sw.ElapsedMilliseconds}ms, body={Truncate(body ?? "", 300)}", "Ai");

                // Защищённый парсинг (A6): кривое 200-тело провайдера (не JSON, без choices)
                // не должно улетать JsonException'ом наружу — помечаем parseFailed и уходим
                // в общую ветку «пустой ответ», которая классифицирует это как
                // TransportOrParse и делает один ограниченный повтор.
                // Модель прислала ТОЛЬКО рассуждения (CoT) без ответа. Текст сохраняем,
                // но НЕ показываем юзеру сразу (раньше сюда проваливался CoT
                // deepseek-v4.1-flash и шёл на экран как «ответ»). Фолбэк на него —
                // только для allowlist-моделей (A6: gpt-oss держит ответ в
                // reasoning_content by design) и только через эвристику «финальный
                // ответ» (2.8.x п.2).

                try
                {
                    if (isResponses)
                    {
                        // Responses-ответ: output[].content[].text + usage + status.
                        var parsed = ResponsesProtocol.ParseResponse(body!);
                        content_str = parsed.Content;
                        promptTokens = parsed.PromptTokens;
                        completionTokens = parsed.CompletionTokens;
                        totalTokens = parsed.TotalTokens;
                        finishReason = parsed.FinishReason;
                        responsesError = parsed.Error;
                    }
                    else
                    {
                        using var doc = JsonDocument.Parse(body!);
                        var root = UnwrapDataEnvelope(doc.RootElement);
                        var msg = TryGetByPath(root, "choices[0].message");
                        content_str = TryGetByPath(root, Profile.ContentPath)?.GetString();
                        if (string.IsNullOrWhiteSpace(content_str) && msg is JsonElement m)
                        {
                            if (m.TryGetProperty("reasoning_content", out var rc) && !string.IsNullOrWhiteSpace(rc.GetString()))
                                reasoningText = rc.GetString();
                            else if (m.TryGetProperty("reasoning_details", out var rd) &&
                                     rd.ValueKind == JsonValueKind.Array && rd.GetArrayLength() > 0 &&
                                     rd[0].TryGetProperty("text", out var rdt) && !string.IsNullOrWhiteSpace(rdt.GetString()))
                                reasoningText = rdt.GetString();
                            else if (m.TryGetProperty("reasoning", out var rsn) &&
                                     rsn.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rsn.GetString()))
                                reasoningText = rsn.GetString(); // Groq (gpt-oss-120b): ответ живёт в message.reasoning
                            sawReasoningOnly = reasoningText != null;
                        }
                        if (TryGetByPath(root, Profile.UsagePath) is JsonElement u && u.ValueKind == JsonValueKind.Object)
                        {
                            if (u.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                            else if (u.TryGetProperty("input_tokens", out var it)) promptTokens = it.GetInt32();
                            if (u.TryGetProperty("completion_tokens", out var ct2)) completionTokens = ct2.GetInt32();
                            else if (u.TryGetProperty("output_tokens", out var ot)) completionTokens = ot.GetInt32();
                            if (u.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
                        }
                        finishReason = TryGetByPath(root, "choices[0].finish_reason")?.GetString();
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
                {
                    _logger.Warn($"Ask: 200, но тело не распарсилось: {ex.Message}", "Ai");
                    parseFailed = true;
                }
                }

            // Ошибка провайдера в 200-ответе Responses ({"error":{"message":"…"}}) —
            // это НЕ «пустой ответ»: текст ошибки показываем юзеру (лог, плашка,
            // результат), иначе он увидит «молчание» вместо причины.
            if (!string.IsNullOrEmpty(responsesError))
            {
                _logger.Warn($"Ask: Responses-ответ вернул ошибку провайдера: {responsesError}", "Ai");
                ReportLlmHealth(background, false, responsesError);
                RaiseExchange(isRequest: false, responsesError, sw.Elapsed.TotalSeconds, totalTokens, isError: true);
                return AskResult.Failed(responsesError);
            }

            // Usage-признак: провайдер НЕ прислал блок usage (или прислал нулями) —
            // тогда токены «не сообщены», а не «равны нулю». Считаем это
            // отсутствием usage: запрос с ответом, но без цифр, обязан дать NA
            // в счётчике, а не ложный 0 (дефект владельца «счетчик показывает 0»).
            usageSeen = promptTokens > 0 || completionTokens > 0 || totalTokens > 0;

            // Срезаем протечку DSML-разметки функ-вызовов и CoT-преамбулу ДО проверки
            // пустоты — ответ из одних маркеров/размышлений уходит в ветку «пустой ответ».
            content_str = LlmAnswerSanitizer.Sanitize(content_str, m => _logger.Debug(m, "Ai"));
            if (string.IsNullOrWhiteSpace(content_str))
            {
                var cot = sawReasoningOnly ? ", в ответе только рассуждения" : "";
                _logger.Warn($"Ask: 200, content пуст — finish={finishReason ?? "<null>"}, usage={promptTokens}+{completionTokens}/{maxTokens} (total={totalTokens}), bodyLen={body?.Length ?? 0}, parseFailed={parseFailed}, background={background}{cot}", "Ai");

                // Фолбэк «ответ живёт в reasoning» — только для ПАР
                // (провайдер, модель), где это подтверждённый контракт провайдера
                // (opencode-go deepseek-v*, groq gpt-oss*): у этих провайдеров финальный
                // ответ реально приходит в reasoning_content. Неподтверждённый/
                // custom-провайдер допуска не получает — иначе произвольная модель
                // с именем «deepseek-v-...» постфактум показывала бы CoT как ответ
                // (аудит 2026-10-16 R1). Сам reasoning всё равно проходит эвристику
                // «финальный ответ» (LooksLikeFinalAnswer ниже отклоняет сырой CoT).
                // Применяется на первом же ответе — иначе StopEmpty/TransportOrParse-ретрай
                // съест ответ, который уже лежит в reasoning (см. тест precedence).
                // finish=length: бюджет сожжён размышлениями — reasoning это
                // обрезанный CoT (аудит errors.txt: bodyLen=16k, «только
                // рассуждения»). Пропуск вниз классифицирует LengthExhausted
                // («бюджет исчерпан») вместо ложного «молчания».
                var reasoningFallback = AnswersInReasoningContent(Profile.Id, modelLower)
                    && !string.IsNullOrWhiteSpace(reasoningText)
                    && finishReason != "length";
                if (reasoningFallback)
                {
                    var fb = LlmAnswerSanitizer.Sanitize(reasoningText, m => _logger.Debug(m, "Ai"))?.Trim();
                    if (!string.IsNullOrEmpty(fb) && LooksLikeFinalAnswer(fb))
                    {
                        _logger.Warn($"Ask: content пуст — fallback на reasoning (эвристика «финальный ответ» ок, len={fb.Length})", "Ai");
                        RaiseExchange(isRequest: false, fb, sw.Elapsed.TotalSeconds, totalTokens);
                        return new AskResult(fb, promptTokens, completionTokens, totalTokens) { WasTruncated = finishReason == "length", UsageReported = usageSeen };
                    }
                    _logger.Warn($"Ask: reasoning отклонён эвристикой «финальный ответ» (len={reasoningText!.Length})", "Ai");
                    if (background)
                    {
                        // Фоновая речь/суфлёр: молчание — легальный ответ, показываем «(молчание)».
                        RaiseExchange(isRequest: false, "(молчание)", sw.Elapsed.TotalSeconds, totalTokens);
                        return new AskResult(null, promptTokens, completionTokens, totalTokens)
                            { EmptyKind = EmptyAnswerKind.ExpectedSilence, FinishReason = finishReason, UsageReported = usageSeen };
                    }
                    // Прямой запрос (чат, экран): «модель промолчала» было бы ложью —
                    // модель ответила рассуждениями, но финального ответа в них нет
                    // (аудит 2026-10-16 R1). Возвращаем TransportOrParse: юзер видит
                    // причину и может повторить запрос, а не молчание без ответа.
                    // Без ретрая уже здесь: повторив ТОТ ЖЕ запрос, мы получим тот же CoT.
                    RaiseExchange(isRequest: false, "Модель вернула только рассуждения, финальный ответ не сформирован", sw.Elapsed.TotalSeconds, totalTokens, isError: true);
                    return new AskResult(null, promptTokens, completionTokens, totalTokens)
                        { EmptyKind = EmptyAnswerKind.TransportOrParse, FinishReason = finishReason, UsageReported = usageSeen };
                }

                // §3.2: разводим «осознанное молчание» и «ответа нет по техпричине».
                var emptyKind = ClassifyEmptyAnswer(finishReason, completionTokens, maxTokens, background, usageSeen);
                switch (emptyKind)
                {
                    // finish=stop при пустом content на ФОНОВОЙ речи = осознанное
                    // молчание (промпт суфлёра сам требует тишины на small talk/шуме).
                    // Не ретраим — повтор бесполезен и добавляет латентность.
                    case EmptyAnswerKind.ExpectedSilence:
                        _logger.Info($"Ask: 200, пустой content при finish_reason=stop на фоне — модель промолчала (completion={completionTokens}/{maxTokens})", "Ai");
                        RaiseExchange(isRequest: false, "(молчание)", sw.Elapsed.TotalSeconds, totalTokens);
                        return new AskResult(null, promptTokens, completionTokens, totalTokens)
                            { EmptyKind = EmptyAnswerKind.ExpectedSilence, FinishReason = finishReason, UsageReported = usageSeen };

                    // Прямой запрос юзера (чат/secret): правило «молчи на small talk»
                    // на него не распространяется — молчание здесь аномалия. Один
                    // повтор тем же запросом (вложения/история/deadline сохраняются);
                    // если и он пуст — StopEmpty: caller покажет «ответа нет» вместо
                    // совета про лимит токенов.
                    case EmptyAnswerKind.StopEmpty:
                        if (allowEmptyRetry)
                        {
                            _logger.Warn("Ask: прямой запрос, пустой content при finish_reason=stop — один повтор без стрима", "Ai");
                            var retried = await AskCoreAsync(userText, systemPrompt, maxTokens, history, ct, null, background, requestTimeout, attachmentsContext, allowEmptyRetry: false, images, imageResolver);
                            // R4: первая попытка — уже оплаченный запрос со своим usage;
                            // без сложения счётчик терял её (attempt 1 usage = dropped).
                            return AccumulateAttemptUsage(promptTokens, completionTokens, totalTokens, retried);
                        }
                        _logger.Warn($"Ask: прямой запрос, пустой content при finish_reason=stop и после повтора — ответа не получено (completion={completionTokens}/{maxTokens})", "Ai");
                        RaiseExchange(isRequest: false, "Ответ от модели не получен", sw.Elapsed.TotalSeconds, totalTokens, isError: true);
                        return new AskResult(null, promptTokens, completionTokens, totalTokens)
                            { EmptyKind = EmptyAnswerKind.StopEmpty, FinishReason = finishReason, UsageReported = usageSeen };

                    // finish=length/max_tokens при пустом content: лимит реально съеден
                    // (usage≈запрошенному) — reasoning-модель сожгла бюджет на
                    // размышления (живой тест: tencent/hy3 при лимите 180). Повторный
                    // запрос с тем же лимитом даст то же самое — не ретраим.
                    case EmptyAnswerKind.LengthExhausted:
                        _logger.Warn($"Ask: пустой content при finish_reason={finishReason} — бюджет исчерпан по usage ({completionTokens}/{maxTokens}), без повтора{cot}", "Ai");
                        RaiseExchange(isRequest: false, $"Бюджет ответа исчерпан ({completionTokens}/{maxTokens} токенов)", sw.Elapsed.TotalSeconds, totalTokens, isError: true);
                        return new AskResult(null, promptTokens, completionTokens, totalTokens)
                            { EmptyKind = EmptyAnswerKind.LengthExhausted, FinishReason = finishReason, UsageReported = usageSeen };

                    // finish=length, но usage заметно ниже запрошенного лимита —
                    // провайдер обрезал вывод по своему внутреннему капу. Повтор
                    // тем же запросом упрётся в тот же кап — не ретраим.
                    case EmptyAnswerKind.ProviderCap:
                        _logger.Warn($"Ask: пустой content при finish_reason={finishReason}, но usage {completionTokens} ≪ maxTokens {maxTokens} — внутренний лимит провайдера, без повтора{cot}", "Ai");
                        RaiseExchange(isRequest: false, "Провайдер обрезал ответ по своему лимиту", sw.Elapsed.TotalSeconds, totalTokens, isError: true);
                        return new AskResult(null, promptTokens, completionTokens, totalTokens)
                            { EmptyKind = EmptyAnswerKind.ProviderCap, FinishReason = finishReason, UsageReported = usageSeen };

                    // Без достоверного finish_reason (null/нестандартный/битое тело) —
                    // подозрение на транспорт/парсинг, а не на решение модели: один
                    // повтор тем же запросом, буфером (SSE мог потерять дельты).
                    default:
                        if (allowEmptyRetry)
                        {
                            _logger.Warn($"Ask: пустой ответ без достоверного finish_reason (parseFailed={parseFailed}{cot}) — один повтор без стрима", "Ai");
                            var retried = await AskCoreAsync(userText, systemPrompt, maxTokens, history, ct, null, background, requestTimeout, attachmentsContext, allowEmptyRetry: false, images, imageResolver);
                            // R4: первая попытка — оплаченный запрос; её usage
                            // складывается с usage'ом повтора (иначе счётчик терял её).
                            return AccumulateAttemptUsage(promptTokens, completionTokens, totalTokens, retried);
                        }
                        _logger.Warn($"Ask: пустой ответ без достоверного finish_reason и после повтора (completion={completionTokens}/{maxTokens}{cot})", "Ai");
                        RaiseExchange(isRequest: false, "Пустой ответ от LLM", sw.Elapsed.TotalSeconds, totalTokens, isError: true);
                        return new AskResult(null, promptTokens, completionTokens, totalTokens)
                            { EmptyKind = EmptyAnswerKind.TransportOrParse, FinishReason = finishReason, UsageReported = usageSeen };
                }
            }
            // Литеральный «маркер молчания» вместо пустого content (2.16.0): модели
            // пишут «(empty)», «пустой ответ», «[silent]», «(空)» словами — это НЕ
            // ответ. Перехватываем ПОСЛЕ 200/sanitize, ДО отдачи на экран — в обеих
            // ветках (буфер и SSE: к этому месту контент уже склеен целиком).
            // Считаем осознанным молчанием, как finish_reason=stop при пустом content.
            if (LlmAnswerSanitizer.IsSilentMarkerAnswer(content_str))
            {
                _logger.Warn($"Ask: модель вернула литеральный маркер молчания «{Truncate(content_str!.Trim(), 60)}» — считаем молчанием", "Ai");
                RaiseExchange(isRequest: false, "(молчание)", sw.Elapsed.TotalSeconds, totalTokens);
                return new AskResult(null, promptTokens, completionTokens, totalTokens)
                    { EmptyKind = EmptyAnswerKind.ExpectedSilence, FinishReason = finishReason, UsageReported = usageSeen };
            }

            var result = content_str.Trim();

            if (finishReason == "length")
            {
                result = result.TrimEnd('.', ' ', ',', ';') + "…";
                _logger.Warn($"Answer was truncated by max_tokens (used {completionTokens}/{maxTokens}), appended '…'", "Ai");
            }

            _logger.Info($"Ask tokens: prompt={promptTokens}, completion={completionTokens}, total={totalTokens}", "Ai");
            _lastAskUtc = DateTime.UtcNow;
            RaiseExchange(isRequest: false, result, sw.Elapsed.TotalSeconds, totalTokens);
            return new AskResult(result, promptTokens, completionTokens, totalTokens) { WasTruncated = finishReason == "length", UsageReported = usageSeen };
            }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Отмена токеном (UI cancel / закрытие / memory-воркер) — не ошибка:
                // возвращаем AsCanceled, а не бросаем, чтобы caller не показал красную плашку.
                _logger.Info("Ask cancelled by parent token", "Ai");
                return AskResult.AsCanceled;
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                // Дедлайн ПОПЫТКИ сгорел (медленный сервер), а не отмена юзера.
                // Это не «нет соединения»: TCP/HTTP коннект был, просто ответа не
                // дождались. Ретраим — у каждой попытки свой attemptCts.
                var sec = (int)(requestTimeout ?? TimeSpan.FromSeconds(_config.RequestTimeoutSeconds)).TotalSeconds;
                _logger.Warn($"Ask error attempt {attempt + 1}: request timeout ({sec}s)", "Ai");
                if (attempt < _config.MaxRetries)
                {
                    try { await Backoff(attempt, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _logger.Info("Ask cancelled during retry backoff", "Ai");
                        return AskResult.AsCanceled;
                    }
                    continue;
                }
                ReportLlmHealth(background, false, "LLM-сервер не отвечает (таймаут)");
                RaiseExchange(isRequest: false, "LLM-сервер не отвечает (таймаут)", isError: true);
                return AskResult.Failed("LLM-сервер не отвечает (таймаут)");
            }
            catch (HttpRequestException ex)
            {
                if (IsConnectivityError(ex, attempt))
                {
                    ReportLlmHealth(background, false, "LLM-сервер недоступен (нет соединения)");
                    RaiseExchange(isRequest: false, "LLM-сервер недоступен (нет соединения)", isError: true);
                    return AskResult.Failed("LLM-сервер недоступен (нет соединения)");
                }
                _logger.Warn($"Ask error attempt {attempt + 1}: {ex.Message}", "Ai");
                if (attempt < _config.MaxRetries)
                {
                    try { await Backoff(attempt, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _logger.Info("Ask cancelled during retry backoff", "Ai");
                        return AskResult.AsCanceled;
                    }
                    continue;
                }
                ReportLlmHealth(background, false, "LLM-сервер недоступен (нет соединения)");
                RaiseExchange(isRequest: false, "LLM-сервер недоступен (нет соединения)", isError: true);
                return AskResult.Empty;
            }
        }
        return AskResult.Empty;
    }

    // R4 (аудит 2026-10-16): внутренний повтор (пустой ответ, SSE→буфер) — это
    // ОТДЕЛЬНЫЙ оплаченный HTTP-запрос, а не «бесплатный» ретрай. Usage первой
    // попытки не выбрасываем, а складываем с usage'ом повтора в итоговый результат,
    // поэтому каждый реальный запрос с usage-блоком доходит до TokenLedger ровно
    // однажды (инвариант «счётчик не врёт»). Смысловые поля (Content/Error/
    // EmptyKind/FinishReason) берём от последней попытки; нули первой (usage не
    // прислали) сумму не меняют.
    private static AskResult AccumulateAttemptUsage(int prompt, int completion, int total, AskResult retried) =>
        retried with
        {
            PromptTokens = retried.PromptTokens + prompt,
            CompletionTokens = retried.CompletionTokens + completion,
            TotalTokens = retried.TotalTokens + total,
            // Цифры реальные, если usage прислал ХОТЬ ОДИН из запросов —
            // NA-флаг («расход недоступен») в этом случае не поднимаем.
            UsageReported = retried.UsageReported || prompt > 0 || completion > 0 || total > 0
        };

    // §3.2: причина пустого content при HTTP 200. «stop»/«end_turn» — модель
    // дошла до конца фразы: для фоновой речи это осознанное молчание
    // (ExpectedSilence), для прямого запроса — аномалия, достойная одного
    // повтора (StopEmpty). «length»/«max_tokens» — лимит: при usage≈запрошенному
    // бюджет реально съеден (LengthExhausted), при usage заметно ниже — провайдер
    // обрезал вывод по своему внутреннему капу (ProviderCap), повтор тем же
    // лимитом бесполезен. Всё остальное (нет finish_reason, нестандартное
    // значение, битое тело) — TransportOrParse: подозрение на транспорт/парсинг.
    private static EmptyAnswerKind ClassifyEmptyAnswer(string? finishReason, int completionTokens,
        int maxTokens, bool background, bool usageSeen)
    {
        if (string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(finishReason, "end_turn", StringComparison.OrdinalIgnoreCase))
            return background ? EmptyAnswerKind.ExpectedSilence : EmptyAnswerKind.StopEmpty;
        if (finishReason != null &&
            (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
             finishReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) ||
             finishReason.Contains("max_output", StringComparison.OrdinalIgnoreCase)))
            // ProviderCap — только при ДОСТОВЕРНОМ usage: без цифр «0 < 80%»
            // означало бы провайдерский кап по отсутствующим данным, и обрезанный
            // reasoning-ответ не получил бы положенного продолжения. Нет usage —
            // считаем бюджет исчерпанным (finish_reason говорит об обрыве).
            return maxTokens > 0 && usageSeen && completionTokens < maxTokens * 0.8
                ? EmptyAnswerKind.ProviderCap
                : EmptyAnswerKind.LengthExhausted;
        return EmptyAnswerKind.TransportOrParse;
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken ct = default)
    {
        var url = EndpointUrl(Profile.ModelsPath);
        _logger.Debug($"CheckConnection → GET {url}", "Ai");
        // Дедлайн на весь чек: HttpClient.Timeout=Infinite ничего не режет.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));

        try
        {
            var sw = Stopwatch.StartNew();
            using var response = await _http.GetAsync(url, timeoutCts.Token);
            sw.Stop();
            _logger.Info($"CheckConnection ← {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms", "Ai");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Warn($"CheckConnection failed: {ex.Message}", "Ai");
            return false;
        }
    }

    // Честная проверка LLM: ключ/URL + РЕАЛЬНЫЙ чат-пинг моделью. Раньше кнопка проверяла только
    // GET /models (200, если ключ ок) и врала «ОК», хотя боевой чат падал на неверной/недоступной
    // модели или политике данных free-моделей. Теперь бьём и по чату — та же ошибка, что в бою.
    // Признак reasoning-модели (тратит токены на «размышления»; при малом max_tokens content
    // может прийти пустым). tencent/hy3 в старом allowlist не было — из-за этого «Проверить
    // подключение» говорило ОК, а боевые ответы приходили пустыми (живой тест).
    private static bool IsReasoningModel(string modelLower) =>
        modelLower.Contains("gpt-oss") || modelLower.Contains("o1") || modelLower.Contains("o3") ||
        modelLower.Contains("reasoning") || modelLower.Contains("hy3") || modelLower.Contains("hunyuan") ||
        modelLower.Contains("deepseek-r") || modelLower.Contains("deepseek-v") ||
        modelLower.Contains("qwq") || modelLower.Contains("think");

    // Подтверждённые контракты «ФИНАЛЬНЫЙ ответ приходит в reasoning_content/reasoning при
    // пустом content» (аудит 2026-10-16 R1): допуск дан ТОЛЬКО паре (провайдер, подстрока
    // модели). Живые подтверждения: Groq gpt-oss-120b кладёт ответ в message.reasoning
    // (прод-лог diagnostics_20261005); opencode-go deepseek-v4.1-flash шлёт ответ в
    // reasoning_content (app.1.log «в ответе только рассуждения» ×12 → пустой экран).
    // Неподтверждённый/custom-провайдер допуска НЕ получает: имя модели — это не контракт
    // (deepseek-v-... на случайном провайдере ответа в reasoning не кладёт, а фолбэк уже
    // готов был показать CoT). Новую пару добавлять только с подобным подтверждением.
    private static readonly (string ProviderId, string ModelSubstring)[] s_reasoningAnswerContracts = new[]
    {
        ("opencode-go", "deepseek-v"),
        ("groq", "gpt-oss"),
    };

    private static bool AnswersInReasoningContent(string providerId, string modelLower) =>
        s_reasoningAnswerContracts.Any(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
            modelLower.Contains(p.ModelSubstring, StringComparison.Ordinal));

    // Эвристика «reasoning похож на ФИНАЛЬНЫЙ ответ, а не на размышление вслух»
    // (2.8.x п.2): страховка от показа сырого CoT. Отклоняем: 1) старт с типового
    // CoT-маркера («Okay,», «Let me», «The user», «Итак», «Хм»…); 2) длину <2 или
    // >2000 символов (простыня — почти всегда дамп мыслей); 3) нумерованный список
    // шагов («1. …», «2) …», «Step N», «Шаг N») в ≥2 строках. Маркеры держим
    // синхронно с s_cotPreambleStart в LlmAnswerSanitizer.
    private static readonly Regex s_cotHead = new(
        @"^\s*(?:" +
        @"okay\b|ok\b|so[,\s]|let me|let's\b|the user|the (?:question|request|task|prompt|input|message|phrase|text)|" +
        @"i (?:need|have|must|should|will|am|want)|first[,\s]|hmm+\b|wait\b|alright\b|well[,\s]|" +
        @"итак\b|хм+\b|думаю\b|мне нужно|сначала|давайте?|ну[,\s])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_stepLine = new(
        @"^\s*(?:\d+[.)]\s|(?:step|шаг)\s+\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool LooksLikeFinalAnswer(string? reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning)) return false;
        var s = reasoning.Trim();
        if (s.Length < 2 || s.Length > 2000) return false;
        var firstLine = s.Split('\n', 2)[0];
        if (s_cotHead.IsMatch(firstLine)) return false;
        var stepLines = 0;
        foreach (var line in s.Split('\n'))
            if (s_stepLine.IsMatch(line)) stepLines++;
        if (stepLines >= 2) return false;
        return true;
    }

    public async Task<(bool ok, string detail)> CheckLlmConnectionAsync(CancellationToken ct = default)
        => Collapse(await CheckLlmConnectionDetailedAsync(ct));

    /// <summary>
    /// Проверка подключения С БОЕВЫМИ ПАРАМЕТРАМИ. Раньше пинг шёл с max_tokens=1 без системного
    /// промпта и судил по HTTP 200 — reasoning-модель «отвечала» в настройках (200), а в бою
    /// возвращала пустой content (лимит съеден размышлениями): «ОК» в настройках, ошибка в окне.
    /// Теперь: тот же max_tokens и системный промпт, что у реальных запросов, и проверка,
    /// что content непустой.
    /// </summary>
    public async Task<(bool ok, string detail)> CheckLlmConnectionAsync(int realMaxTokens, string? systemPrompt, CancellationToken ct = default)
        => Collapse(await CheckLlmDetailedCoreAsync(realMaxTokens, systemPrompt, ct));

    /// <summary>Сводка по шагам: Chat важен; Models может отсутствовать у провайдера — это warn,
    /// а не фейл. detail берём из первого плохого шага, иначе — «модель отвечает».</summary>
    private static (bool ok, string detail) Collapse(List<CheckStepResult> steps)
    {
        var chat = steps.FirstOrDefault(s => s.Name == "Chat");
        var ok = chat?.Ok == true; // Chat-шаг обязан быть и пройти (пропуск по протоколу — тоже ок)
        var bad = steps.FirstOrDefault(s => !s.Ok && s.Name != "Models");
        return (ok, bad?.Detail ?? (ok ? $"OK — модель отвечает" : "Проверка не пройдена"));
    }

    /// <summary>
    /// Пошаговая проверка LLM для кнопки «Проверить подключение»: URL → заголовки →
    /// GET {ModelsPath} → POST {ChatPath}. Каждый шаг — строка в отчёте UI; проверка идёт
    /// до конца, даже если промежуточный шаг упал (Models может не существовать — warn).
    /// </summary>
    public Task<List<CheckStepResult>> CheckLlmConnectionDetailedAsync(CancellationToken ct = default)
        => CheckLlmDetailedCoreAsync(0, null, ct);

    private async Task<List<CheckStepResult>> CheckLlmDetailedCoreAsync(int realMaxTokens, string? systemPrompt, CancellationToken ct)
    {
        var steps = new List<CheckStepResult>();
        // Один дедлайн на весь чек: HttpClient.Timeout=Infinite ничего не режет,
        // а зависший прокси без CTS повесил бы «Проверить» навсегда.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));
        ct = timeoutCts.Token;

        var profile = Profile;
        var baseUrl = _config.BaseUrl.TrimEnd('/');

        // 1) URL — валидация базы (A7: подсказка про «только базу вида …/v1»).
        var urlOk = Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
                    (baseUri.Scheme == "http" || baseUri.Scheme == "https");
        steps.Add(urlOk
            ? new CheckStepResult("URL", true, baseUrl)
            : new CheckStepResult("URL", false,
                string.IsNullOrWhiteSpace(baseUrl)
                    ? "BaseUrl пуст — укажите базу API, например https://api.openai.com/v1"
                    : $"«{baseUrl}» не похоже на базу API — укажите только базу вида https://host/v1 (без /chat/completions или /responses)"));
        var modelsUrl = urlOk ? EndpointUrl(profile.ModelsPath) : null;
        var chatUrl = urlOk ? EndpointUrl(profile.ChatPath) : null;

        // 2) Auth — заголовки профиля применены (ключ может быть пустым у локальных auth=none).
        // Перед этим — обновление истёкшего токена подписки ChatGPT: иначе проверка
        // упала бы с 401 на живом, но протухшем токене.
        await EnsureCodexTokenAsync(ct).ConfigureAwait(false);
        ApplyHeaders();
        var hasAuth = _http.DefaultRequestHeaders.Authorization != null ||
                      _http.DefaultRequestHeaders.Contains("x-api-key");
        var extraCount = profile.ExtraHeaders.Count(h => _http.DefaultRequestHeaders.Contains(h.Key));
        steps.Add(profile.AuthKind == "none"
            ? new CheckStepResult("Auth", true, $"Без авторизации ({profile.Name}){(extraCount > 0 ? $", заголовков: {extraCount}" : "")}")
            : new CheckStepResult("Auth", hasAuth,
                hasAuth ? $"Ключ применён ({profile.AuthKind}){(extraCount > 0 ? $", заголовков: {extraCount}" : "")}"
                        : "Ключ API пустой — введите ключ провайдера"));

        // 3) Models — GET {ModelsPath}. Отсутствие эндпойнта (404/405) — warn, не стоп:
        //    у части провайдеров /models нет, а чат работает.
        if (modelsUrl == null)
            steps.Add(new CheckStepResult("Models", false, "пропущен — невалидный URL"));
        else
            try
            {
                using var r1 = await _http.GetAsync(modelsUrl, ct);
                var b1 = await r1.Content.ReadAsStringAsync(ct);
                if (r1.IsSuccessStatusCode)
                {
                    var count = CountModelIds(b1);
                    steps.Add(new CheckStepResult("Models", true, $"OK — моделей в списке: {count}"));
                }
                else
                {
                    _logger.Warn($"CheckLlm {profile.ModelsPath} ← {(int)r1.StatusCode}: {Truncate(b1, 200)}", "Ai");
                    steps.Add(new CheckStepResult("Models", false, HumanizeApiError(r1.StatusCode, ShortApiError(r1.StatusCode, b1))));
                }
            }
            catch (Exception ex) { _logger.Warn($"CheckLlm models failed: {ex.Message}", "Ai"); steps.Add(new CheckStepResult("Models", false, $"Нет связи: {ex.Message}")); }

        // 4) Chat — боевой пинг POST {ChatPath}; у протоколов без chat/completions — пропуск.
        // openai-responses (Responses API) проверяем так же: клиент говорит на этом
        // протоколе, поэтому шаг пропускать нельзя — иначе «проверка подключения»
        // ничего не говорит о живости модели.
        var chatModel = NormalizeModelId(_config.ChatModel);
        if (profile.Protocol is not ("openai-chat" or ResponsesProtocol.Protocol) || string.IsNullOrEmpty(profile.ChatPath))
            steps.Add(new CheckStepResult("Chat", true, $"пропущен (протокол {profile.Protocol})"));
        else if (string.IsNullOrWhiteSpace(chatModel))
            steps.Add(new CheckStepResult("Chat", false, "Модель не указана — впишите модель провайдера в поле «Модель»."));
        else if (chatUrl == null)
            steps.Add(new CheckStepResult("Chat", false, "пропущен — невалидный URL"));
        else
            steps.Add(await CheckChatStepAsync(profile, chatModel, chatUrl, realMaxTokens, systemPrompt, ct));
        return steps;
    }

    // Шаг «Chat»: POST {ChatPath} с боевым промптом; пустой content → авто-ретрай с лимитом
    // 1024 (reasoning-модель съедает маленький лимит на размышления). Фолбэк на
    // reasoning_content — только для allowlist-моделей (как в боевом AskAsync),
    // прочим reasoning-моделям их CoT ответом не считаем.
    private async Task<CheckStepResult> CheckChatStepAsync(LlmProviderProfile profile, string chatModel, string chatUrl,
        int realMaxTokens, string? systemPrompt, CancellationToken ct)
    {
        var maxTokens = realMaxTokens > 0 ? realMaxTokens : 60;
        // Responses-протокол (OpenAI Responses API): тело запроса другое, и лимита
        // вывода в нём НЕТ (бэкенд его отвергает) — «ретрай с 1024 токена» тут
        // бессмысленен: запрос получится тем же.
        var isResponses = profile.Protocol == ResponsesProtocol.Protocol;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var checkEffort = _config.ReasoningEffortOverride ?? profile.ReasoningEffort;
                Dictionary<string, object>? req = null;
                string? responsesBody = null;
                if (isResponses)
                {
                    // Минимальный боевой запрос Responses: model/instructions/input/stream.
                    // Sampling-параметры не отправляем — бэкенд их отвергает; лимит вывода
                    // шлём только там, где бэкенд его принимает.
                    var checkCap = ResponsesProtocol.AcceptsOutputCaps(profile.BaseUrl) ? maxTokens : (int?)null;
                    // Бэкенд подписки ChatGPT/Codex отвечает только потоком:
                    // stream=false → 400 «Stream must be set to true».
                    responsesBody = ResponsesProtocol.BuildRequest(chatModel, systemPrompt,
                        "Ответь ровно одним словом: готов", null, checkEffort,
                        stream: profile.RequiresStreaming, checkCap);
                }
                else
                {
                    var messages = new List<object>();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                        messages.Add(new { role = "system", content = systemPrompt });
                    messages.Add(new { role = "user", content = "Ответь ровно одним словом: готов" });
                    req = new Dictionary<string, object>
                    {
                        { "model", chatModel },
                        { "messages", messages },
                        { "max_tokens", maxTokens }
                    };
                    if (IsReasoningModel(chatModel.ToLowerInvariant()) && !string.IsNullOrEmpty(checkEffort))
                        req["reasoning"] = new { effort = checkEffort };
                }

                using var r2 = isResponses
                    ? await _http.PostAsync(chatUrl,
                        new StringContent(responsesBody!, Encoding.UTF8, "application/json"), ct)
                    : await _http.PostAsJsonAsync(chatUrl, req!, _jsonOpts, ct);
                // Провайдер мог ответить SSE даже при stream=false — такой ответ читаем как поток.
                var ctHeader = r2.Content.Headers.ContentType?.MediaType;
                var sseReply = r2.IsSuccessStatusCode && ctHeader != null &&
                               ctHeader.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);
                var b2 = sseReply ? "" : await r2.Content.ReadAsStringAsync(ct);
                // Бэкенд подписки ChatGPT/Codex отдаёт SSE без Content-Type —
                // узнаём поток по началу тела, иначе ответ не распарсится.
                var sseBody = !sseReply && isResponses && ResponsesProtocol.LooksLikeSse(b2);
                _logger.Info($"CheckLlm chat[{chatModel}] ← {(int)r2.StatusCode} (maxTokens={maxTokens})", "Ai");
                if (!r2.IsSuccessStatusCode)
                {
                    _logger.Warn($"CheckLlm chat ← {(int)r2.StatusCode}: {Truncate(b2, 400)}", "Ai");
                    var err = $"Модель «{chatModel}»: {HumanizeApiError(r2.StatusCode, ShortApiError(r2.StatusCode, b2))}";
                    ReportHealth(ApiComponent.Llm, false, err, (int)r2.StatusCode); // реальная ошибка — в плашку тоже
                    return new CheckStepResult("Chat", false, err);
                }

                // HTTP 200 ещё не успех: проверяем НЕПУСТОЙ content — ровно как в боевом запросе,
                // включая конверт {"data":{…}}. reasoning_content/reasoning_details не считаем
                // ответом: это мысли модели, юзеру они не показываются.
                string content = "";
                string? providerError = null;
                try
                {
                    if (isResponses)
                    {
                        // Responses: output[].content[].text (буфер) или SSE-события.
                        if (sseReply)
                        {
                            var parser = await ResponsesProtocol.ReadSseAsync(r2, null, ct);
                            content = parser.ResolveContent();
                            providerError = parser.Error;
                        }
                        else if (sseBody)
                        {
                            // Поток без Content-Type: тело целиком, разбираем как SSE.
                            var parser = ResponsesProtocol.ParseSse(b2);
                            content = parser.ResolveContent();
                            providerError = parser.Error;
                        }
                        else
                        {
                            var parsed = ResponsesProtocol.ParseResponse(b2);
                            content = parsed.Content ?? "";
                            providerError = parsed.Error;
                        }
                    }
                    else
                    {
                        using var doc = JsonDocument.Parse(b2);
                        var root = UnwrapDataEnvelope(doc.RootElement);
                        var c = TryGetByPath(root, profile.ContentPath)?.GetString();
                        content = c ?? "";
                        // R1 (аудит 2026-10-16): допуск reasoning-как-ответа — только для
                        // подтверждённой пары (провайдер, модель), как в боевом AskAsync.
                        // Прочим reasoning-моделям reasoning ≠ ответ (иначе CoT протекает
                        // в «Проверить подключение = ОК»).
                        if (string.IsNullOrWhiteSpace(content) &&
                            AnswersInReasoningContent(profile.Id, chatModel.ToLowerInvariant()) &&
                            TryGetByPath(root, "choices[0].message") is JsonElement m)
                        {
                            if (m.TryGetProperty("reasoning_content", out var rc) && !string.IsNullOrWhiteSpace(rc.GetString()))
                                content = rc.GetString()!;
                            else if (m.TryGetProperty("reasoning_details", out var rd) &&
                                     rd.ValueKind == JsonValueKind.Array && rd.GetArrayLength() > 0 &&
                                     rd[0].TryGetProperty("text", out var rdt) && !string.IsNullOrWhiteSpace(rdt.GetString()))
                                content = rdt.GetString()!;
                            else if (m.TryGetProperty("reasoning", out var rsn) &&
                                     rsn.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rsn.GetString()))
                                content = rsn.GetString()!; // Groq gpt-oss: ответ в message.reasoning
                        }
                    }
                }
                catch { /* нестандартный ответ провайдера — считаем пустым */ }

                // Ошибка провайдера внутри 200-ответа (Responses: {"error":{"message":"…"}})
                // — это не «пустой ответ»: показываем текст ошибки, а не «нет ответа».
                if (!string.IsNullOrEmpty(providerError))
                {
                    var perr = $"Модель «{chatModel}»: {providerError}";
                    _logger.Warn($"CheckLlm chat: провайдер вернул ошибку — {providerError}", "Ai");
                    ReportHealth(ApiComponent.Llm, false, perr, (int)r2.StatusCode);
                    return new CheckStepResult("Chat", false, perr);
                }

                if (!string.IsNullOrWhiteSpace(content))
                {
                    // Настоящий успех = LLM жив → ГАСИМ возможную залипшую плашку «ошибка LLM»
                    // (она могла остаться со старта, когда конфиг был другой/без ключа).
                    ReportHealth(ApiComponent.Llm, true, "LLM-сервер отвечает");
                    return new CheckStepResult("Chat", true, $"OK — модель {chatModel} отвечает");
                }

                // Пусто при малом лимите → один ретрай с 1024 (как в боевом AskAsync).
                if (!isResponses && attempt == 0 && maxTokens < 1024)
                {
                    _logger.Warn($"CheckLlm: 200, но content пуст (maxTokens={maxTokens}) — ретрай с 1024", "Ai");
                    maxTokens = 1024;
                    continue;
                }
                var errEmpty = isResponses
                    ? $"Модель «{chatModel}» вернула ПУСТОЙ ответ — бэкенд не прислал текст. " +
                      "Проверьте ID модели и доступ к провайдеру."
                    : $"Модель «{chatModel}» вернула ПУСТОЙ ответ при лимите {maxTokens} токенов — " +
                      "reasoning-модель «съедает» лимит на размышления. Увеличьте «Максимум токенов ответа» или смените модель.";
                _logger.Warn($"CheckLlm: 200, но content пуст (модель {chatModel}, maxTokens={maxTokens})", "Ai");
                ReportHealth(ApiComponent.Llm, false, errEmpty, (int)r2.StatusCode);
                return new CheckStepResult("Chat", false, errEmpty);
            }
            catch (Exception ex)
            {
                _logger.Warn($"CheckLlm chat failed: {ex.Message}", "Ai");
                return new CheckStepResult("Chat", false, ex.Message);
            }
        }
        return new CheckStepResult("Chat", false, "проверка не завершилась");
    }

    /// <summary>GET {ModelsPath} провайдера → список ID моделей для комбобокса в настройках.
    /// Парсит OpenAI-формат {"data":[{"id":…}]} и anthropic {"data"}/массив на будущее;
    /// при ошибке — пустой список (UI предложит ручной ввод).</summary>
    public async Task<List<string>> GetModelsAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        // Дедлайн как у остальных check-вызовов (HttpClient.Timeout=Infinite).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));
        ct = timeoutCts.Token;

        try
        {
            ApplyHeaders();
            var url = EndpointUrl(Profile.ModelsPath);
            using var r = await _http.GetAsync(url, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode)
            {
                _logger.Warn($"GetModels ← {(int)r.StatusCode}: {Truncate(body, 200)}", "Ai");
                return result;
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // OpenAI: {"object":"list","data":[{"id":"…"},…]}; Anthropic: {"data":[{"id":…}]};
            // бывает и голый массив [{"id":…}].
            JsonElement list = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (!root.TryGetProperty("data", out list) &&
                    !root.TryGetProperty("models", out list))
                    list = root;
            }
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id))
                    {
                        var s = id.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                    }
                }
            _logger.Info($"GetModels ← 200, моделей: {result.Count}", "Ai");
        }
        catch (Exception ex) { _logger.Warn($"GetModels failed: {ex.Message}", "Ai"); }
        return result;
    }

    // Счётчик моделей для детали шага «Models» (не бросает — любой мусор = 0).
    private static int CountModelIds(string body)
        // Форматы: OpenAI {"data":[…]}, бэкенд подписки ChatGPT/Codex
        // {"models":[{"slug":…}]}, голый массив — парсер общий (ProviderModelLister).
        => ProviderModelLister.ParseModels(body).Count;

    // Обход JsonElement по пути вида "choices[0].message.content" / "content[0].text"
    // (ContentPath профиля). null — сегмент не найден.
    private static JsonElement? TryGetByPath(JsonElement root, string path)
    {
        var el = root;
        foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = seg;
            var bracket = seg.IndexOf('[');
            if (bracket >= 0) name = seg[..bracket];
            if (name.Length > 0)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out el))
                    return null;
            }
            if (bracket >= 0)
            {
                if (el.ValueKind != JsonValueKind.Array) return null;
                var idx = seg.IndexOf(']', bracket);
                if (idx < 0 || !int.TryParse(seg[(bracket + 1)..idx], out var i) || i < 0 || i >= el.GetArrayLength())
                    return null;
                el = el[i];
            }
        }
        return el;
    }

    // Авто-распаковка конверта {"data":{…}}: часть шлюзов отдаёт тело ответа внутри data.
    private static JsonElement UnwrapDataEnvelope(JsonElement root)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
           !root.TryGetProperty("choices", out _) && d.TryGetProperty("choices", out _)
            ? d : root;

    // Результат чтения SSE-стрима: склеенный контент, склеенные reasoning-дельты
    // (у gpt-oss ответ может лежать в delta.reasoning при пустом content), usage
    // из финального чанка, finish_reason последнего choice-чанка. Aborted —
    // поток кончился без [DONE].
    private sealed record SseResult(string Content, string? FinishReason,
        int PromptTokens, int CompletionTokens, int TotalTokens, int DeltaCount, bool Aborted,
        string? Reasoning)
    {
        /// <summary>Ошибка провайдера в потоке (Responses: response.failed / event «error»).</summary>
        public string? Error { get; init; }
    }

    // Читает text/event-stream построчно: data: {json}-чанки → дельты по
    // Profile.DeltaPath → onDelta (на потоке вызывающего), data: [DONE] — конец.
    // Битый JSON-чанк пропускаем (warn). reasoning-дельты в буфер копим (у
    // gpt-oss весь ответ может прийти в delta.reasoning при пустом content —
    // иначе стрим ушёл бы в IsSilent/ретрай), но на экран через onDelta не идут.
    // Исключение из onDelta гасим: баг в UI-колбэке не должен убивать стрим.
    private async Task<SseResult> ReadSseStreamAsync(HttpResponseMessage response, Action<string> onDelta, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var rsn = new StringBuilder();
        string? finishReason = null;
        int promptTokens = 0, completionTokens = 0, totalTokens = 0, deltaCount = 0;
        var done = false;
        var deltaPath = string.IsNullOrEmpty(Profile.DeltaPath) ? "choices[0].delta.content" : Profile.DeltaPath;
        var onDeltaThrew = false;
        var deltaPrefix = deltaPath[..(deltaPath.LastIndexOf('.') + 1)]; // "choices[0].delta."

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") { done = true; break; }
            if (data.Length == 0) continue;
            JsonElement chunkRoot;
            try
            {
                using var doc = JsonDocument.Parse(data);
                chunkRoot = UnwrapDataEnvelope(doc.RootElement).Clone();
            }
            catch (JsonException)
            {
                _logger.Warn($"Ask SSE: нераспарсенный чанк ({Truncate(data, 120)})", "Ai");
                continue;
            }
            if (TryGetByPath(chunkRoot, deltaPath) is JsonElement delta && delta.ValueKind == JsonValueKind.String)
            {
                var d = delta.GetString();
                if (!string.IsNullOrEmpty(d))
                {
                    sb.Append(d);
                    deltaCount++;
                    if (!onDeltaThrew)
                    {
                        try { onDelta(d); }
                        catch (Exception cbEx)
                        {
                            onDeltaThrew = true;
                            _logger.Warn($"Ask SSE: onDelta бросил исключение ({cbEx.Message}) — дальше без колбэка", "Ai");
                        }
                    }
                }
            }
            // Тот же порядок, что у буферного парсинга: reasoning_content →
            // reasoning_details[0].text → reasoning (Groq gpt-oss). Дельты
            // склеиваем — фолбэк на ответ из reasoning делается в AskAsync.
            if (TryGetByPath(chunkRoot, deltaPrefix + "reasoning_content") is JsonElement rc &&
                rc.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rc.GetString()))
                rsn.Append(rc.GetString());
            else if (TryGetByPath(chunkRoot, deltaPrefix + "reasoning_details") is JsonElement rd &&
                     rd.ValueKind == JsonValueKind.Array && rd.GetArrayLength() > 0 &&
                     rd[0].TryGetProperty("text", out var rdt) && !string.IsNullOrEmpty(rdt.GetString()))
                rsn.Append(rdt.GetString());
            else if (TryGetByPath(chunkRoot, deltaPrefix + "reasoning") is JsonElement rsnEl &&
                     rsnEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rsnEl.GetString()))
                rsn.Append(rsnEl.GetString());
            if (TryGetByPath(chunkRoot, "choices[0].finish_reason") is JsonElement fr &&
                fr.ValueKind == JsonValueKind.String)
                finishReason = fr.GetString();
            if (TryGetByPath(chunkRoot, Profile.UsagePath) is JsonElement u && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                else if (u.TryGetProperty("input_tokens", out var it)) promptTokens = it.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var ct2)) completionTokens = ct2.GetInt32();
                else if (u.TryGetProperty("output_tokens", out var ot)) completionTokens = ot.GetInt32();
                if (u.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
            }
        }
        return new SseResult(sb.ToString(), finishReason, promptTokens, completionTokens, totalTokens, deltaCount,
            Aborted: !done, Reasoning: rsn.Length > 0 ? rsn.ToString() : null);
    }

    // SSE-стрим протокола Responses (OpenAI Responses API): события
    // response.output_text.delta (текст), response.output_text.done (финальный
    // текст — запасной вариант, если дельт не было), response.completed /
    // response.incomplete (статус + usage), response.failed и event «error»
    // (ошибка провайдера — её показываем юзеру). Разбор — в ResponsesProtocol.
    // Терминальным считается и response.completed, и «data: [DONE]»: [DONE] может
    // не приходить, а completed приходит всегда. Поток без терминального события,
    // но с текстом, — не «обрыв»: отдаём накопленный текст (warn в лог).
    private async Task<SseResult> ReadResponsesSseAsync(HttpResponseMessage response, Action<string> onDelta,
        CancellationToken ct)
        => await ReadResponsesSseAsync(await OpenReaderAsync(response, ct), onDelta, ct);

    /// <summary>Тот же разбор SSE, но из уже открытого reader'а (тело без Content-Type).</summary>
    private async Task<SseResult> ReadResponsesSseAsync(TextReader reader, Action<string> onDelta,
        CancellationToken ct)
    {
        var parser = await ResponsesProtocol.ReadSseAsync(reader, onDelta, ct);
        if (parser.ParseFailures > 0)
            _logger.Warn($"Ask Responses SSE: нераспарсенных чанков — {parser.ParseFailures}", "Ai");
        if (parser.OnDeltaThrew)
            _logger.Warn("Ask Responses SSE: onDelta бросил исключение — дальше без колбэка", "Ai");
        var content = parser.ResolveContent();
        var aborted = !parser.Terminal && string.IsNullOrEmpty(content);
        if (!parser.Terminal && !aborted)
            _logger.Warn("Ask Responses SSE: поток закрылся без response.completed — отдаём накопленный текст", "Ai");
        return new SseResult(content, parser.FinishReason, parser.PromptTokens, parser.CompletionTokens,
            parser.TotalTokens, parser.DeltaCount, aborted, Reasoning: null) { Error = parser.Error };
    }

    /// <summary>Открывает reader по телу ответа (поток не читаем — только готовимся).</summary>
    private static async Task<StreamReader> OpenReaderAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new StreamReader(stream, Encoding.UTF8);
    }

    /// <summary>
    /// Открывает тело ответа Responses и определяет формат по первому байту:
    /// бэкенд подписки ChatGPT/Codex отдаёт text/event-stream БЕЗ заголовка
    /// Content-Type, и по заголовку поток не отличить от JSON. Прочитанные
    /// байты возвращаем в поток (PrefixedStream) — тело не теряется и не
    /// буферизуется целиком, стрим остаётся живым.
    /// </summary>
    private static async Task<(bool IsSse, StreamReader Reader)> OpenResponsesBodyAsync(HttpResponseMessage response,
        CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var probe = new byte[64];
        var read = 0;
        // Метку формата ищем по первому «значимому» байту: BOM и ведущие
        // пробельные символы (их добавляют прокси) не должны прятать SSE.
        while (read < probe.Length)
        {
            var n = await stream.ReadAsync(probe.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0) break;
            read += n;
            if (FirstMeaningfulByte(probe, read) >= 0) break;
        }
        var idx = FirstMeaningfulByte(probe, read);
        // '{' или '[' — JSON; 'e' (event:) или 'd' (data:) — SSE.
        var isSse = idx >= 0 && (probe[idx] == (byte)'e' || probe[idx] == (byte)'d');
        // Тело ошибки (не-2xx) SSE-парсером не разбираем: иначе текст ошибки
        // потерялся бы и юзер увидел бы пустой ответ вместо кода ответа.
        if (!response.IsSuccessStatusCode) isSse = false;
        var combined = read > 0 ? new PrefixedStream(probe, read, stream) : stream;
        return (isSse, new StreamReader(combined, Encoding.UTF8));
    }

    /// <summary>
    /// Индекс первого значимого байта в буфере: пропускает UTF-8 BOM и
    /// пробельные символы. -1 — в прочитанном куске значимых байтов нет.
    /// </summary>
    private static int FirstMeaningfulByte(byte[] buffer, int count)
    {
        var i = 0;
        if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) i = 3;
        while (i < count)
        {
            var b = buffer[i];
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n') return i;
            i++;
        }
        return -1;
    }

    private static bool IsRetryable(HttpStatusCode code)
    {
        var n = (int)code;
        return n == 429 || (n >= 500 && n < 600);
    }

    private bool IsConnectivityError(Exception ex, int attempt)
    {
        if (attempt > 0) return false;
        var isConnectivity = ex is HttpRequestException hre &&
                              (hre.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                               hre.Message.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
                               hre.Message.Contains("resolve", StringComparison.OrdinalIgnoreCase) ||
                               hre.Message.Contains("Name or service", StringComparison.OrdinalIgnoreCase) ||
                               hre.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
                               // DNS-ошибка с локализованным текстом (аудит 15.10:
                               // «Этот хост неизвестен. (opencode.ai:443)») — раньше
                               // уходила в ретрай-ветку, а не в fail-fast «нет связи».
                               hre.Message.Contains("неизвестен", StringComparison.OrdinalIgnoreCase) ||
                               hre.InnerException?.Message?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true);
        if (isConnectivity)
            _logger.Warn($"Connectivity error on first attempt — fail fast: {ex.Message}", "Ai");
        return isConnectivity;
    }

    private static Task Backoff(int attempt, CancellationToken ct)
    {
        var ms = 1000 * (int)Math.Pow(2, attempt);
        return Task.Delay(ms, ct);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
    // ── Исходящие MCP tool_calls ────────────────────────────────────────────
    // Отдельный метод (AskAsync не трогаем): нативный function-calling формат
    // OpenAI — tools[] в запросе, tool_calls[]/finish_reason="tool_calls" в ответе.
    // Стриминг собирает аргументы по index вызова; шлюз, проигнорировавший
    // stream:true, уходит в буферный парсинг тем же кодом.

    /// <summary>
    /// Запрос с инструментами (для исходящего MCP-цикла). messages — полный массив
    /// (system/user/assistant/tool). tools — адаптерные имена + JSON Schema.
    /// </summary>
    public async Task<ToolAskResult> AskWithToolsAsync(
        IReadOnlyList<ToolLoopMessage> messages,
        IReadOnlyList<ToolSpecDto> tools,
        int maxTokens,
        CancellationToken ct = default,
        bool stream = true,
        IReadOnlyList<ChatImagePayload>? images = null)
    {
        // Картинки вопроса крепятся к последнему user-сообщению цикла
        // (там живёт актуальный вопрос; ТЗ §7/§9).
        if (images is { Count: > 0 })
        {
            var list = new List<ToolLoopMessage>(messages);
            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i].Role == "user")
                {
                    var merged = new List<ChatImagePayload>(list[i].ImagePayloads ?? Array.Empty<ChatImagePayload>());
                    merged.AddRange(images);
                    list[i] = list[i] with { ImagePayloads = merged };
                    break;
                }
            messages = list;
        }
        var hasToolImages = messages.Any(m => m.ImagePayloads is { Count: > 0 });
        if (hasToolImages && Profile.Protocol != "openai-chat")
            return ToolAskResult.Failed(new McpError("IMG_ADAPTER_UNSUPPORTED",
                "выбранный адаптер API не принимает изображения"));
        ApplyHeaders();
        var url = EndpointUrl(Profile.ChatPath);

        if (_config.RateLimitSeconds > 0)
        {
            try
            {
                await _rateLimitGate.WaitAsync(ct);
                try
                {
                    var elapsed = (DateTime.UtcNow - _lastAskUtc).TotalSeconds;
                    var wait = _config.RateLimitSeconds - elapsed;
                    if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }
                finally { _rateLimitGate.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ToolAskResult.Failed(new McpError("CANCELLED", "Запрос отменён"));
            }
        }
        _lastAskUtc = DateTime.UtcNow;

        var chatModel = NormalizeModelId(_config.ChatModel);
        var toolsArr = tools.Count == 0 ? null : tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description ?? "",
                parameters = t.ParametersSchema.ValueKind == JsonValueKind.Object
                    ? t.ParametersSchema
                    : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
            },
        }).ToArray();

        var req = new Dictionary<string, object?>
        {
            ["model"] = chatModel,
            ["messages"] = SerializeToolLoopMessages(messages),
            ["temperature"] = 0.3,
            ["max_tokens"] = maxTokens,
        };
        if (toolsArr != null)
        {
            req["tools"] = toolsArr;
            req["tool_choice"] = "auto";
        }
        var reasoningEffort = _config.ReasoningEffortOverride ?? Profile.ReasoningEffort;
        if (IsReasoningModel(chatModel.ToLowerInvariant()) && !string.IsNullOrEmpty(reasoningEffort))
            req["reasoning"] = new { effort = reasoningEffort };

        var streaming = stream && Profile.Protocol == "openai-chat" && !_streamDisabled;
        if (streaming)
        {
            req["stream"] = true;
            req["stream_options"] = new { include_usage = true };
        }

        RaiseExchange(isRequest: true, LastUserText(messages));

        for (var attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(_config.RequestTimeoutSeconds * 4)); // tool-ответы длиннее
            var rct = attemptCts.Token;
            try
            {
                HttpResponseMessage response;
                string? body = null;
                var isSse = false;
                if (streaming)
                {
                    using var msg = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(req, _jsonOpts), Encoding.UTF8, "application/json")
                    };
                    response = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, rct);
                    var ctHeader = response.Content.Headers.ContentType?.MediaType;
                    if (response.IsSuccessStatusCode &&
                        ctHeader != null && ctHeader.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                        isSse = true;
                    else
                        body = await response.Content.ReadAsStringAsync(rct);
                }
                else
                {
                    response = await _http.PostAsJsonAsync(url, req, _jsonOpts, rct);
                    body = await response.Content.ReadAsStringAsync(rct);
                }
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var apiErr = ShortApiError(response.StatusCode, body ?? "");
                        // Тело ответа провайдера — единственный источник причины
                        // (например 400 с текстом про tool_call_id). Логируем
                        // усечённо: секретов в теле ответа нет.
                        _logger.Warn($"AskTools ← {(int)response.StatusCode} {response.StatusCode}: " +
                            $"{Truncate(body ?? "", 400)} [запрос: {DescribeToolLoop(messages)}]", "Ai");
                        if (hasToolImages && _imageCapabilities != null)
                        {
                            var (ecode, etype, emsg) = ImageCapabilityService.ParseApiError(body);
                            var verdict = ImageApiErrorClassifier.Classify((int)response.StatusCode, ecode, etype, emsg);
                            _imageCapabilities.RecordApiResult(CurrentImageKey(), verdict, emsg);
                        }
                        if (IsRetryable(response.StatusCode) && attempt < _config.MaxRetries)
                        {
                            _logger.Info($"AskTools retry {attempt + 1}/{_config.MaxRetries} после {(int)response.StatusCode}", "Ai");
                            await Backoff(attempt, ct);
                            continue;
                        }
                        RaiseExchange(isRequest: false, apiErr, isError: true);
                        return ToolAskResult.Failed(new McpError("HTTP_" + (int)response.StatusCode, apiErr));
                    }

                    ToolAskResult result;
                    if (isSse) result = await ReadToolSseStreamAsync(response, rct);
                    else result = ParseToolResponse(body ?? "");
                    if (result.Error == null)
                    {
                        RaiseExchange(isRequest: false,
                            result.AssistantContent ?? $"[tool_calls: {result.ToolCalls.Count}]",
                            totalTokens: result.TotalTokens);
                        if (hasToolImages)
                            _imageCapabilities?.RecordApiResult(CurrentImageKey(), ImageApiVerdict.Accepted, "accepted-request");
                    }
                    return result;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ToolAskResult.Failed(new McpError("CANCELLED", "Запрос отменён"));
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                if (attempt < _config.MaxRetries) continue;
                return ToolAskResult.Failed(new McpError("TIMEOUT", "Таймаут ответа LLM"));
            }
            catch (HttpRequestException ex) when (attempt < _config.MaxRetries)
            {
                _logger.Warn($"AskTools transport: {ex.Message} — retry {attempt + 1}", "Ai");
                await Backoff(attempt, ct);
            }
            catch (Exception ex)
            {
                _logger.Error("AskTools failed", ex, "Ai");
                return ToolAskResult.Failed(new McpError("INTERNAL", ex.Message));
            }
        }
        return ToolAskResult.Failed(new McpError("HTTP", "Исчерпаны повторы запроса"));
    }

    /// <summary>Шов IMcpToolLlm → боевой AskWithToolsAsync (stream выключен: tool-loop
    /// читает финальные tool_calls, потоковая дельта ему не нужна).</summary>
    Task<ToolAskResult> IMcpToolLlm.AskAsync(
        IReadOnlyList<ToolLoopMessage> messages,
        IReadOnlyList<ToolSpecDto> tools,
        int maxTokens, CancellationToken ct)
        => AskWithToolsAsync(messages, tools, maxTokens, ct, stream: false);

    /// <summary>Последний текст пользователя — для сессионного LLM-лога.</summary>
    private static string LastUserText(IReadOnlyList<ToolLoopMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == "user" && messages[i].Content != null)
                return messages[i].Content!;
        return messages.Count > 0 ? $"[{messages[^1].Role}]" : "";
    }

    /// <summary>
    /// Компактная структура сообщений tool-цикла для диагностики отказа провайдера:
    /// роли, id вызовов и длины содержимого. Содержимое сообщений не выводится —
    /// нужен только вид структуры (например «tool без tool_call_id»).
    /// </summary>
    private static string DescribeToolLoop(IReadOnlyList<ToolLoopMessage> messages)
    {
        var parts = new List<string>(messages.Count);
        foreach (var m in messages)
        {
            var ids = m.ToolCalls is { Count: > 0 }
                ? "[" + string.Join(",", m.ToolCalls.Select(c => string.IsNullOrEmpty(c.Id) ? "<empty>" : c.Id)) + "]"
                : "";
            parts.Add($"{m.Role}{ids}({m.Content?.Length ?? 0})");
        }
        return string.Join(" ", parts);
    }

    /// <summary>Сериализация сообщений tool-цикла в формат OpenAI (assistant tool_calls / tool results).</summary>
    private List<object> SerializeToolLoopMessages(IReadOnlyList<ToolLoopMessage> messages)
    {
        var list = new List<object>(messages.Count);
        foreach (var m in messages)
        {
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                var calls = new List<Dictionary<string, object?>>(m.ToolCalls.Count);
                for (var i = 0; i < m.ToolCalls.Count; i++)
                {
                    var c = m.ToolCalls[i];
                    calls.Add(new Dictionary<string, object?>
                    {
                        ["id"] = string.IsNullOrEmpty(c.Id) ? $"call_{i}" : c.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = c.Name,
                            ["arguments"] = c.ArgsJson ?? "{}",
                        },
                    });
                }
                list.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = m.Content,
                    ["tool_calls"] = calls,
                });
            }
            else if (m.Role == "tool")
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = m.ToolCallId ?? "",
                    ["content"] = m.Content ?? "",
                });
            }
            else if (m.Role == "user" && m.ImagePayloads is { Count: > 0 })
            {
                // Мультимодальный user: text-часть + image_url-части (ТЗ §7).
                list.Add(new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = ChatImageTransport.BuildUserContent(m.Content, m.ImagePayloads),
                });
            }
            else
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content ?? "",
                });
            }
        }
        return list;
    }

    /// <summary>Буферный парсинг ответа с tool_calls.</summary>
    private ToolAskResult ParseToolResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = UnwrapDataEnvelope(doc.RootElement);
            var result = new ToolAskResult();
            if (TryGetByPath(root, "choices[0].message") is JsonElement msg)
            {
                result.AssistantContent = MessageContentText(msg);
                if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    foreach (var c in tcs.EnumerateArray())
                        if (TryParseToolCall(c, -1) is { } tc) result.ToolCalls.Add(tc);
            }
            result.FinishReason = TryGetByPath(root, "choices[0].finish_reason")?.GetString();
            if (TryGetByPath(root, Profile.UsagePath) is JsonElement u && u.ValueKind == JsonValueKind.Object)
            {
                result.UsageReported = true;
                if (u.TryGetProperty("prompt_tokens", out var pt)) result.PromptTokens = pt.GetInt32();
                else if (u.TryGetProperty("input_tokens", out var it)) result.PromptTokens = pt.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var ct2)) result.CompletionTokens = ct2.GetInt32();
                if (u.TryGetProperty("output_tokens", out var ot)) result.CompletionTokens = ot.GetInt32();
                if (u.TryGetProperty("total_tokens", out var tt)) result.TotalTokens = tt.GetInt32();
            }
            else result.UsageReported = false;
            return result;
        }
        catch (JsonException ex)
        {
            return ToolAskResult.Failed(new McpError("PARSE", $"Невалидный JSON ответа: {ex.Message}"));
        }
    }

    /// <summary>content может быть строкой или массивом частей {type:"text"}.</summary>
    private static string? MessageContentText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var c)) return null;
        if (c.ValueKind == JsonValueKind.String) return c.GetString();
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in c.EnumerateArray())
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            return sb.Length > 0 ? sb.ToString() : null;
        }
        return null;
    }

    /// <summary>Один tool_call-объект (id + function.name + function.arguments как JSON-строка).</summary>
    private static ToolCallRequest? TryParseToolCall(JsonElement el, int fallbackIndex)
    {
        if (!el.TryGetProperty("function", out var fn)) return null;
        var name = fn.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(name)) return null;
        string argsJson = "{}";
        if (fn.TryGetProperty("arguments", out var a))
            argsJson = a.ValueKind == JsonValueKind.String ? (a.GetString() ?? "{}") : a.GetRawText();
        var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()! : $"call_{fallbackIndex}";
        return new ToolCallRequest(id, name!, argsJson);
    }

    /// <summary>
    /// SSE-парсинг tool-ответа: content-дельты склеиваются, tool_calls собираются по
    /// полю index (дельты id/name/arguments приходят кусками в разных чанках).
    /// </summary>
    private async Task<ToolAskResult> ReadToolSseStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var content = new StringBuilder();
        // index вызова → аккумулятор (id может прийти только в первом чанке).
        var acc = new SortedDictionary<int, (string Id, StringBuilder Name, StringBuilder Args)>();
        string? finishReason = null;
        int promptTokens = 0, completionTokens = 0, totalTokens = 0;
        var done = false;

        var deltaPath = string.IsNullOrEmpty(Profile.DeltaPath) ? "choices[0].delta.content" : Profile.DeltaPath;
        var deltaPrefix = deltaPath[..(deltaPath.LastIndexOf('.') + 1)]; // "choices[0].delta."

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") { done = true; break; }
            if (data.Length == 0) continue;
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(data);
                root = UnwrapDataEnvelope(doc.RootElement).Clone();
            }
            catch (JsonException) { continue; }

            if (TryGetByPath(root, deltaPath) is JsonElement d && d.ValueKind == JsonValueKind.String)
                content.Append(d.GetString());

            // Дельты tool_calls внутри choices[0].delta.tool_calls[].
            if (TryGetByPath(root, deltaPrefix + "tool_calls") is JsonElement tcs &&
                tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in tcs.EnumerateArray())
                {
                    var index = c.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number
                        ? ix.GetInt32() : acc.Count;
                    if (!acc.TryGetValue(index, out var a))
                        a = ($"call_{index}", new StringBuilder(), new StringBuilder());
                    if (c.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        a.Id = id.GetString()!;
                    if (c.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            a.Name.Append(n.GetString());
                        if (fn.TryGetProperty("arguments", out var args))
                            a.Args.Append(args.ValueKind == JsonValueKind.String ? args.GetString() : args.GetRawText());
                    }
                    acc[index] = a;
                }
            }
            // Некоторые шлюзы шлют финальный message целиком одним чанком.
            if (acc.Count == 0 && TryGetByPath(root, "choices[0].message.tool_calls") is JsonElement mtc &&
                mtc.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var c in mtc.EnumerateArray())
                    if (TryParseToolCall(c, i) is { } tc)
                    { acc[i] = (tc.Id, new StringBuilder(tc.Name), new StringBuilder(tc.ArgsJson)); i++; }
            }

            if (TryGetByPath(root, "choices[0].finish_reason") is JsonElement fr &&
                fr.ValueKind == JsonValueKind.String)
                finishReason = fr.GetString();
            if (TryGetByPath(root, Profile.UsagePath) is JsonElement u && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                else if (u.TryGetProperty("input_tokens", out var it)) promptTokens = it.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var ct2)) completionTokens = ct2.GetInt32();
                else if (u.TryGetProperty("output_tokens", out var ot)) completionTokens = ot.GetInt32();
                if (u.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
            }
        }

        var result = new ToolAskResult
        {
            AssistantContent = content.Length > 0 ? content.ToString() : null,
            FinishReason = finishReason,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            UsageReported = promptTokens > 0 || completionTokens > 0 || totalTokens > 0,
        };
        foreach (var kv in acc)
            result.ToolCalls.Add(new ToolCallRequest(
                kv.Value.Id, kv.Value.Name.ToString(),
                kv.Value.Args.Length > 0 ? kv.Value.Args.ToString() : "{}"));
        if (!done && result.ToolCalls.Count == 0 && result.AssistantContent == null)
            return ToolAskResult.Failed(new McpError("STREAM_ABORTED", "Поток ответа оборвался"));
        return result;
    }

}

/// <summary>
/// Одна запись сессионного LLM-лога (вкладка Настройки → Диагностика): запрос ушёл
/// (IsRequest=true, Text = последнее user-сообщение) или пришёл ответ/ошибка
/// (IsRequest=false, Text = ответ либо текст ошибки). Text уже обрезан до ~200 символов.
/// </summary>
public sealed class LlmExchangeEventArgs : EventArgs
{
    public DateTime Timestamp { get; init; }
    public bool IsRequest { get; init; }
    public string Model { get; init; } = "";
    public string Text { get; init; } = "";
    public double ElapsedSeconds { get; init; }
    public int TotalTokens { get; init; }
    public bool IsError { get; init; }
}

// ── ДТО для исходящего MCP tool-loop (AskWithToolsAsync) ─────────────────────

/// <summary>Кодированная ошибка MCP-контура (не HTTP-статус): код машиночитаемый.</summary>
public sealed record McpError(string Code, string Message)
{
    public static readonly McpError LlmPaused = new("LLM_PAUSED", "LLM на паузе (идёт ввод ответа/микрофон занят)");
    public static readonly McpError ApprovalDenied = new("APPROVAL_DENIED", "Действие отклонено пользователем");
}

/// <summary>Запрос вызова инструмента, который прислала модель.</summary>
public sealed record ToolCallRequest(string Id, string Name, string ArgsJson);

/// <summary>Спецификация инструмента для запроса tools[] (адаптерное имя + schema).</summary>
public sealed record ToolSpecDto(string Name, string? Description, System.Text.Json.JsonElement ParametersSchema);

/// <summary>
/// Сообщение tool-цикла: role system|user|assistant|tool. У assistant может быть
/// список ToolCalls (content тогда может быть null); у tool — ToolCallId вызова.
/// </summary>
public sealed record ToolLoopMessage(string Role, string? Content)
{
    public string? ToolCallId { get; init; }
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; init; }
    /// <summary>Картинки сообщения (ТЗ §7): сериализуются в content-массив
    /// openai-chat вместе с текстом; только у user-сообщений.</summary>
    public IReadOnlyList<ChatImagePayload>? ImagePayloads { get; init; }

    public static ToolLoopMessage System(string content) => new("system", content);
    public static ToolLoopMessage User(string content) => new("user", content);
    /// <summary>User-сообщение с картинками; текст может быть пустым.</summary>
    public static ToolLoopMessage UserWithImages(string? content, IReadOnlyList<ChatImagePayload> images)
        => new("user", content) { ImagePayloads = images };
    public static ToolLoopMessage Assistant(string? content, IReadOnlyList<ToolCallRequest>? calls = null)
        => new("assistant", content) { ToolCalls = calls };
    public static ToolLoopMessage ToolResult(string toolCallId, string content)
        => new("tool", content) { ToolCallId = toolCallId };
}

/// <summary>Ответ LLM на запрос с инструментами: текст ИЛИ список вызовов (или и то и то).</summary>
public sealed class ToolAskResult
{
    public string? AssistantContent { get; set; }
    public List<ToolCallRequest> ToolCalls { get; } = new();
    public string? FinishReason { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public McpError? Error { get; set; }
    /// <summary>Провайдер прислал usage по этому шагу цикла. false → токены
    /// «не сообщены»: MCP-ответ считается оценочным (Estimated).</summary>
    public bool UsageReported { get; set; } = true;

    public static ToolAskResult Failed(McpError error) => new() { Error = error };
}

