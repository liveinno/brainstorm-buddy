using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Embeddings;

/// <summary>
/// Эмбеддинги через OpenAI-совместимый endpoint: POST {BaseUrl}/embeddings
/// { model, input: prefix + text }. Префикс "query: "/"passage: " — конвенция E5,
/// безвредная для обычных embedding-моделей.
///
/// Отдельный маленький HttpClient: IApiClient не имеет embeddings-endpoint.
/// Таймаут — per-call через linked CTS (как в OpenAiClient), HttpClient.Timeout
/// не трогаем. Ретраи: 429/5xx, до MaxRetries раз с экспоненциальным backoff.
///
/// Никогда не бросает наружу: любые ошибки (сеть, HTTP, JSON, таймаут,
/// отмена) → Warn/Debug в лог и null — memory не должна ломать live-путь.
/// </summary>
public sealed class OpenAiMemoryEmbeddingProvider : IMemoryEmbeddingProvider, IDisposable
{
    private const string LogCat = "Memory";

    private readonly ApiConfig _api;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly LoggingService? _logger;

    public OpenAiMemoryEmbeddingProvider(ApiConfig api, string model,
        LoggingService? logger = null, HttpMessageHandler? handler = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _model = model ?? "";
        _logger = logger;
        // Таймаут намеренно НЕ здесь — per-call linked CTS в EmbedAsync.
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        if (!string.IsNullOrEmpty(api.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.ApiKey);
    }

    public string Name => $"openai:{_model}";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_api.BaseUrl) && !string.IsNullOrWhiteSpace(_model);

    public async Task<float[]?> EmbedAsync(string text, bool isQuery, CancellationToken ct)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return null;

        var prefix = isQuery ? "query: " : "passage: ";
        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            input = prefix + text
        });
        var url = $"{_api.BaseUrl.TrimEnd('/')}/embeddings";
        int timeoutSec = _api.RequestTimeoutSeconds > 0 ? _api.RequestTimeoutSeconds : 30;
        int maxRetries = Math.Max(0, _api.MaxRetries);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                var token = timeoutCts.Token;

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                using var resp = await _http.SendAsync(req, token);
                var body = await resp.Content.ReadAsStringAsync(token);

                if (!resp.IsSuccessStatusCode)
                {
                    if (IsRetryable(resp.StatusCode) && attempt < maxRetries)
                    {
                        _logger?.Info($"Embeddings retry {attempt + 1}/{maxRetries} after {(int)resp.StatusCode}", LogCat);
                        await Task.Delay(BackoffMs(attempt), token);
                        continue;
                    }
                    _logger?.Warn($"Embeddings ← {(int)resp.StatusCode}: {Truncate(body, 200)}", LogCat);
                    return null;
                }
                return ParseEmbedding(body);
            }
            catch (OperationCanceledException)
            {
                // Отмена вызывающего или таймаут — оба «эмбеддинга нет», live-путь идёт дальше.
                _logger?.Debug(ct.IsCancellationRequested ? "Embeddings cancelled" : $"Embeddings timeout {timeoutSec}s", LogCat);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Embeddings request failed: {ex.Message}", LogCat);
                return null;
            }
        }
    }

    private float[]? ParseEmbedding(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var el = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            var vec = new float[el.GetArrayLength()];
            int i = 0;
            foreach (var x in el.EnumerateArray()) vec[i++] = x.GetSingle();
            return vec.Length > 0 ? vec : null;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Embeddings parse failed: {ex.Message}: {Truncate(body, 200)}", LogCat);
            return null;
        }
    }

    private static bool IsRetryable(HttpStatusCode code)
    {
        var n = (int)code;
        return n == 429 || (n >= 500 && n < 600);
    }

    private static int BackoffMs(int attempt) => 500 * (1 << Math.Min(attempt, 4));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => _http.Dispose();
}
