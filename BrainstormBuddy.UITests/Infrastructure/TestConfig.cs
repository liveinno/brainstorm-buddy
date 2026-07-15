using System.Collections.Generic;
using System.Text.Json;
using System.IO;

namespace BrainstormBuddy.UITests.Infrastructure;

public class ProviderConfig
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string VisionModel { get; set; } = "";
}

public class TestConfig
{
    public string ExePath { get; set; } = "";
    public List<ProviderConfig> VisionProviders { get; set; } = new();

    public static TestConfig Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Config file not found: {path}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TestConfig>(json) ?? new TestConfig();
    }
}