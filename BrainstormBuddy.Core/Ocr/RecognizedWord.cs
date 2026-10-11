

namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Одно слово, как его вернул движок распознавания.
/// </summary>
/// <remarks>
/// ТЗ §2.1: результат распознавания — не строка, а структура «строки → слова с bounding box,
/// уверенностью и языком». Координаты нужны для восстановления колонок (TSV), уверенность —
/// для подсветки сомнительных мест в окне результата.
/// </remarks>
/// <param name="Text">Текст слова без окружающих пробелов.</param>
/// <param name="Bounds">Границы слова в координатах РАСПОЗНАННОГО ИЗОБРАЖЕНИЯ (физические пиксели).</param>
/// <param name="Confidence">
/// Уверенность движка, 0..1. Если распознаватель не сообщает уверенность отдельного слова,
/// ставится <see cref="UnknownConfidence"/>.
/// </param>
/// <param name="Language">Язык слова, определённый по его символам.</param>
public readonly record struct RecognizedWord(
    string Text,
    PixelRect Bounds,
    double Confidence,
    TextLanguage Language)
{
    /// <summary>
    /// Маркер «движок уверенность не сообщил». Отличается от 0.0, которая означает
    /// «распознаватель уверен, что это мусор»: смешивать их нельзя, иначе средняя
    /// уверенность станет недостоверной и повторный проход будет запускаться случайно.
    /// </summary>
    public const double UnknownConfidence = -1.0;

    public bool HasConfidence => Confidence >= 0;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    public override string ToString() => Text;
}
