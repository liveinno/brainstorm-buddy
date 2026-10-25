using BrainstormBuddy.Core.Images;

namespace BrainstormBuddy.Ai;

/// <summary>
/// Сборка мультимодального content сообщений для протокола openai-chat (ТЗ §7):
/// text-часть (если текст непустой) + image_url-части с data:...;base64.
/// Core ничего не знает о файлах — для истории байты даёт резолвер вызывающего.
/// </summary>
internal static class ChatImageTransport
{
    /// <summary>Массив частей user-сообщения: [{type:"text",text}] + image_url-части.
    /// Текст пустой → только картинки; список картинок пустой → только текст
    /// (вызывающий сам решает, нужен ли массив — для обычного текста content
    /// остаётся строкой).</summary>
    public static IReadOnlyList<object> BuildUserContent(string? text, IReadOnlyList<ChatImagePayload>? images)
    {
        var parts = new List<object>(1 + (images?.Count ?? 0));
        if (!string.IsNullOrEmpty(text))
            parts.Add(new { type = "text", text });
        if (images != null)
            foreach (var img in images)
                parts.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:{img.Mime};base64,{Convert.ToBase64String(img.Bytes)}" }
                });
        return parts;
    }

    /// <summary>Content для истории (ChatMessage): у сообщения с ссылками на
    /// картинки резолвим каждую через делегат (null → часть пропускается, решает
    /// резолвер и логирует сам вызывающий). Ни одна не резолвилась → вернём
    /// строку — запрос не ломаем.</summary>
    public static object BuildMessageContent(ChatMessage m, Func<ChatImageRef, ChatImagePayload?> resolve)
    {
        if (m.Images is not { Count: > 0 } refs || resolve == null)
            return m.Content;
        var payloads = new List<ChatImagePayload>(refs.Count);
        foreach (var r in refs)
        {
            var p = resolve(r);
            if (p != null) payloads.Add(p);
        }
        return payloads.Count == 0 ? m.Content : BuildUserContent(m.Content, payloads);
    }
}
