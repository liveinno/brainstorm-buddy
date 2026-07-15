using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Ai;

public class OpenAiClient : IApiClient
{
    private readonly ApiConfig _config;
    private readonly HttpClient _http;
    private readonly LoggingService _logger;
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
    private DateTime _lastAskUtc = DateTime.MinValue;

    // Последнее сообщённое состояние здоровья (null = ещё не знаем).
    // Событие шлём только при переходе, чтобы не спамить UI на каждый чанк.
    private bool? _sttHealthy;
    private bool? _llmHealthy;
    private string _sttMsg = "";
    private string _llmMsg = "";

    public event EventHandler<ApiHealthEventArgs>? HealthChanged;

    private void ReportHealth(ApiComponent component, bool healthy, string message)
    {
        ref bool? last = ref (component == ApiComponent.Stt ? ref _sttHealthy : ref _llmHealthy);
        ref string lastMsg = ref (component == ApiComponent.Stt ? ref _sttMsg : ref _llmMsg);
        // Шлём событие при смене статуса ИЛИ текста ошибки (иначе новая 401/429 не покажется).
        if (last == healthy && lastMsg == message) return;
        last = healthy;
        lastMsg = message;
        _logger.Info($"Health[{component}] → {(healthy ? "OK" : "DOWN")}: {message}", "Ai");
        HealthChanged?.Invoke(this, new ApiHealthEventArgs(component, healthy, message));
    }

