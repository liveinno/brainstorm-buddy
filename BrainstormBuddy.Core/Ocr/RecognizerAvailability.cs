namespace BrainstormBuddy.Core.Ocr;

/// <summary>Готовность движка распознавания к работе на этой машине.</summary>
/// <remarks>
/// Отдельный статус вместо исключения нужен по ТЗ §2.1 и §8: модели Tesseract могут
/// отсутствовать, и приложение обязано **сообщить о деградации, а не падать**.
/// </remarks>
public enum RecognizerAvailability
{
    /// <summary>Ещё не проверяли.</summary>
    Unknown = 0,

    /// <summary>Готов: нужные языки на месте, инициализация прошла.</summary>
    Available = 1,

    /// <summary>
    /// Движок в системе есть, но нужных языков нет (не установлены языковые пакеты OCR)
    /// либо не найдены модели <c>tessdata</c>. Причина — в <c>UnavailabilityReason</c>.
    /// </summary>
    Unavailable = 2,
}
