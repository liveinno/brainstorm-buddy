using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrainstormBuddy.Ai;

/// <summary>
/// Протокол OpenAI Responses API (POST {BaseUrl}{ChatPath}, обычно /responses):
/// сборка тела запроса, разбор буферного JSON-ответа и разбор SSE-потока.
/// Чистая логика без HTTP-клиента — юнит-тесты гоняют её напрямую
/// (Tests/Ai/OpenAiResponsesProtocolTests.cs).
///
/// Особенность бэкенда подписки ChatGPT/Codex (chatgpt.com/backend-api/codex/responses):
/// он ОТКЛОНЯЕТ переданные клиентом лимиты вывода и sampling-параметры
/// («Unsupported parameter: temperature»), поэтому в теле запроса нет ни
/// max_output_tokens, ни temperature/top_p/stop — только model / instructions /
/// input / stream (+ reasoning.effort, если профиль его просит).
/// </summary>
public static class ResponsesProtocol
{
    /// <summary>Значение LlmProviderProfile.Protocol для этого протокола.</summary>
    public const string Protocol = "openai-responses";

    // ── Запрос ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Тело POST {BaseUrl}{ChatPath} в формате Responses API:
    /// model / instructions / input[] / stream / store (+ reasoning.effort,
    /// + max_output_tokens — если бэкенд лимит принимает).
    /// Sampling-параметры (temperature/top_p/stop) НЕ отправляем — бэкенд
    /// подписки ChatGPT/Codex их отвергает (400 «Unsupported parameter»).
    /// </summary>
    /// <param name="model">ID модели (как в профиле/конфиге).</param>
    /// <param name="instructions">Системный промпт (пусто — поле не отправляем).</param>
    /// <param name="userText">Текущий вопрос пользователя.</param>
    /// <param name="history">История диалога: user → input_text, assistant → output_text.</param>
    /// <param name="reasoningEffort">Effort рассуждений (low/medium/high); пусто — не отправляем.</param>
    /// <param name="stream">true — SSE-поток, false — буферный JSON-ответ.</param>
    /// <param name="maxOutputTokens">Лимит вывода; null/0 — не отправляем (бэкенд его отвергает).</param>
    public static string BuildRequest(string model, string? instructions, string userText,
        IEnumerable<ChatMessage>? history, string? reasoningEffort, bool stream, int? maxOutputTokens = null)
    {
        var input = new JsonArray();
        // История идёт в хронологическом порядке, текущий вопрос — последним:
        // так же, как messages[] в чат-протоколе (контекст совпадает).
        // Изображения в истории протокол Responses не принимает — берём только текст
        // (запрос с картинками в профиле Responses отклоняется до отправки).
        foreach (var m in history ?? Enumerable.Empty<ChatMessage>())
        {
            if (m == null) continue;
            var assistant = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase);
            input.Add(Message(assistant ? "assistant" : "user", m.Content ?? "",
                assistant ? "output_text" : "input_text"));
        }
        input.Add(Message("user", userText ?? "", "input_text"));

