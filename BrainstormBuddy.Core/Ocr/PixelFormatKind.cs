namespace BrainstormBuddy.Core.Ocr;

/// <summary>Формат пикселей в <see cref="PixelBuffer"/>.</summary>
public enum PixelFormatKind
{
    /// <summary>
    /// 4 байта на пиксель, порядок B, G, R, A. Совпадает с <c>Bgra32</c> WPF и с тем,
    /// что отдаёт GDI/WGC, — поэтому конвертация на границе бесплатная.
    /// </summary>
    Bgra32 = 0,

    /// <summary>
    /// 1 байт на пиксель — яркость. Рабочий формат препроцессинга: движки OCR всё равно
    /// работают по яркости, а память и время обработки вчетверо меньше.
    /// </summary>
    Gray8 = 1,
}