    // Актуализирует Authorization-заголовок из конфига перед каждым запросом:
    // ключ мог измениться в настройках уже после создания клиента.
    private void ApplyAuth()
    {
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(_config.ApiKey) ? null : new AuthenticationHeaderValue("Bearer", _config.ApiKey);
    }

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
        402 => "Лимит бесплатного тарифа OpenRouter исчерпан — пополните счёт или переключитесь на локальную модель (Настройки → API).\n" + apiErr,
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
        _http = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds) };

        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            _logger.Debug($"OpenAiClient: BaseUrl={config.BaseUrl}, Chat={config.ChatModel}, Stt={config.SttModel}, Key=***{(config.ApiKey.Length > 4 ? config.ApiKey[^4..] : "<short>")}", "Ai");
        }
        else
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
                    ReportHealth(ApiComponent.Stt, false, $"STT-сервер ответил ошибкой {(int)response.StatusCode}");
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
                if (IsConnectivityError(ex, attempt))
                {
                    ReportHealth(ApiComponent.Stt, false, "STT-сервер не отвечает (таймаут)");
                    return null;
                }
                _logger.Warn($"STT timeout attempt {attempt + 1}: {ex.Message}", "Ai");
                if (attempt < _config.MaxRetries)
                {
                    await Backoff(attempt, token);
                    continue;
                }
                ReportHealth(ApiComponent.Stt, false, "STT-сервер не отвечает (таймаут)");
                return null;
            }
            catch (HttpRequestException ex)
            {
                if (IsConnectivityError(ex, attempt))
                {
                    ReportHealth(ApiComponent.Stt, false, "STT-сервер недоступен (нет соединения)");
                    return null;
                }
                _logger.Warn($"STT HTTP error attempt {attempt + 1}: {ex.Message}", "Ai");
                if (attempt < _config.MaxRetries)
                {
                    await Backoff(attempt, token);
                    continue;
                }
                ReportHealth(ApiComponent.Stt, false, "STT-сервер недоступен (нет соединения)");
                return null;
            }
        }
        return null;
    }

    public async Task<AskResult> AskAsync(string userText, string systemPrompt, int maxTokens, List<ChatMessage> history, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) return AskResult.Empty;
        ApplyAuth();

        var url = $"{_config.BaseUrl.TrimEnd('/')}/chat/completions";
        _logger.Debug($"Ask → POST {url} (text={userText.Length} chars, history={history.Count}, maxTokens={maxTokens})", "Ai");

        if (_config.RateLimitSeconds > 0)
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

        // «→LLM» — один раз на вызов (авто-ретраи 402/пустого ответа идут рекурсией и честно
        // дадут свою строку — в логе видно, что запрос ушёл повторно с другим лимитом).
        RaiseExchange(isRequest: true, userText);

        for (int attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
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
                var modelLower = _config.ChatModel.ToLowerInvariant();
                var isReasoning = IsReasoningModel(modelLower);
                // Whisper-промпт: temperature 0.1 для строгости (без галлюцинаций и вопросов)
                var isWhisperPrompt = systemPrompt.Contains("шепот", StringComparison.OrdinalIgnoreCase) ||
                                      systemPrompt.Contains("whisper", StringComparison.OrdinalIgnoreCase);
                var temperature = isWhisperPrompt ? 0.1 : 0.3;
                var req = new Dictionary<string, object>
                {
                    { "model", _config.ChatModel },
                    { "messages", messages },
                    { "temperature", temperature },
                    { "max_tokens", maxTokens }
                };
                if (isReasoning)
                {
                    req["reasoning"] = new { effort = "low" };
                }

                var sw = Stopwatch.StartNew();
                using var response = await _http.PostAsJsonAsync(url, req, _jsonOpts, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                sw.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"Ask ← {(int)response.StatusCode} {response.StatusCode} in {sw.ElapsedMilliseconds}ms: {Truncate(body, 300)}", "Ai");
                    var apiErr = ShortApiError(response.StatusCode, body);
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        _logger.Error("Auth error: invalid API key (401)", null, "Ai");
                        ReportHealth(ApiComponent.Llm, false, apiErr);
                        RaiseExchange(isRequest: false, apiErr, isError: true);
                        return AskResult.Failed(apiErr);
                    }
                    if ((int)response.StatusCode == 402 && maxTokens > 50)
                    {
                        _logger.Warn($"Payment required (402): reducing max_tokens from {maxTokens} to 50 and retrying", "Ai");
                        return await AskAsync(userText, systemPrompt, 50, history, ct);
                    }
                    if ((int)response.StatusCode == 402 && maxTokens <= 50)
                    {
                        _logger.Error("Payment required (402): no credits left. Top up at openrouter.ai/settings/credits", null, "Ai");
                        // №5 бэклога: человеческое объяснение первой строкой, сырой текст — второй.
                        var friendly402 = HumanizeApiError(response.StatusCode, apiErr);
                        ReportHealth(ApiComponent.Llm, false, friendly402);
                        RaiseExchange(isRequest: false, apiErr, isError: true);
                        return AskResult.Failed(friendly402);
                    }
                    if (body.Contains("context_length_exceeded") && history.Count > 2)
                    {
                        _logger.Warn($"Context length exceeded (history={history.Count}), trimming to 2", "Ai");
                        history.RemoveRange(0, history.Count - 2);
                        return await AskAsync(userText, systemPrompt, maxTokens, history, ct);
                    }
                    if (IsRetryable(response.StatusCode) && attempt < _config.MaxRetries)
                    {
                        _logger.Info($"Ask retry {attempt + 1}/{_config.MaxRetries} after backoff", "Ai");
                        await Backoff(attempt, ct);
                        continue;
                    }
                    // Сюда доходит и 429 после исчерпанных ретраев — тоже даём человеческий текст.
                    var friendly = HumanizeApiError(response.StatusCode, apiErr);
                    ReportHealth(ApiComponent.Llm, false, friendly);
                    RaiseExchange(isRequest: false, apiErr, isError: true);
                    return AskResult.Failed(friendly);
                }

                // Дошёл ответ 200 — LLM-сервер жив.
                ReportHealth(ApiComponent.Llm, true, "LLM-сервер отвечает");
                _logger.Debug($"Ask ← 200 in {sw.ElapsedMilliseconds}ms, body={Truncate(body, 300)}", "Ai");

                using var doc = JsonDocument.Parse(body);
                var content_str = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                int promptTokens = 0, completionTokens = 0, totalTokens = 0;
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct2)) completionTokens = ct2.GetInt32();
                    if (usage.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
                }

                if (string.IsNullOrWhiteSpace(content_str))
                {
                    // Reasoning-модель сожгла весь лимит на размышления (200, usage есть, content
                    // пуст — живой тест: tencent/hy3 при лимите 180). Один авто-ретрай с большим
                    // лимитом вместо показа юзеру «пустой ответ» при исправной модели.
                    if (maxTokens < 1024)
                    {
                        var boosted = Math.Max(1024, maxTokens * 4);
                        _logger.Warn($"Ask: 200, но content пуст (completion={completionTokens}/{maxTokens}) — авто-ретрай с max_tokens={boosted}", "Ai");
                        return await AskAsync(userText, systemPrompt, boosted, history, ct);
                    }
                    RaiseExchange(isRequest: false, $"пустой ответ (200, completion={completionTokens}/{maxTokens} — reasoning-модель съела лимит)", sw.Elapsed.TotalSeconds, totalTokens, isError: true);
                    return new AskResult(null, promptTokens, completionTokens, totalTokens);
                }
                var result = content_str.Trim();

                var finishReason = doc.RootElement
                    .GetProperty("choices")[0]
                    .TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

                if (finishReason == "length")
                {
                    result = result.TrimEnd('.', ' ', ',', ';') + "…";
                    _logger.Warn($"Answer was truncated by max_tokens (used {completionTokens}/{maxTokens}), appended '…'", "Ai");
                }

                _logger.Info($"Ask tokens: prompt={promptTokens}, completion={completionTokens}, total={totalTokens}", "Ai");
                _lastAskUtc = DateTime.UtcNow;
                RaiseExchange(isRequest: false, result, sw.Elapsed.TotalSeconds, totalTokens);
                return new AskResult(result, promptTokens, completionTokens, totalTokens);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.Info("Ask cancelled by parent token", "Ai");
                throw;
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
            {
                if (IsConnectivityError(ex, attempt))
                {
                    ReportHealth(ApiComponent.Llm, false, "LLM-сервер недоступен (нет соединения)");
                    RaiseExchange(isRequest: false, "LLM-сервер недоступен (нет соединения)", isError: true);
                    return AskResult.Failed("LLM-сервер недоступен (нет соединения)");
                }
                _logger.Warn($"Ask error attempt {attempt + 1}: {ex.Message}", "Ai");
                if (attempt < _config.MaxRetries)
                {
                    await Backoff(attempt, ct);
                    continue;
                }
                ReportHealth(ApiComponent.Llm, false, "LLM-сервер недоступен (нет соединения)");
                RaiseExchange(isRequest: false, "LLM-сервер недоступен (нет соединения)", isError: true);
                return AskResult.Empty;
            }
        }
        return AskResult.Empty;
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken ct = default)
    {
        var url = $"{_config.BaseUrl.TrimEnd('/')}/models";
        _logger.Debug($"CheckConnection → GET {url}", "Ai");
        try
        {
            var sw = Stopwatch.StartNew();
            using var response = await _http.GetAsync(url, ct);
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
        modelLower.Contains("deepseek-r") || modelLower.Contains("qwq") || modelLower.Contains("think");

    public async Task<(bool ok, string detail)> CheckLlmConnectionAsync(CancellationToken ct = default)
        => await CheckLlmConnectionAsync(0, null, ct);

    /// <summary>
    /// Проверка подключения С БОЕВЫМИ ПАРАМЕТРАМИ. Раньше пинг шёл с max_tokens=1 без системного
    /// промпта и судил по HTTP 200 — reasoning-модель «отвечала» в настройках (200), а в бою
    /// возвращала пустой content (лимит съеден размышлениями): «ОК» в настройках, ошибка в окне.
    /// Теперь: тот же max_tokens и системный промпт, что у реальных запросов, и проверка,
    /// что content непустой.
    /// </summary>
    public async Task<(bool ok, string detail)> CheckLlmConnectionAsync(int realMaxTokens, string? systemPrompt, CancellationToken ct = default)
    {
        var baseUrl = _config.BaseUrl.TrimEnd('/');

        // 1) Ключ/URL — GET /models.
        try
        {
            ApplyAuth();
            using var r1 = await _http.GetAsync($"{baseUrl}/models", ct);
            if (!r1.IsSuccessStatusCode)
            {
                var b1 = await r1.Content.ReadAsStringAsync(ct);
                _logger.Warn($"CheckLlm /models ← {(int)r1.StatusCode}: {Truncate(b1, 200)}", "Ai");
                return (false, $"Ключ/URL: {ShortApiError(r1.StatusCode, b1)}");
            }
        }
        catch (Exception ex) { _logger.Warn($"CheckLlm /models failed: {ex.Message}", "Ai"); return (false, $"Нет связи с {baseUrl}: {ex.Message}"); }

        // 2) РЕАЛЬНЫЙ чат-пинг выбранной моделью — с боевыми max_tokens/промптом.
        if (string.IsNullOrWhiteSpace(_config.ChatModel))
            return (false, "Модель не указана — впишите модель провайдера в поле «Модель».");
        try
        {
            ApplyAuth();
            var maxTokens = realMaxTokens > 0 ? realMaxTokens : 60;
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = "Ответь ровно одним словом: готов" });
            var req = new Dictionary<string, object>
            {
                { "model", _config.ChatModel },
                { "messages", messages },
                { "max_tokens", maxTokens }
            };
            if (IsReasoningModel(_config.ChatModel.ToLowerInvariant()))
                req["reasoning"] = new { effort = "low" };
            using var r2 = await _http.PostAsJsonAsync($"{baseUrl}/chat/completions", req, _jsonOpts, ct);
            var b2 = await r2.Content.ReadAsStringAsync(ct);
            _logger.Info($"CheckLlm chat[{_config.ChatModel}] ← {(int)r2.StatusCode} (maxTokens={maxTokens}, sysPrompt={systemPrompt?.Length ?? 0} симв.)", "Ai");
            if (r2.IsSuccessStatusCode)
            {
                // HTTP 200 ещё не успех: проверяем НЕПУСТОЙ content — ровно как в боевом запросе.
                string content = "";
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(b2);
                    content = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
                                 .GetProperty("content").GetString() ?? "";
                }
                catch { /* нестандартный ответ провайдера — считаем пустым */ }
                if (string.IsNullOrWhiteSpace(content))
                {
                    var errEmpty = $"Модель «{_config.ChatModel}» вернула ПУСТОЙ ответ при лимите {maxTokens} токенов — " +
                                   "reasoning-модель «съедает» лимит на размышления. Увеличьте «Максимум токенов ответа» или смените модель.";
                    _logger.Warn($"CheckLlm: 200, но content пуст (модель {_config.ChatModel}, maxTokens={maxTokens})", "Ai");
                    ReportHealth(ApiComponent.Llm, false, errEmpty);
                    return (false, errEmpty);
                }
                // Настоящий успех = LLM жив → ГАСИМ возможную залипшую плашку «ошибка LLM»
                // (она могла остаться со старта, когда конфиг был другой/без ключа).
                ReportHealth(ApiComponent.Llm, true, "LLM-сервер отвечает");
                return (true, $"OK — модель {_config.ChatModel} отвечает");
            }
            _logger.Warn($"CheckLlm chat ← {(int)r2.StatusCode}: {Truncate(b2, 400)}", "Ai");
            // Тот же человеческий текст для 402/429, что и в боевых запросах (№5 бэклога).
            var err = $"Модель «{_config.ChatModel}»: {HumanizeApiError(r2.StatusCode, ShortApiError(r2.StatusCode, b2))}";
            ReportHealth(ApiComponent.Llm, false, err); // реальная ошибка — в плашку тоже
            return (false, err);
        }
        catch (Exception ex) { _logger.Warn($"CheckLlm chat failed: {ex.Message}", "Ai"); return (false, ex.Message); }
    }

    private static bool IsRetryable(HttpStatusCode code)
    {
        var n = (int)code;
        return n == 429 || (n >= 500 && n < 600);
    }

    private bool IsConnectivityError(Exception ex, int attempt)
    {
        if (attempt > 0) return false;
        var isConnectivity = ex is TaskCanceledException ||
                             (ex is HttpRequestException hre &&
                              (hre.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                               hre.Message.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
                               hre.Message.Contains("resolve", StringComparison.OrdinalIgnoreCase) ||
                               hre.Message.Contains("Name or service", StringComparison.OrdinalIgnoreCase) ||
                               hre.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
                               hre.InnerException?.Message?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true));
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

