namespace BrainstormBuddy.Config;

public class AgentConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4ADE80";
    public string Icon { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public int MaxWords { get; set; } = 120;
    public string Tone { get; set; } = "Деловой";
    public string Style { get; set; } = "Структурированный";
    public string Language { get; set; } = "ru";
    public string ExtraInstructions { get; set; } = "";
    public bool Enabled { get; set; } = true;
}