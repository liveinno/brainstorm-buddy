namespace BrainstormBuddy.Ai;

/// <summary>Исход детального STT-распознавания. Разводит то, что прежний
/// TranscribeAsync сворачивал в один null: пустой текст (речи нет) ≠ ошибка
/// сервера ≠ отмена вызывающим.</summary>
public enum SttTranscribeStatus
{
    /// <summary>Речь распознана, Text непустой.</summary>
    Text,
    /// <summary>Сервер ответил 200, но текст пустой/пробельный — речи в аудио нет.</summary>
    NoSpeech,
    /// <summary>Техническая ошибка (HTTP, таймаут, нет поля "text"); детали в Error.</summary>
    Failed,
    /// <summary>Запрос прерван родительским CancellationToken (не таймаут).</summary>
    Canceled
}

/// <summary>Детальный результат STT-запроса: статус исхода, текст и тех. детали ошибки.</summary>
/// <param name="Status">Категория исхода.</param>
/// <param name="Text">Распознанный текст (только для Status=Text), уже вычищенный по краям.</param>
/// <param name="Error">Человекочитаемая причина неудачи (Failed/Canceled/NoSpeech).</param>
/// <param name="HttpStatus">HTTP-код ответа сервера, если ошибка пришла от него; иначе null.</param>
public sealed record SttTranscribeResult(SttTranscribeStatus Status, string? Text, string? Error, int? HttpStatus)
{
    /// <summary>True, когда речь распознана и Text готов к использованию.</summary>
    public bool IsSuccess => Status == SttTranscribeStatus.Text;

    /// <summary>Сервер распознал речь; Text уже вычищен по краям вызывающим.</summary>
    public static SttTranscribeResult Recognized(string text)
        => new(SttTranscribeStatus.Text, text, null, null);

    /// <summary>HTTP 200 при пустом/пробельном тексте: речи в аудио нет, сервер жив.</summary>
    public static SttTranscribeResult NoSpeechResult(string? error = null)
        => new(SttTranscribeStatus.NoSpeech, null, error, null);

    /// <summary>Технический сбой: HTTP-ошибка, таймаут, неожиданное тело ответа.</summary>
    public static SttTranscribeResult FailedResult(string error, int? httpStatus = null)
        => new(SttTranscribeStatus.Failed, null, error, httpStatus);

    /// <summary>Вызов прерван родительским CancellationToken (истёкший таймаут — это Failed).</summary>
    public static SttTranscribeResult CanceledResult(string? error = null)
        => new(SttTranscribeStatus.Canceled, null, error, null);
}
