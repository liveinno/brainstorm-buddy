namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Язык распознанного текста. Определяется ПО РЕЗУЛЬТАТУ — по доле кириллицы и латиницы
/// (ТЗ §2.2), а не по настройке движка: пользователь видит, что реально распознали.
/// </summary>
public enum TextLanguage
{
    /// <summary>Букв не найдено вовсе — только цифры, знаки препинания или пусто.</summary>
    Unknown = 0,

    /// <summary>Подавляющее большинство букв — кириллица.</summary>
    Russian = 1,

    /// <summary>Подавляющее большинство букв — латиница.</summary>
    English = 2,

    /// <summary>
    /// Обе письменности заметно представлены. Типовой случай BrainstormBuddy:
    /// «Открыть file_name.txt через Ctrl+Shift+P» (ТЗ §2.2).
    /// </summary>
    Mixed = 3,
}
