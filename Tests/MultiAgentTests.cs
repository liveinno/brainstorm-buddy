using Xunit;
using BrainstormBuddy.Config;
using BrainstormBuddy.Ai;
using System.Text;
using System.Text.Json;
using System.Net;
using Moq;
using Moq.Protected;
using System.Net.Http;

namespace BrainstormBuddy.Tests;

public class MultiAgentTests
{
    [Fact]
    public void ScenarioConfig_Has5DefaultScenarios()
    {
        var interview = ScenarioConfig.CreateInterview();
        Assert.Equal("interview", interview.Id);
        Assert.Equal(2, interview.Agents.Count);
        Assert.Contains(interview.Agents, a => a.Id == "tech_lead");
        Assert.Contains(interview.Agents, a => a.Id == "hrd");

        var brainstorm = ScenarioConfig.CreateBrainstorm();
        Assert.Equal(3, brainstorm.Agents.Count);

        var customer = ScenarioConfig.CreateCustomerCall();
        Assert.Equal(3, customer.Agents.Count);

        var career = ScenarioConfig.CreateCareerConsult();
        Assert.Equal(3, career.Agents.Count);

        var oneOnOne = ScenarioConfig.CreateOneOnOne();
        Assert.Equal(3, oneOnOne.Agents.Count);
    }

    [Fact]
    public void UserProfile_FormatForPrompt_ContainsAllSections()
    {
        var profile = UserProfile.CreateDefault();
        var text = profile.FormatForPrompt();
        Assert.Contains("Профиль кандидата", text);
        Assert.Contains("Ключевые кейсы", text);
        Assert.Contains("Технические навыки", text);
        Assert.Contains("Soft skills", text);
        Assert.Contains("Не умею", text);
    }

    [Fact]
    public async Task Orchestrator_ReturnsSilent_ForHrdOnTechQuestion()
    {
        var config = new MultiAgentConfig
        {
            Enabled = true,
            ActiveScenarioId = "interview",
            UserProfile = UserProfile.CreateDefault(),
            Scenarios = new() { ScenarioConfig.CreateInterview() }
        };

        var httpMock = new Mock<HttpMessageHandler>();
        httpMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                using var doc = JsonDocument.Parse(body);
                var messages = doc.RootElement.GetProperty("messages");
                var systemPrompt = messages[0].GetProperty("content").GetString() ?? "";

                string response;
                if (systemPrompt.Contains("HRD-агент"))
                    response = "[SILENT]";
                else
                    response = "Гибридный Scrum с 2-недельными спринтами";

                var json = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = response } } }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

        var orchestrator = new TestOrchestrator(config, httpMock.Object);

        var results = await orchestrator.ProcessAsync("Какая методология?", "test-model");
        Assert.Equal(2, results.Count);

        var techLead = results.First(r => r.AgentId == "tech_lead");
        Assert.False(techLead.IsSilent);
        Assert.NotEmpty(techLead.Text);

        var hrd = results.First(r => r.AgentId == "hrd");
        Assert.True(hrd.IsSilent);
    }

    [Fact]
    public void MultiAgentConfig_Defaults_Enabled()
    {
        var defaults = MultiAgentConfig.CreateDefaults();
        Assert.True(defaults.Enabled);
        Assert.Equal("interview", defaults.ActiveScenarioId);
        Assert.Equal(5, defaults.Scenarios.Count);
        Assert.NotEmpty(defaults.UserProfile.Summary);
    }
}

// Test orchestrator that allows injection of HttpMessageHandler
public class TestOrchestrator : AgentOrchestrator
{
    public TestOrchestrator(MultiAgentConfig config, HttpMessageHandler handler)
        : base(config, "test-key", "https://test.example.com/v1")
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }
}