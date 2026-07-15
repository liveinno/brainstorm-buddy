using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace BrainstormBuddy.UITests.Infrastructure;

public class VisionClient
{
    private readonly List<ProviderConfig> _providers;
    public bool LastCallAllFailed { get; private set; }
    public string LastFailureReason { get; private set; } = "";

    public VisionClient(TestConfig config)
    {
        _providers = config.VisionProviders;
    }

    /// <summary>
    /// Pings every configured provider with a tiny text-only request.
    /// Returns true if ALL providers failed (caller should abort the test).
    /// Returns false if at least one provider is healthy.
    /// </summary>
    public async Task<bool> PingAllProvidersAsync()
    {
        if (_providers == null || _providers.Count == 0)
        {
            LastFailureReason = "No Vision providers configured in config.test.json";
            return true;
        }

        var errors = new StringBuilder();
        int failedCount = 0;

        foreach (var provider in _providers)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

                var payload = new
                {
                    model = provider.VisionModel,
                    messages = new[]
                    {
                        new { role = "user", content = "ping" }
                    },
                    max_tokens = 5
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await http.PostAsync(provider.BaseUrl, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  [OK] {provider.VisionModel} ({provider.BaseUrl})");
                    LastCallAllFailed = false;
                    LastFailureReason = "";
                    return false;
                }
                else
                {
                    failedCount++;
                    errors.AppendLine($"  [DEAD] {provider.VisionModel} ({provider.BaseUrl}): {response.StatusCode}");
                    errors.AppendLine($"         {Truncate(responseString, 300)}");
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                errors.AppendLine($"  [DEAD] {provider.VisionModel} ({provider.BaseUrl}): {ex.Message}");
            }
        }

        LastCallAllFailed = true;
        LastFailureReason = $"All {failedCount} provider(s) failed:\n{errors}";
        return true;
    }

    public async Task<string> AskAboutImageAsync(string base64Image, string prompt)
    {
        var errors = new StringBuilder();
        LastCallAllFailed = false;

        foreach (var provider in _providers)
        {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

                var payload = new
                {
                    model = provider.VisionModel,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = prompt },
                                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } }
                            }
                        }
                    },
                    max_tokens = 1000
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await http.PostAsync(provider.BaseUrl, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseString);
                    var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

                    // Короткий, но однозначный OK-вердикт — валидный ответ, не «слабый»
                    // (модель может ответить и «КОНТРАСТНОСТЬ OK»)
                    var isShortOk = !string.IsNullOrWhiteSpace(text) &&
                                    (text.Trim().StartsWith("OK", StringComparison.OrdinalIgnoreCase) ||
                                     (text.Length < 60 && text.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0));
                    if ((string.IsNullOrWhiteSpace(text) || text.Length < 20 || IsSafetyDodged(text)) && !isShortOk)
                    {
                        errors.AppendLine($"[Weak {provider.VisionModel}]: response too short or empty: '{Truncate(text ?? "null", 100)}'");
                        continue;
                    }

                    return text;
                }
                else
                {
                    errors.AppendLine($"[Failed {provider.VisionModel}]: {response.StatusCode} - {Truncate(responseString, 200)}");
                }
            }
            catch (Exception ex)
            {
                errors.AppendLine($"[Exception {provider.VisionModel}]: {ex.Message}");
            }
        }

        LastCallAllFailed = true;
        LastFailureReason = errors.ToString();
        return $"All providers failed.\n{errors.ToString()}";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    private static bool IsSafetyDodged(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("user safety") ||
               lower.Contains("safe") && lower.Contains("content") ||
               lower.Contains("cannot process") ||
               lower.Contains("i cannot") ||
               lower.Contains("i'm unable") ||
               lower.Contains("i am unable") ||
               lower.Contains("not able to") ||
               lower.Contains("sorry,");
    }
}
