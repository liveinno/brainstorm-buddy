namespace BrainstormBuddy.Ai;

public record ChatMessage(string Role, string Content, long? Timestamp = null)
{
    /// <summary>Типизированные части «изображение» (ТЗ image-chat §7): ссылки на
    /// локально сохранённые картинки. Сериализация в image_url/data-url — только
    /// в транспорте на момент запроса; в истории/суммаризации части не теряются
    /// и не считаются текстом.</summary>
    public IReadOnlyList<Core.Images.ChatImageRef>? Images { get; init; }

    public static ChatMessage User(string content) =>
        new("user", content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Ручной ход с картинками: текст может быть пустым — сообщение
    /// остаётся полноценным за счёт частей изображения.</summary>
    public static ChatMessage UserWithImages(string content, IReadOnlyList<Core.Images.ChatImageRef> images) =>
        new("user", content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { Images = images };
    public static ChatMessage Assistant(string content) =>
        new("assistant", content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
