namespace BrainstormBuddy.Config;

/// <summary>
/// Режим совместимости STT-запроса: строгие OpenAI-совместимые API (Groq, OpenAI)
/// отклоняют неизвестные multipart-поля, поэтому расширения серверов faster-whisper
/// можно отправлять только явно выбранным ("faster-whisper") режимом.
/// </summary>
public static class SttCompatModes
{
    /// <summary>Только строгие поля OpenAI /audio/transcriptions — дефолт и фолбэк.</summary>
    public const string Standard = "standard";

    /// <summary>Плюс beam_size и vad_filter — для серверов на faster-whisper.</summary>
    public const string FasterWhisper = "faster-whisper";

    /// <summary>true, если режим допускает расширения faster-whisper (beam_size/vad_filter).</summary>
    public static bool SupportsExtensions(string? mode)
        => string.Equals(mode?.Trim(), FasterWhisper, StringComparison.OrdinalIgnoreCase);

    /// <summary>Нормализует значение из конфига: null/пусто/неизвестное → "standard".</summary>
    public static string Normalize(string? mode)
        => SupportsExtensions(mode) ? FasterWhisper : Standard;
}
