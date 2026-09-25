using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace BrainstormBuddy.Tests;

public class OpenAiClientTests
{
    [Fact]
    public async Task MockTranscribe_ReturnsText()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"text\":\"hello world\"}", Encoding.UTF8, "application/json")
            });

        var config = new ApiConfig
        {
            BaseUrl = "https://test.example.com/v1",
            ApiKey = "test-key",
            SttModel = "whisper-1",
            RequestTimeoutSeconds = 5,
            MaxRetries = 0
        };
        var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
        var client = new OpenAiClient(config, logger, handler.Object);

        var text = await client.TranscribeAsync(new byte[] { 1, 2, 3 });

        Assert.Equal("hello world", text);
    }

    [Fact]
    public async Task RetryLogic_On503Error()
    {
        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.ServiceUnavailable,
                        Content = new StringContent("{\"error\":\"busy\"}")
                    };
                }
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{\"text\":\"ok\"}", Encoding.UTF8, "application/json")
                };
            });

        var config = new ApiConfig
        {
            BaseUrl = "https://test.example.com/v1",
            ApiKey = "test-key",
            SttModel = "whisper-1",
            RequestTimeoutSeconds = 5,
            MaxRetries = 2
        };
        var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
        var client = new OpenAiClient(config, logger, handler.Object);

        var text = await client.TranscribeAsync(new byte[] { 1, 2, 3 });

        Assert.Equal(2, callCount);
        Assert.Equal("ok", text);
    }

    [Fact]
    public async Task AskAsync_ReturnsAnswer()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Yes.\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        var config = new ApiConfig
        {
            BaseUrl = "https://test.example.com/v1",
            ApiKey = "test-key",
            ChatModel = "test-model",
            RequestTimeoutSeconds = 5,
            MaxRetries = 0
        };
        var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
        var client = new OpenAiClient(config, logger, handler.Object);

        var result = await client.AskAsync("test question", "system", 50, new System.Collections.Generic.List<ChatMessage>());

        Assert.Equal("Yes.", result.Content);
    }

    [Fact]
    public async Task GetModels_AppliesProfileExtraHeaders_AndParsesIds()
    {
        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                captured = req;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(
                        "{\"data\":[{\"id\":\"space-bunny-free\"},{\"id\":\"deepseek-v4.1-flash\"}]}",
                        Encoding.UTF8, "application/json")
                };
            });

        var config = new ApiConfig
        {
            ProviderId = "opencode-go",
            BaseUrl = "https://opencode.ai/zen/go/v1",
            ApiKey = "oc_sk_test",
            RequestTimeoutSeconds = 5,
            MaxRetries = 0
        };
        var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
        var client = new OpenAiClient(config, logger, handler.Object);

        var models = await client.GetModelsAsync();

        Assert.Equal(new[] { "space-bunny-free", "deepseek-v4.1-flash" }, models);
        Assert.NotNull(captured);
        // Профиль opencode-go: Bearer + обязательный x-opencode-session (стабильный per-client).
        Assert.Equal("oc_sk_test", captured!.Headers.Authorization?.Parameter);
        Assert.True(captured.Headers.Contains("x-opencode-session"));
        Assert.Equal("/zen/go/v1/models", captured.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task AskAsync_NormalizesModelId_AndReadsReasoningContent()
    {
        string? sentModel = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                var json = req.Content!.ReadAsStringAsync().Result;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                sentModel = doc.RootElement.GetProperty("model").GetString();
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    // content пуст, ответ лежит в reasoning_content (reasoning-модель, A6)
                    Content = new StringContent(
                        "{\"choices\":[{\"message\":{\"content\":\"\",\"reasoning_content\":\"ответ\"}}]," +
                        "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}",
                        Encoding.UTF8, "application/json")
                };
            });

        var config = new ApiConfig
        {
            BaseUrl = "https://test.example.com/v1",
            ApiKey = "k",
            ChatModel = " model​ ", // zero-width + пробелы — должны срезаться (A7)
            RequestTimeoutSeconds = 5,
            MaxRetries = 0
        };
        var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
        var client = new OpenAiClient(config, logger, handler.Object);

        var result = await client.AskAsync("q", "s", 2000, new System.Collections.Generic.List<ChatMessage>());

        Assert.Equal("model", sentModel);
        Assert.Equal("ответ", result.Content);
        Assert.Equal(3, result.TotalTokens);
    }

    [Fact]
    public async Task CheckLlmConnectionDetailed_ReportsSteps_AndRetriesEmptyContent()
    {
        int chatCalls = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                if (req.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"data\":[{\"id\":\"m1\"}]}") };
                chatCalls++;
                // Нечётные ответы пустые → каждый вызов проверки делает авто-ретрай (A6).
                var content = chatCalls % 2 == 1
                    ? "{\"choices\":[{\"message\":{\"content\":\"\"}}]}"
                    : "{\"choices\":[{\"message\":{\"content\":\"готов\"}}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(content, Encoding.UTF8, "application/json") };
            });

        var config = new ApiConfig
        {
            ProviderId = "ollama",
            BaseUrl = "http://127.0.0.1:11434/v1",
            ChatModel = "qwen2.5vl:7b",
            RequestTimeoutSeconds = 5,
            MaxRetries = 0
        };
        var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
        var client = new OpenAiClient(config, logger, handler.Object);

        var steps = await client.CheckLlmConnectionDetailedAsync();
        var (ok, _) = await client.CheckLlmConnectionAsync();

        Assert.Equal(new[] { "URL", "Auth", "Models", "Chat" }, steps.Select(s => s.Name));
        Assert.True(steps.All(s => s.Ok), string.Join(" | ", steps.Select(s => $"{s.Name}:{s.Detail}")));
        // Пустой content при лимите 60 → авто-ретрай с 1024, затем успех (A6).
        Assert.Equal(4, chatCalls); // 2 ретрая detailed + 2 ретрая collapsed-обёртки
        Assert.True(ok);
    }

    [Fact]
    public async Task GetModels_OnError_ReturnsEmpty()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("no models") });

        var config = new ApiConfig
        {
            BaseUrl = "https://test.example.com/v1",
            ApiKey = "k",
            RequestTimeoutSeconds = 5,
            MaxRetries = 0
        };
        var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
        var client = new OpenAiClient(config, logger, handler.Object);

        Assert.Empty(await client.GetModelsAsync());
    }
}
