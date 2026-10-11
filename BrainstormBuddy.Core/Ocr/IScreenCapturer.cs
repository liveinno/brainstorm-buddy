namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Порт снимка экрана: «снять прямоугольник физического рабочего стола».
/// </summary>
/// <remarks>
/// <para>
/// <b>Координаты — физические пиксели виртуального десктопа</b>, включая отрицательные:
/// монитор слева от главного даёт <c>X &lt; 0</c>. Никаких DIP на этом слое.
/// </para>
/// <para>
/// <b>Приватность.</b> Кадр — содержимое чужого экрана. Реализация не имеет права писать
/// его на диск, отправлять куда-либо или класть в лог. В журнал уходят только размеры,
/// имя реализации и время.
/// </para>
/// </remarks>
public interface IScreenCapturer : IDisposable
{
    /// <summary>Имя реализации для журнала.</summary>
    string Name { get; }

    /// <summary>
    /// Снимок прямоугольника рабочего стола. Регион обрезается по границам виртуального
    /// десктопа; после обрезки он не пуст.
    /// </summary>
    /// <exception cref="ArgumentException">После обрезки прямоугольник пуст.</exception>
    ScreenFrame CaptureRegion(PixelRect region);
}

/// <summary>
/// Снятый кадр: пиксели в <see cref="PixelFormatKind.Bgra32"/> плюс метаданные о том,
/// откуда и чем он снят.
/// </summary>
/// <remarks>
/// [WARN] Намеренно НЕ печатает содержимое буфера: автосгенерированный <c>ToString()</c>
/// позиционного record выводит только метрики (размеры и имя), но не сами пиксели.
/// </remarks>
public sealed record ScreenFrame(PixelBuffer Pixels, PixelRect Region, string Source, TimeSpan Elapsed)
{
    public override string ToString()
        => $"{Region.Width}×{Region.Height} @ ({Region.Left},{Region.Top}), {Source}, {Elapsed.TotalMilliseconds:F0} мс";
}
