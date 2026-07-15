using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Ai;

public record AgentResponse(string AgentId, string AgentName, string Color, string Text, bool IsSilent, long LatencyMs, string? Error,
    int PromptTokens = 0, int CompletionTokens = 0);

public class AgentOrchestrator
{
    protected HttpClient _http;
    private readonly MultiAgentConfig _config;
    private readonly Dictionary<string, List<ChatMessage>> _histories = new();
    private readonly Dictionary<string, string> _lastResponse = new();  // для дедупа залипших ответов
    private readonly List<string> _recentTurns = new();  // скользящий контекст последних реплик диалога
    private const int MaxContext = 6;
    private const string SilentToken = "[SILENT]";

    public AgentOrchestrator(MultiAgentConfig config, string? apiKey, string? baseUrl)
    {
        _config = config;
        // Локальные модели (ollama) обрабатывают параллельные запросы последовательно —
        // двум агентам нужен запас по времени
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        if (!string.IsNullOrEmpty(baseUrl) && !baseUrl.EndsWith('/'))
            baseUrl += '/';
        BaseUrl = (baseUrl ?? "https://api.openai.com/v1/") + "chat/completions";
    }

    /// <summary>Опциональный лог-хук: ошибки и вердикты агентов идут сюда (категория Agent).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Гонять агентов ПОСЛЕДОВАТЕЛЬНО (не параллельно) — чтобы не перегружать прокси/API.</summary>
    public bool Sequential { get; set; } = false;
    /// <summary>Пауза между запросами (мс) в последовательном режиме — против rate-limit.</summary>
    public int InterRequestDelayMs { get; set; } = 0;
    /// <summary>Ретраи при пустом ответе/ошибке (пустой ответ часто = временный rate-limit).</summary>
    public int MaxRetries { get; set; } = 0;
    public int RetryDelayMs { get; set; } = 5000;

    public string BaseUrl { get; }
    public bool Enabled => _config.Enabled;
    public ScenarioConfig? ActiveScenario =>
        _config.Scenarios.FirstOrDefault(s => s.Id == _config.ActiveScenarioId);

    public async Task<List<AgentResponse>> ProcessAsync(string userText, string chatModel, CancellationToken ct = default)
    {
        var scenario = ActiveScenario;
        if (scenario == null || scenario.Agents.Count == 0)
            return new();

        var profileText = _config.UserProfile.FormatForPrompt();

        // Добавляем текущую реплику в контекст и строим сообщение с последними репликами диалога,
        // чтобы агент понимал, о чём разговор (речь кандидата тоже в контексте — см. NoteContext).
        NoteContext(userText);
        var userMessage = BuildContextMessage();

        var enabled = scenario.Agents.Where(a => a.Enabled).ToList();
        var results = new List<AgentResponse>();
        if (Sequential)
        {
            // По одному запросу за раз (+ пауза) — бережём прокси/аккаунт от rate-limit.
            for (int i = 0; i < enabled.Count; i++)
            {
                if (i > 0 && InterRequestDelayMs > 0) await Task.Delay(InterRequestDelayMs, ct);
                results.Add(await AskAgentAsync(enabled[i], userMessage, profileText, chatModel, ct));
            }
        }
        else
        {
            var tasks = enabled.Select(a => AskAgentAsync(a, userMessage, profileText, chatModel, ct));
            results.AddRange(await Task.WhenAll(tasks));
        }
        return results;
    }

    /// <summary>
    /// Добавить реплику в контекст диалога БЕЗ генерации ответа. Для речи кандидата ([Микрофон])
    /// и не-вопросов собеседника: агент должен знать контекст, но не отвечать на них.
    /// </summary>
    public void NoteContext(string labeledText)
    {
        if (string.IsNullOrWhiteSpace(labeledText)) return;
        lock (_recentTurns)
        {
            _recentTurns.Add(labeledText.Trim());
            while (_recentTurns.Count > MaxContext) _recentTurns.RemoveAt(0);
        }
    }

    private string BuildContextMessage()
    {
        lock (_recentTurns)
        {
            if (_recentTurns.Count <= 1)
                return _recentTurns.LastOrDefault() ?? "";
            var sb = new StringBuilder();
            sb.AppendLine("Контекст последних реплик диалога (собеседник — [Динамик], кандидат — [Микрофон]):");
            foreach (var t in _recentTurns) sb.AppendLine(t);
            sb.AppendLine();
            sb.AppendLine("Ответь как суфлёр на ПОСЛЕДНЮЮ реплику, если это вопрос собеседника по ТВОЕЙ теме " +
                          "(по правилам из твоей роли). Если последняя реплика — речь самого кандидата ([Микрофон]) " +
                          "или явно не вопрос — выведи [SILENT].");
            return sb.ToString();
        }
    }

