using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Orientation = System.Windows.Controls.Orientation;

namespace BrainstormBuddy;

/// <summary>
/// Адаптивная раскладка Memory Center (волна 05): ширина области → ориентация.
/// Используется, например, для переноса действий карточки факта под текст при ActualWidth &lt; Threshold.
/// Порог задаётся в XAML свойством <see cref="Threshold"/> в DIP, без ручного пересчёта DPI.
/// </summary>
public sealed class WidthToOrientationConverter : IValueConverter
{
    /// <summary>Порог ActualWidth (DIP): ниже него результат <see cref="Orientation.Vertical"/>.</summary>
    public double Threshold { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double width && width < Threshold ? Orientation.Vertical : Orientation.Horizontal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Адаптивная раскладка Memory Center (волна 05): ширина области → ширина колонки.
/// ActualWidth ≥ <see cref="Threshold"/> → <see cref="WideValue"/>, иначе <see cref="NarrowValue"/>
/// (строки-числа в DIP, напр. "280"/"240"; "Auto"/"*" тоже допустимы — как у GridLength).
/// </summary>
public sealed class WidthToGridLengthConverter : IValueConverter
{
    /// <summary>Порог ActualWidth (DIP), от которого применяется <see cref="WideValue"/>.</summary>
    public double Threshold { get; set; }

    /// <summary>Ширина при ActualWidth ≥ Threshold (DIP).</summary>
    public string WideValue { get; set; } = "280";

    /// <summary>Ширина при ActualWidth &lt; Threshold (DIP).</summary>
    public string NarrowValue { get; set; } = "240";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = value is double d ? d : 0;
        var text = width >= Threshold ? WideValue : NarrowValue;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return GridLength.Auto;
        return new GridLength(v);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
