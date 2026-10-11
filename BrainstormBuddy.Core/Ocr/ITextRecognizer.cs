


namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Единый интерфейс движка распознавания (ТЗ §2.1). Единственная продуктовая реализация —
/// Tesseract 5, обёрнутый в препроцессинг.
/// </summary>
/// <remarks>
/// ⛔ Реализация НЕ имеет права делать сетевые вызовы — ни при какой отладке (ТЗ §8,
/// AGENTS.md). В BrainstormBuddy.Core.Ocr сетевых зависимостей нет и не должно появиться.
/// </remarks>
public interface ITextRecognizer
{
    /// <summary>Человекочитаемое имя — попадает в лог и в метаданные окна результата.</summary>
    string Name { get; }

    /// <summary>Готовность на этой машине. До вызова <see cref="InitializeAsync"/> — <c>Unknown</c>.</summary>
    RecognizerAvailability Availability { get; }

    /// <summary>Почему движок недоступен. Пусто, если доступен или ещё не проверялся.</summary>
    string? UnavailabilityReason { get; }

    /// <summary>
    /// Проверить языки и прогреть движок. Вызывается в фоне при старте приложения,
    /// а не по хоткею: иначе первое распознавание не уложится в бюджет 700 мс (ТЗ §2.5).
    /// Не бросает при отсутствии языков — выставляет <see cref="RecognizerAvailability.Unavailable"/>.
    /// </summary>
    Task InitializeAsync(RecognitionLanguages languages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Распознать подготовленный растр. Движок недоступен — вернуть
    /// <see cref="RecognitionResult.Empty"/>, а не бросать.
    /// </summary>
    Task<RecognitionResult> RecognizeAsync(
        PixelBuffer image,
        RecognitionOptions options,
        CancellationToken cancellationToken = default);
}
