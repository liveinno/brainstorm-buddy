using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Ai;

public class OpenAiClient : IApiClient
{
    private readonly ApiConfig _config;
    private readonly HttpClient _http;
    private readonly LoggingService _logger;
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
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
    private void ApplyHeaders()
        => LlmRequestHeaders.Apply(_http, _config, _sessionId);

    // STT-запросы идут к отдельному сервису — только Bearer, без LLM-профильных заголовков.
    private void ApplyAuth()
        => _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(_config.ApiKey) ? null : new AuthenticationHeaderValue("Bearer", _config.ApiKey);

    // Профиль провайдера по ProviderId/CustomProviders, иначе синтетический из BaseUrl.
    private LlmProviderProfile Profile => LlmProviderRegistry.Resolve(_config);

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

    public OpenAiClient(ApiConfig config, LoggingService logger, HttpMessageHandler? handler = null)
    {
        _config = config;
        _logger = logger;
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

    public async Task<AskResult> AskAsync(string userText, string systemPrompt, int maxTokens, List<ChatMessage> history, CancellationToken ct = default, Action<string>? onDelta = null, bool background = false, TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(userText)) return AskResult.Empty;
        ApplyHeaders();

        var url = EndpointUrl(Profile.ChatPath);
        _logger.Debug($"Ask → POST {url} (text={userText.Length} chars, history={history.Count}, maxTokens={maxTokens})", "Ai");

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
        RaiseExchange(isRequest: true, userText);

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
                var messages = new List<object>
                {
                    new { role = "system", content = systemPrompt }
                };
                foreach (var m in history)
                    messages.Add(new { role = m.Role, content = m.Content });
                messages.Add(new { role = "user", content = userText });

                // Для reasoning-моделей (gpt-oss, o1, etc.) минимизируем reasoning,
                // чтобы получить быстрый прямой ответ. По умолчанию для не-reasoning — отключаем.
                var chatModel = NormalizeModelId(_config.ChatModel);
                var modelLower = chatModel.ToLowerInvariant();
                var isReasoning = IsReasoningModel(modelLower);
                // Whisper-промпт: temperature 0.1 для строгости (без галлюцинаций и вопросов)
                var isWhisperPrompt = systemPrompt.Contains("шепот", StringComparison.OrdinalIgnoreCase) ||
                                      systemPrompt.Contains("whisper", StringComparison.OrdinalIgnoreCase);
                var temperature = isWhisperPrompt ? 0.1 : 0.3;
                var req = new Dictionary<string, object>
                {
                    { "model", chatModel },
                    { "messages", messages },
                    { "temperature", temperature },
                    { "max_tokens", maxTokens }
                };
                // Effort: глобальный override (Api.ReasoningEffortOverride) приоритетнее
                // профиля провайдера; итог пуст — поле reasoning не шлём вообще.
                var reasoningEffort = _config.ReasoningEffortOverride ?? Profile.ReasoningEffort;
                if (isReasoning && !string.IsNullOrEmpty(reasoningEffort))
                {
                    req["reasoning"] = new { effort = reasoningEffort };
                }

                // Стриминг — безопасный upgrade: только когда вызывающий дал onDelta
                // и протокол openai-chat. Прокси, проигнорировавший stream:true и
                // вернувший обычный JSON, уходит в буферный парсинг ниже без ошибки.
                // _streamDisabled: шлюз уже доказал, что портит SSE — дальше только буфер.
                var streaming = onDelta != null && Profile.Protocol == "openai-chat" && !_streamDisabled;
                if (streaming)
                {
                    req["stream"] = true;
                    req["stream_options"] = new { include_usage = true };
                }

                var sw = Stopwatch.StartNew();
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
                if (!isSse) sw.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"Ask ← {(int)response.StatusCode} {response.StatusCode} in {sw.ElapsedMilliseconds}ms: {Truncate(body ?? "", 300)}", "Ai");
                    var apiErr = ShortApiError(response.StatusCode, body ?? "");
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
                        return await AskAsync(userText, systemPrompt, 50, history, ct, onDelta, background, requestTimeout);
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
                        return await AskAsync(userText, systemPrompt, maxTokens, history, ct, onDelta, background, requestTimeout);
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

