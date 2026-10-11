namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Прямоугольник в ФИЗИЧЕСКИХ пикселях экрана.
/// </summary>
/// <remarks>
/// Инвариант проекта (AGENTS.md, ТЗ §1): все геометрические вычисления ведутся в физических
/// пикселях, DIP появляются только на отрисовке. Отдельный тип вместо System.Drawing.Rectangle
/// нужен именно для того, чтобы единица измерения была видна в сигнатуре и мультимониторная
/// арифметика не смешивалась с координатами WPF.
///
/// Координаты могут быть ОТРИЦАТЕЛЬНЫМИ: монитор слева от главного даёт X &lt; 0, монитор
/// сверху — Y &lt; 0. Это штатная ситуация, а не ошибка.
/// </remarks>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static readonly PixelRect Empty = new(0, 0, 0, 0);

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>Площадь в пикселях. Для вырожденного прямоугольника — 0.</summary>
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>Пустой = нулевая или отрицательная сторона. Такой прямоугольник ничего не покрывает.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;

    /// <summary>Прямоугольник по двум углам в любом порядке — как его тянет мышь.</summary>
    public static PixelRect FromCorners(int x1, int y1, int x2, int y2)
    {
        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        return new PixelRect(left, top, Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    public static PixelRect FromBounds(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>Пересечение. Не пересекаются — <see cref="Empty"/>.</summary>
    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right <= left || bottom <= top ? Empty : FromBounds(left, top, right, bottom);
    }

    /// <summary>Пересекаются ли прямоугольники хотя бы одним пикселем.</summary>
    public bool IntersectsWith(PixelRect other)
        => !IsEmpty && !other.IsEmpty
           && Left < other.Right && Right > other.Left
           && Top < other.Bottom && Bottom > other.Top;

    /// <summary>Минимальный прямоугольник, покрывающий оба. Пустой операнд игнорируется.</summary>
    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;

        return FromBounds(
            Math.Min(Left, other.Left),
            Math.Min(Top, other.Top),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>Раздуть на <paramref name="d"/> пикселей во все стороны (отрицательное — сжать).</summary>
    public PixelRect Inflate(int d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    /// <summary>Сдвиг — например, из координат виртуального десктопа в координаты снимка.</summary>
    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Обрезать по границам <paramref name="bounds"/>. Целиком снаружи — <see cref="Empty"/>.</summary>
    public PixelRect ClampTo(PixelRect bounds) => Intersect(bounds);

    /// <summary>
    /// Доля перекрытия по вертикали относительно меньшей высоты: 0 — не перекрываются,
    /// 1 — один полностью внутри другого по Y. Используется при склейке слов в строки:
    /// слова одной строки перекрываются по вертикали почти полностью.
    /// </summary>
    public double VerticalOverlapRatio(PixelRect other)
    {
        if (IsEmpty || other.IsEmpty) return 0;

        var overlap = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        if (overlap <= 0) return 0;

        return (double)overlap / Math.Min(Height, other.Height);
    }

    public override string ToString() => $"({X},{Y} {Width}×{Height})";
}
