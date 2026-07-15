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
}