        var root = new JsonObject
        {
            ["model"] = model ?? "",
            ["input"] = input,
            ["stream"] = stream,
            // store=false: ответ не должен сохраняться на стороне провайдера
            // (так же, как у клиента подписки ChatGPT/Codex).
            ["store"] = false,
        };
        if (!string.IsNullOrWhiteSpace(instructions))
            root["instructions"] = instructions;
        // Effort — из профиля/override; пустое значение = поле не отправляем вовсе.
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            root["reasoning"] = new JsonObject { ["effort"] = reasoningEffort };
        // Лимит вывода — обычные /responses-эндпоинты его принимают, а бэкенд
        // подписки ChatGPT/Codex отвергает (см. AcceptsOutputCaps).
        if (maxOutputTokens is > 0)
            root["max_output_tokens"] = maxOutputTokens.Value;
        return root.ToJsonString();
    }

    /// <summary>
    /// Принимает ли бэкенд клиентский лимит вывода (max_output_tokens).
    /// Бэкенд подписки ChatGPT/Codex (chatgpt.com/backend-api) отвергает его
    /// с 400 «Unsupported parameter: max_output_tokens» — туда кап не шлём.
    /// </summary>
    public static bool AcceptsOutputCaps(string? baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl)
           || !baseUrl.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Требует ли бэкенд потоковый ответ. Бэкенд подписки ChatGPT/Codex
    /// (chatgpt.com/backend-api) отвечает только на stream=true — иначе 400
    /// «Stream must be set to true».
    /// </summary>
    public static bool RequiresStreaming(string? baseUrl)
        => !string.IsNullOrWhiteSpace(baseUrl)
           && baseUrl.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase);

    // Одно сообщение Responses: type=message, role=user|assistant,
    // content=[{type=input_text|output_text, text=…}].
    private static JsonObject Message(string role, string text, string contentType) => new()
    {
        ["type"] = "message",
        ["role"] = role,
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = contentType,
            ["text"] = text,
        }),
    };

    // ── Буферный ответ ──────────────────────────────────────────────────────

    /// <summary>Разобранный буферный ответ Responses API.</summary>
    /// <param name="Content">Текст ответа (может быть пустым).</param>
    /// <param name="FinishReason">stop / length в терминах чат-протокола (null — не пришёл).</param>
    /// <param name="Error">Сообщение об ошибке провайдера (если ответ — это ошибка).</param>
    public sealed record ParsedResponse(string? Content, int PromptTokens, int CompletionTokens, int TotalTokens,
        string? FinishReason, string? Error);

    /// <summary>
    /// Разбор буферного ответа (stream=false): output[].content[].text (все
    /// output_text-части склеиваются), usage{input_tokens,output_tokens,total_tokens},
    /// status → finish_reason (completed → stop, incomplete → length).
    /// Бросает JsonException на не-JSON теле — вызывающий трактует это как
    /// «ответ не распарсился» (существующий путь пустого ответа).
    /// </summary>
    public static ParsedResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new ParsedResponse(null, 0, 0, 0, null, null);

        var error = ErrorMessage(root);
        var content = ExtractOutputText(root);
        var (prompt, completion, total) = Usage(root);
        var finish = FinishReason(root);
        if (error == null && Str(root, "status") == "failed")
            error = "провайдер вернул статус failed";
        return new ParsedResponse(content, prompt, completion, total, finish, error);
    }

    // Текст ответа: склеиваем output_text-части ВСЕХ output-элементов
    // (провайдер может разбить ответ на несколько элементов).
    private static string ExtractOutputText(JsonElement root)
    {
        var sb = new StringBuilder();
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            // reasoning/function_call элементы — не текст ответа.
            var itemType = Str(item, "type");
            if (itemType != null && itemType != "message") continue;
            sb.Append(ExtractContentText(item));
        }
        return sb.ToString();
    }

    // Текст одного элемента output: content[].text с типом output_text.
    private static string ExtractContentText(JsonElement item)
    {
        var sb = new StringBuilder();
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var partType = Str(part, "type");
            if (partType != null && partType != "output_text") continue;
            var text = Str(part, "text");
            if (!string.IsNullOrEmpty(text)) sb.Append(text);
        }
        return sb.ToString();
    }

    // usage → (prompt, completion, total); total = input+output, если не пришёл.
    internal static (int prompt, int completion, int total) Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return (0, 0, 0);
        var prompt = Int(u, "input_tokens", "prompt_tokens");
        var completion = Int(u, "output_tokens", "completion_tokens");
        var total = Int(u, "total_tokens");
        return (prompt, completion, total > 0 ? total : prompt + completion);
    }

    // status ответа → finish_reason чат-протокола (stop / length).
    internal static string? FinishReason(JsonElement root)
    {
        var status = Str(root, "status");
        if (status == "completed") return "stop";
        if (status == "incomplete") return "length";
        // incomplete_details.reason == "max_output_tokens" — тоже обрыв по лимиту.
        if (root.TryGetProperty("incomplete_details", out var det) && det.ValueKind == JsonValueKind.Object &&
            Str(det, "reason") == "max_output_tokens")
            return "length";
        return null;
    }

    // Сообщение об ошибке провайдера: {"error":{"message":"…"}} или {"error":"…"}.
    internal static string? ErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("error", out var err)) return null;
        if (err.ValueKind == JsonValueKind.String) return err.GetString();
        if (err.ValueKind == JsonValueKind.Object) return Str(err, "message");
        return null;
    }

    private static string? Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
        return 0;
    }

    // ── SSE-поток ───────────────────────────────────────────────────────────

    /// <summary>
    /// Накопитель событий SSE-протокола Responses:
    /// response.output_text.delta (текст), response.output_text.done (финальный
    /// текст — запасной вариант, если дельт не было), response.completed /
    /// response.incomplete (статус + usage), response.failed и event «error»
    /// (ошибка провайдера — её показываем юзеру, а не прячем в «пустой ответ»).
    /// </summary>
    public sealed class StreamParser
    {
        private readonly StringBuilder _content = new();
        private string? _finalText;

        /// <summary>Склеенный текст из дельт.</summary>
        public string Content => _content.ToString();

        /// <summary>Сколько текстовых дельт пришло.</summary>
        public int DeltaCount { get; private set; }

        public int PromptTokens { get; private set; }
        public int CompletionTokens { get; private set; }
        public int TotalTokens { get; private set; }

        /// <summary>finish_reason в терминах чат-протокола (stop / length).</summary>
        public string? FinishReason { get; private set; }

        /// <summary>Сообщение об ошибке провайдера (response.failed / event error).</summary>
        public string? Error { get; private set; }

        /// <summary>Терминальное событие пришло (response.completed/failed/incomplete, error или [DONE]).</summary>
        public bool Terminal { get; private set; }

        /// <summary>Сколько SSE-чанков не распарсилось как JSON.</summary>
        public int ParseFailures { get; private set; }

        /// <summary>Колбэк onDelta бросил исключение — дальше дельты не отдаём.</summary>
        public bool OnDeltaThrew { get; internal set; }

        /// <summary>Итоговый текст: дельты, а если их не было — финальный text события.</summary>
        public string ResolveContent() => _content.Length > 0 ? _content.ToString() : (_finalText ?? "");

        /// <summary>
        /// Обработать одну полезную нагрузку SSE (без префикса «data: »).
        /// Возвращает текстовую дельту (для колбэка onDelta) или null.
        /// </summary>
        public string? Feed(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return null;
            var payload = data.Trim();
            // «data: [DONE]» может быть, а может и не быть — терминальными
            // считаем и его, и response.completed/response.failed.
            if (payload == "[DONE]") { Terminal = true; return null; }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                ParseFailures++;
                return null;
            }

            switch (Str(root, "type"))
            {
                case "response.output_text.delta":
                {
                    var delta = Str(root, "delta");
                    if (string.IsNullOrEmpty(delta)) return null;
                    _content.Append(delta);
                    DeltaCount++;
                    return delta;
                }

                case "response.output_text.done":
                {
                    var text = Str(root, "text");
                    if (!string.IsNullOrEmpty(text)) _finalText = text;
                    return null;
                }

                case "response.output_text":
                {
                    // Провайдер прислал целый item сообщения — это финальный текст.
                    var text = ExtractContentText(root);
                    if (!string.IsNullOrEmpty(text)) _finalText = text;
                    return null;
                }

                case "response.completed":
                case "response.incomplete":
                case "response.failed":
                {
                    Terminal = true;
                    var response = root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object
                        ? r
                        : root;
                    var (prompt, completion, total) = Usage(response);
                    PromptTokens = prompt;
                    CompletionTokens = completion;
                    TotalTokens = total;
                    var status = Str(response, "status");
                    if (Str(root, "type") == "response.failed" || status == "failed")
                        Error = ErrorMessage(response) ?? "провайдер вернул ошибку";
                    else if (Str(root, "type") == "response.incomplete" || status == "incomplete")
                        FinishReason = "length";
                    else
                        FinishReason = "stop";
                    // Фолбэк: дельт не было — полный текст может лежать в терминальном событии.
                    if (_content.Length == 0)
                    {
                        var text = ExtractOutputText(response);
                        if (!string.IsNullOrEmpty(text)) _finalText = text;
                    }
                    return null;
                }

                case "error":
                {
                    Terminal = true;
                    Error = Str(root, "message") ?? ErrorMessage(root) ?? "ошибка провайдера";
                    return null;
                }

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Похоже ли тело на SSE-поток. Бэкенд подписки ChatGPT/Codex отдаёт
    /// text/event-stream БЕЗ заголовка Content-Type, поэтому формат
    /// определяем по первому непробельному символу: '{' или '[' — это JSON,
    /// 'e' (event:) или 'd' (data:) — SSE.
    /// </summary>
    public static bool LooksLikeSse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.AsSpan().TrimStart();
        if (trimmed.Length == 0) return false;
        var c = trimmed[0];
        if (c == '{' || c == '[') return false;   // однозначно JSON
        return c == 'e' || c == 'd';
    }

    /// <summary>
    /// Разбор SSE-тела, уже прочитанного в строку (ответ без Content-Type —
    /// приходит целиком, разбираем как поток). Дельты в парсер не отдаются:
    /// вызывающий сам раздаёт их потребителю.
    /// </summary>
    public static StreamParser ParseSse(string body)
    {
        var parser = new StreamParser();
        using var reader = new StringReader(body);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            parser.Feed(data);
        }
        return parser;
    }

    /// <summary>
    /// Читает SSE-поток ответа Responses в парсер. Дельты уходят в onDelta
    /// (исключение колбэка гасится — баг UI не должен убивать стрим).
    /// </summary>
    public static async Task<StreamParser> ReadSseAsync(HttpResponseMessage response, Action<string>? onDelta,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await ReadSseAsync(reader, onDelta, ct);
    }

    /// <summary>Читает SSE-поток из уже открытого reader'а (тело без Content-Type).</summary>
    public static async Task<StreamParser> ReadSseAsync(TextReader reader, Action<string>? onDelta,
        CancellationToken ct)
    {
        var parser = new StreamParser();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            var delta = parser.Feed(data);
            if (string.IsNullOrEmpty(delta) || onDelta == null || parser.OnDeltaThrew) continue;
            try { onDelta(delta); }
            catch { parser.OnDeltaThrew = true; }
        }
        return parser;
    }
}
