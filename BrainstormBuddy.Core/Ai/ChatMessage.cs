namespace BrainstormBuddy.Ai;

public record ChatMessage(string Role, string Content, long? Timestamp = null)
{
    public static ChatMessage User(string content) =>
        new("user", content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ChatMessage Assistant(string content) =>
        new("assistant", content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
