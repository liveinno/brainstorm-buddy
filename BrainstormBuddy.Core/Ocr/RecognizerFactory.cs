namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Единственная точка сборки движков распознавания, чей продукт совпадает с гейтом качества
/// (BACKLOG ТЗ §2).
/// </summary>
/// <remarks>
/// Выбор движка — <c>ScreenOcrConfig.Engine</c>: <c>"tesseract"</c> (умолчание) собирается
/// здесь, <c>"windows"</c> живёт в WPF-проекте (WinRT недоступен в Core-TFM) и оборачивается
/// тем же препроцессингом через <see cref="Create(ITextRecognizer, Action{string}?)"/>.
/// Продуктовый путь совпадает с гейтом качества (BACKLOG D-59).
/// </remarks>
public static class RecognizerFactory
{
    /// <summary>
    /// Собрать движок распознавания. Препроцессинг (ТЗ §2.3) навешивается всегда:
    /// без него настройки «улучшение мелкого текста» и «автоинверсия» не делают ничего.
    /// </summary>
    /// <param name="tessdataPath">
    /// Каталог моделей Tesseract. <c>null</c> — путь по умолчанию рядом со сборкой.
    /// </param>
    /// <param name="trace">
    /// Приёмник трасс повторного препроцессинга. Без трассировки повторный проход на слабом
    /// результате проходил бы молча.
    /// </param>
    /// <returns>
    /// Возвращается КОНКРЕТНЫЙ <see cref="PreprocessingRecognizer"/>, а не интерфейс: он всегда
    /// внешняя обёртка по построению и он освобождаем. Tesseract держит нативную память,
    /// и вызывающий обязан иметь возможность написать <c>using</c> — с типом
    /// <c>ITextRecognizer</c> это не компилируется, а забытый Dispose на приложении, живущем
    /// в трее сутками, копит утечку.
    /// </returns>
    public static PreprocessingRecognizer Create(
        string? tessdataPath = null,
        Action<string>? trace = null)
        => Create(new TesseractRecognizer(tessdataPath), trace);

    /// <summary>
    /// Навесить препроцессинг на готовый движок (например, Windows OCR из WPF-проекта).
    /// Владение передаётся: Dispose обёртки освобождает и внутренний движок.
    /// </summary>
    public static PreprocessingRecognizer Create(
        ITextRecognizer inner,
        Action<string>? trace = null)
        => new(inner) { Trace = trace };
}