    private async Task<AgentResponse> AskAgentAsync(AgentConfig agent, string userText, string profileText, string chatModel, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var systemPrompt = BuildSystemPrompt(agent, profileText);

            // Stateless: НЕ подаём агенту его прошлые ответы. Иначе qwen 3b залипает и дословно
            // повторяет прежний ответ на все реплики (вырожденный цикл — главный BLOCKER).
            // Даём только системный промпт + текущую реплику.
            var messages = new List<object>
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText },
            };

            var requestBody = new
            {
                model = chatModel,
                messages,
                temperature = 0.6,          // выше 0.3 — меньше залипания на один шаблон
                frequency_penalty = 0.6,    // штраф за повтор токенов
                presence_penalty = 0.3,
                max_tokens = Math.Min(agent.MaxWords * 3, 500)
            };

            var json = JsonSerializer.Serialize(requestBody);
            string text = ""; int promptTok = 0, complTok = 0; string? lastErr = null;
            // Ретраи: пустой ответ/HTTP-ошибка часто = временный rate-limit прокси; ждём и повторяем.
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                if (attempt > 0) await Task.Delay(RetryDelayMs, ct);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(BaseUrl, content, ct);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) { lastErr = $"HTTP {response.StatusCode}"; continue; }
                using var doc = JsonDocument.Parse(body);
                text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTok = pt.GetInt32();
                    else if (usage.TryGetProperty("input_tokens", out var it)) promptTok = it.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ck)) complTok = ck.GetInt32();
                    else if (usage.TryGetProperty("output_tokens", out var ot)) complTok = ot.GetInt32();
                }
                if (string.IsNullOrWhiteSpace(text)) { lastErr = "пустой ответ (возможно rate-limit)"; continue; }
                lastErr = null; break;
            }
            if (lastErr != null && string.IsNullOrWhiteSpace(text))
                return new AgentResponse(agent.Id, agent.Name, agent.Color, "", false, sw.ElapsedMilliseconds, lastErr);

            // [SILENT] считаем и когда модель добавила его в начало ответа с пояснением
            var isSilent = text.Trim() == SilentToken || text.TrimStart().StartsWith(SilentToken, StringComparison.Ordinal);

            // Дедуп: если ответ почти дословно повторяет прошлый ответ ЭТОГО агента — это
            // залипание, а не осмысленный ответ. Глушим в [SILENT].
            if (!isSilent && IsNearDuplicateLocked(agent.Id, text))
            {
                isSilent = true;
                Log?.Invoke($"Agent {agent.Id}: DEDUP (повтор прошлого ответа) → [SILENT]");
            }
            if (!isSilent)
                lock (_lastResponse) _lastResponse[agent.Id] = text;

            Log?.Invoke($"Agent {agent.Id}: {(isSilent ? "[SILENT]" : $"{text.Length} chars")} in {sw.ElapsedMilliseconds}ms (tok {promptTok}/{complTok})");
            return new AgentResponse(agent.Id, agent.Name, agent.Color, text, isSilent, sw.ElapsedMilliseconds, null, promptTok, complTok);
        }
        catch (TaskCanceledException)
        {
            Log?.Invoke($"Agent {agent.Id}: TIMEOUT after {sw.ElapsedMilliseconds}ms ({BaseUrl})");
            return new AgentResponse(agent.Id, agent.Name, agent.Color, "", false, sw.ElapsedMilliseconds, "Timeout");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Agent {agent.Id}: ERROR {ex.Message} after {sw.ElapsedMilliseconds}ms ({BaseUrl})");
            return new AgentResponse(agent.Id, agent.Name, agent.Color, "", false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private string BuildSystemPrompt(AgentConfig agent, string profileText)
    {
        return agent.SystemPrompt
            .Replace("{{STYLE}}", agent.Style)
            .Replace("{{TONE}}", agent.Tone)
            .Replace("{{LANGUAGE}}", agent.Language)
            .Replace("{{MAX_WORDS}}", agent.MaxWords.ToString())
            .Replace("{{USER_PROFILE}}", profileText)
            .Replace("{{EXTRA_INSTRUCTIONS}}", agent.ExtraInstructions);
    }

    // Похож ли новый ответ на прошлый ответ этого агента (залипание). Сравниваем по нормализованным
    // словам: доля общих слов ≥ 0.8 → считаем дублем.
    private bool IsNearDuplicateLocked(string agentId, string text)
    {
        string prev;
        lock (_lastResponse) { if (!_lastResponse.TryGetValue(agentId, out prev!)) return false; }
        var a = Normalize(text); var b = Normalize(prev);
        if (a.Count == 0 || b.Count == 0) return false;
        int common = a.Count(w => b.Contains(w));
        double overlap = common / (double)Math.Max(a.Count, b.Count);
        return overlap >= 0.8;
    }

    private static HashSet<string> Normalize(string s) =>
        new(s.ToLowerInvariant().Split(new[] { ' ', '\n', '\r', '\t', ',', '.', '!', '?', ';', ':', '[', ']', '(', ')' },
            StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2));

    public void ResetHistory(string? agentId = null)
    {
        lock (_lastResponse)
        {
            if (agentId != null) { _histories.Remove(agentId); _lastResponse.Remove(agentId); }
            else { _histories.Clear(); _lastResponse.Clear(); }
        }
        if (agentId == null) lock (_recentTurns) _recentTurns.Clear();
    }
}