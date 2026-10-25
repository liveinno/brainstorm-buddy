using System.Text.Json;

namespace BrainstormBuddy.Ai;

/// <summary>
/// Получение списка моделей для КАНДИДАТА-провайдера (ещё не выбранного в
/// настройках). Не зависит от текущего ApiConfig: принимает профиль и ключ,
/// поэтому список моделей можно показать до того, как провайдер станет активным.
/// Форматы ответа: OpenAI {"data":[{"id":…}]}, Codex {"models":[{"slug"|"id":…}]},
/// голый массив ["id", …].
/// </summary>
public static class ProviderModelLister
{
    /// <summary>Результат запроса списка моделей.</summary>
    public sealed record ModelListResult(IReadOnlyList<string> Models, string? Error, string SourcePath)
    {
        /// <summary>Модели найдены (список непуст).</summary>
        public bool Ok => Models.Count > 0;
    }

    /// <summary>
    /// GET списка моделей у провайдера. <paramref name="paths"/> — пути-кандидаты
    /// (пробуются по очереди); <paramref name="extraHeaders"/> — заголовки профиля
    /// (например chatgpt-account-id для Codex).
    /// </summary>
    public static async Task<ModelListResult> FetchAsync(
        LlmProviderProfile profile,
        string? apiKey,
        IEnumerable<string>? paths = null,
        CancellationToken ct = default,
        string? model = null)
    {
        var candidates = (paths ?? DefaultPaths(profile)).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (candidates.Count == 0) candidates.Add("/models");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        string? lastError = null;
        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var url = BuildUrl(profile.BaseUrl, path);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(request, profile, apiKey);
                foreach (var (name, value) in profile.ExtraHeaders)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var headerValue = value.Replace("{session}", Guid.NewGuid().ToString("N"));
                    // "{model}" (x-codex-routing-hint) подставляем, когда модель
                    // известна: бэкенд подписки ждёт реальный id, а не плейсхолдер.
                    if (!string.IsNullOrWhiteSpace(model))
                        headerValue = headerValue.Replace("{model}", model);
                    request.Headers.TryAddWithoutValidation(name, headerValue);
                }
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                using var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"{(int)resp.StatusCode}: {Trim(body, 160)}";
                    continue;
                }
                var models = ParseModels(body);
                if (models.Count > 0)
                    return new ModelListResult(models, null, path);
                lastError = "ответ без списка моделей";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }
        return new ModelListResult(Array.Empty<string>(), lastError, candidates.First());
    }

    /// <summary>Пути-кандидаты для профиля (Codex — сначала /codex/models).</summary>
    public static IReadOnlyList<string> DefaultPaths(LlmProviderProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ModelsPath))
            return new[] { profile.ModelsPath };
        return new[] { "/models" };
    }

    /// <summary>Парсит список id моделей из ответа провайдера.</summary>
    public static List<string> ParseModels(string body)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return result;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var list = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (!root.TryGetProperty("data", out list) && !root.TryGetProperty("models", out list))
                    list = root;
            }
            if (list.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in list.EnumerateArray())
            {
                string? id = null;
                if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "id", "slug", "model", "name" })
                    {
                        if (item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            id = v.GetString();
                            break;
                        }
                    }
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    id = item.GetString();
                }
                if (!string.IsNullOrWhiteSpace(id) && !result.Contains(id!))
                    result.Add(id!);
            }
        }
        catch (JsonException)
        {
            return result;
        }
        return result;
    }

    private static string BuildUrl(string baseUrl, string path)
    {
        var b = (baseUrl ?? "").TrimEnd('/');
        var p = path.StartsWith('/') ? path : "/" + path;
        return b + p;
    }

    private static void ApplyAuth(HttpRequestMessage request, LlmProviderProfile profile, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return;
        switch ((profile.AuthKind ?? "bearer"))
        {
            case "none":
                break;
            case "x-api-key":
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                break;
            default:
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                break;
        }
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
