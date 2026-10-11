namespace BrainstormBuddy.Core.Ocr;

/// <summary>Языки, которые просят распознать (ТЗ §6, раздел «Распознавание»).</summary>
[Flags]
public enum RecognitionLanguages
{
    None = 0,
    Russian = 1,
    English = 2,

    /// <summary>Смешанный режим — обязательное требование ТЗ §2.2.</summary>
    RussianAndEnglish = Russian | English,
}

/// <summary>Параметры одного вызова распознавания.</summary>
/// <param name="Languages">Какие языки искать.</param>
/// <param name="EnhanceSmallText">
/// Апскейл мелкого текста и бинаризация Otsu — опция «улучшение мелкого текста» (ТЗ §2.3, §6).
/// </param>
/// <param name="AutoInvertDarkThemes">
/// Автоинверсия по медианной яркости для светлого текста на тёмном фоне (ТЗ §2.3).
/// </param>
/// <param name="DetectBarcodes">Искать QR и штрих-коды (ТЗ §2.4).</param>
public readonly record struct RecognitionOptions(
    RecognitionLanguages Languages,
    bool EnhanceSmallText,
    bool AutoInvertDarkThemes,
    bool DetectBarcodes)
{
    /// <summary>Дефолт продукта: RU+EN, улучшения включены (ТЗ §6).</summary>
    public static readonly RecognitionOptions Default = new(
        RecognitionLanguages.RussianAndEnglish,
        EnhanceSmallText: true,
        AutoInvertDarkThemes: true,
        DetectBarcodes: true);
}