                string? content_str = null;
                int promptTokens = 0, completionTokens = 0, totalTokens = 0;
                string? finishReason = null;
                var sawReasoningOnly = false;
                string? reasoningText = null;

                if (isSse)
                {
                    // SSE-стрим: читаем дельты построчно, onDelta — на потоке
                    // вызывающего. Финальный content = склейка дельт → дальше те же
                    // ветки (пустой/stop=молчание/length), что у буферного ответа.
                    SseResult sse;
                    try
                    {
                        sse = await ReadSseStreamAsync(response, onDelta!, rct);
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
                    // глючного шлюза: completion > maxTokens*1.25 — кап проигнорирован
                    // (в SSE opencode.ai/zen жгло 820-883 токена при лимите 180,
                    // латентность ×3); пустой content без finish_reason — молчание не
                    // распознаётся и уходит в авто-ретрай с max_tokens=1024. Стрим
                    // выключаем до конца сессии и переиздаём тот же запрос буфером —
                    // один раз: рекурсивный вызов уже идёт без stream:true (флаг
                    // поднят), на стрим-путь не вернётся.
                    var capIgnored = sse.CompletionTokens > maxTokens * 1.25;
                    var finishLost = string.IsNullOrWhiteSpace(sse.Content) && sse.FinishReason == null;
                    if (capIgnored || finishLost)
                    {
                        var why = capIgnored
                            ? $"provider ignored max_tokens in SSE (completion={sse.CompletionTokens}/{maxTokens})"
                            : "SSE ответ без finish_reason при пустом content";
                        _logger.Warn($"Ask: {why} — reissue без стрима", "Ai");
                        _streamDisabled = true;
                        _logger.Info($"stream disabled for session ({why})", "Ai");
                        return await AskAsync(userText, systemPrompt, maxTokens, history, ct, onDelta);
                    }
                }
                else
                {
                _logger.Debug($"Ask ← 200 in {sw.ElapsedMilliseconds}ms, body={Truncate(body ?? "", 300)}", "Ai");

                // Защищённый парсинг (A6): кривое 200-тело провайдера (не JSON, без choices)
                // не должно улетать JsonException'ом наружу — считаем пустым ответом и
                // уходим в авто-ретрай с большим лимитом по общей ветке ниже.
                // Модель прислала ТОЛЬКО рассуждения (CoT) без ответа. Текст сохраняем,
                // но НЕ показываем юзеру сразу (раньше сюда проваливался CoT
                // deepseek-v4.1-flash и шёл на экран как «ответ»). Фолбэк на него —
                // только для allowlist-моделей (A6: space-bunny-free держит ответ в
                // reasoning_content by design), только после boost-ретрая и только
                // через эвристику «финальный ответ» (2.8.x п.2).

                try
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
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
                {
                    _logger.Warn($"Ask: 200, но тело не распарсилось: {ex.Message}", "Ai");
                }
                }

            // Срезаем протечку DSML-разметки функ-вызовов и CoT-преамбулу ДО проверки
            // пустоты — ответ из одних маркеров/размышлений уходит в ветку «пустой ответ».
            content_str = LlmAnswerSanitizer.Sanitize(content_str, m => _logger.Debug(m, "Ai"));
            if (string.IsNullOrWhiteSpace(content_str))
            {
                var cot = sawReasoningOnly ? ", в ответе только рассуждения" : "";
                // 2.8.x п.2: allowlist-модели (A6: space-bunny-free на OpenCode, Groq
                // gpt-oss-120b — ответ может лежать в reasoning_content/reasoning при
                // пустом content) сначала идут в boost-ретрай, как ветка «reasoning-
                // модель съела лимит»: повторный запрос может вернуть нормальный
                // content. ТОЛЬКО если и после ретрая content пуст — берём reasoning,
                // и лишь когда он проходит эвристику «похоже на финальный ответ»
                // (иначе в reasoning сидит сырой CoT — баг: «мысли» на экране).
                // Эвристика не прошла → дружелюбное молчание, reasoning не показываем.
                // Для НЕ-allowlist reasoning-моделей (deepseek-v* и др.) — как раньше:
                // пустой content → null, их CoT ответом не считаем вообще.
                var reasoningFallback = AnswersInReasoningContent(modelLower) && !string.IsNullOrWhiteSpace(reasoningText);
                if (reasoningFallback && maxTokens < 1024)
                {
                    var boosted = Math.Max(1024, maxTokens * 4);
                    _logger.Warn($"Ask: 200, content пуст (у allowlist-модели есть reasoning{cot}) — boost-ретрай с max_tokens={boosted} до фолбэка на reasoning", "Ai");
                    return await AskAsync(userText, systemPrompt, boosted, history, ct, onDelta);
                }
                if (reasoningFallback)
                {
                    var fb = LlmAnswerSanitizer.Sanitize(reasoningText, m => _logger.Debug(m, "Ai"))?.Trim();
                    if (!string.IsNullOrEmpty(fb) && LooksLikeFinalAnswer(fb))
                    {
                        _logger.Warn($"Ask: content пуст и после ретрая — fallback на reasoning (эвристика «финальный ответ» ок, len={fb.Length})", "Ai");
                        RaiseExchange(isRequest: false, fb, sw.Elapsed.TotalSeconds, totalTokens);
                        return new AskResult(fb, promptTokens, completionTokens, totalTokens) { WasTruncated = finishReason == "length" };
                    }
                    _logger.Warn($"Ask: reasoning отклонён эвристикой «финальный ответ» (len={reasoningText!.Length}) — CoT на экран не выводим, считаем молчанием", "Ai");
                    RaiseExchange(isRequest: false, "(молчание)", sw.Elapsed.TotalSeconds, totalTokens);
                    return new AskResult(null, promptTokens, completionTokens, totalTokens) { IsSilent = true };
                }
                // finish_reason="stop" при пустом content = осознанное молчание
                // (промпт суфлёра сам требует тишины на small talk/шуме). Не
                // ретраим — третий запрос бесполезен и добавляет латентность.
                // Отличается от finish_reason="length", где лимит съеден.
                if (finishReason == "stop")
                {
                    _logger.Info($"Ask: 200, пустой content при finish_reason=stop — модель промолчала (completion={completionTokens}/{maxTokens})", "Ai");
                    RaiseExchange(isRequest: false, "(молчание)", sw.Elapsed.TotalSeconds, totalTokens);
                    return new AskResult(null, promptTokens, completionTokens, totalTokens) { IsSilent = true };
                }
                // Reasoning-модель сожгла весь лимит на размышления (200, usage есть,
                // content пуст — живой тест: tencent/hy3 при лимите 180). Один
                // авто-ретрай с большим лимитом вместо «пустого ответа».
                if (maxTokens < 1024)
                {
                    var boosted = Math.Max(1024, maxTokens * 4);
                    _logger.Warn($"Ask: 200, но content пуст (completion={completionTokens}/{maxTokens}{cot}) — авто-ретрай с max_tokens={boosted}", "Ai");
                    return await AskAsync(userText, systemPrompt, boosted, history, ct, onDelta, background, requestTimeout);
                }
                var emptyMsg = sawReasoningOnly
                    ? $"пустой ответ (200: модель прислала только рассуждения, completion={completionTokens}/{maxTokens})"
                    : $"пустой ответ (200, completion={completionTokens}/{maxTokens} — reasoning-модель съела лимит)";
                RaiseExchange(isRequest: false, emptyMsg, sw.Elapsed.TotalSeconds, totalTokens, isError: true);
                return new AskResult(null, promptTokens, completionTokens, totalTokens);
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
                return new AskResult(null, promptTokens, completionTokens, totalTokens) { IsSilent = true };
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
            return new AskResult(result, promptTokens, completionTokens, totalTokens) { WasTruncated = finishReason == "length" };
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

    // Модели, у которых ФИНАЛЬНЫЙ ответ может жить в reasoning_content/reasoning при
    // пустом content (бэклог A6: space-bunny-free на OpenCode; Groq gpt-oss-120b
    // кладёт ответ в message.reasoning — прод-логи diagnostics_20261005). 2.8.x п.2:
    // для них — boost-ретрай, и ТОЛЬКО потом fallback на reasoning через эвристику
    // LooksLikeFinalAnswer (иначе их CoT — «Okay, the user wants…» — утекает на
    // экран); для прочих reasoning-моделей (deepseek-v* и т.п.) пустой content =
    // null, их CoT ответом не считаем вообще (баг deepseek-v4.1-flash).
    private static bool AnswersInReasoningContent(string modelLower) =>
        modelLower.Contains("space-bunny") || modelLower.Contains("gpt-oss");

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
        var chatModel = NormalizeModelId(_config.ChatModel);
        if (profile.Protocol != "openai-chat" || string.IsNullOrEmpty(profile.ChatPath))
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
    // reasoning_content — только для space-bunny-подобных (как в боевом AskAsync),
    // прочим reasoning-моделям их CoT ответом не считаем.
    private async Task<CheckStepResult> CheckChatStepAsync(LlmProviderProfile profile, string chatModel, string chatUrl,
        int realMaxTokens, string? systemPrompt, CancellationToken ct)
    {
        var maxTokens = realMaxTokens > 0 ? realMaxTokens : 60;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var messages = new List<object>();
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    messages.Add(new { role = "system", content = systemPrompt });
                messages.Add(new { role = "user", content = "Ответь ровно одним словом: готов" });
                var req = new Dictionary<string, object>
                {
                    { "model", chatModel },
                    { "messages", messages },
                    { "max_tokens", maxTokens }
                };
                var checkEffort = _config.ReasoningEffortOverride ?? profile.ReasoningEffort;
                if (IsReasoningModel(chatModel.ToLowerInvariant()) && !string.IsNullOrEmpty(checkEffort))
                    req["reasoning"] = new { effort = checkEffort };

                using var r2 = await _http.PostAsJsonAsync(chatUrl, req, _jsonOpts, ct);
                var b2 = await r2.Content.ReadAsStringAsync(ct);
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
                try
                {
                    using var doc = JsonDocument.Parse(b2);
                    var root = UnwrapDataEnvelope(doc.RootElement);
                    var c = TryGetByPath(root, profile.ContentPath)?.GetString();
                    content = c ?? "";
                    // A6: у space-bunny-free ОТВЕТ может лежать в reasoning_content при
                    // пустом content — считаем валидным, как боевой AskAsync. Прочим
                    // reasoning-моделям reasoning ≠ ответ (иначе CoT deepseek протекает).
                    if (string.IsNullOrWhiteSpace(content) &&
                        AnswersInReasoningContent(chatModel.ToLowerInvariant()) &&
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
                catch { /* нестандартный ответ провайдера — считаем пустым */ }

                if (!string.IsNullOrWhiteSpace(content))
                {
                    // Настоящий успех = LLM жив → ГАСИМ возможную залипшую плашку «ошибка LLM»
                    // (она могла остаться со старта, когда конфиг был другой/без ключа).
                    ReportHealth(ApiComponent.Llm, true, "LLM-сервер отвечает");
                    return new CheckStepResult("Chat", true, $"OK — модель {chatModel} отвечает");
                }

                // Пусто при малом лимите → один ретрай с 1024 (как в боевом AskAsync).
                if (attempt == 0 && maxTokens < 1024)
                {
                    _logger.Warn($"CheckLlm: 200, но content пуст (maxTokens={maxTokens}) — ретрай с 1024", "Ai");
                    maxTokens = 1024;
                    continue;
                }
                var errEmpty = $"Модель «{chatModel}» вернула ПУСТОЙ ответ при лимите {maxTokens} токенов — " +
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
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return data.GetArrayLength();
            if (root.ValueKind == JsonValueKind.Array) return root.GetArrayLength();
        }
        catch { }
        return 0;
    }

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
        string? Reasoning);

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

