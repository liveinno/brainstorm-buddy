using System;
using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;

namespace BrainstormBuddy.Controls;

/// <summary>
/// Адаптивная двухпанельная раскладка для настроек (волна 03):
/// при ширине >= Breakpoint дети стоят рядом — первый занимает FirstFraction
/// свободной ширины (или своё естественное, если FirstFraction=NaN), второй — остаток;
/// при ширине &lt; Breakpoint дети стопкой вертикально на всю ширину.
/// Gap — промежуток между панелями в обоих режимах.
/// Зачем не Grid+DataTrigger: ColumnDefinitions нельзя переключать сеттерами —
/// нужен код раскладки.
/// </summary>
public sealed class TwoPanePanel : Panel
{
    public static readonly DependencyProperty BreakpointProperty =
        DependencyProperty.Register(nameof(Breakpoint), typeof(double), typeof(TwoPanePanel),
            new FrameworkPropertyMetadata(560.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Порог ширины: ниже него — вертикальная стопка.</summary>
    public double Breakpoint
    {
        get => (double)GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    public static readonly DependencyProperty FirstFractionProperty =
        DependencyProperty.Register(nameof(FirstFraction), typeof(double), typeof(TwoPanePanel),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Доля первой панели от свободной ширины (0..1). NaN — по содержимому (Auto).</summary>
    public double FirstFraction
    {
        get => (double)GetValue(FirstFractionProperty);
        set => SetValue(FirstFractionProperty, value);
    }

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(TwoPanePanel),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Промежуток между панелями (DIP).</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }
    /// <summary>
    /// Текущий режим раскладки (stacked/wide) — прицепляется к детям в Arrange.
    /// Дети могут читать его через Style-DataTrigger, например чтобы менять
    /// MinHeight под режим (в LLM-пресетах список: 144 в стопке / 240 в двухпанельной).
    /// Пишется только из Arrange — не участвует в measure, циклов не создаёт.
    /// </summary>
    public static readonly DependencyProperty IsStackedModeProperty =
        DependencyProperty.RegisterAttached("IsStackedMode", typeof(bool), typeof(TwoPanePanel),
            new FrameworkPropertyMetadata(false));

    public static bool GetIsStackedMode(DependencyObject d) => (bool)d.GetValue(IsStackedModeProperty);
    private static void SetIsStackedMode(DependencyObject d, bool v) => d.SetValue(IsStackedModeProperty, v);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
            return new Size();

        bool stacked = IsStacked(availableSize.Width);
        if (stacked)
        {
            double width = 0, height = 0;
            for (int i = 0; i < children.Count; i++)
            {
                children[i].Measure(availableSize);
                var d = children[i].DesiredSize;
                width = Math.Max(width, d.Width);
                height += d.Height + (i > 0 ? Gap : 0);
            }
            return new Size(width, height);
        }

        double freeWidth = Math.Max(0, availableSize.Width - Gap);
        double firstWidth = ResolveFirstWidth(freeWidth);
        children[0].Measure(new Size(firstWidth, availableSize.Height));
        var firstDesired = children[0].DesiredSize;

        var secondDesired = new Size();
        if (children.Count > 1)
        {
            double secondWidth = Math.Max(0, freeWidth - firstDesired.Width);
            children[1].Measure(new Size(secondWidth, availableSize.Height));
            secondDesired = children[1].DesiredSize;
        }

        double rowHeight = Math.Max(firstDesired.Height, secondDesired.Height);
        double rowWidth = firstDesired.Width + (children.Count > 1 ? Gap + secondDesired.Width : 0);
        return new Size(rowWidth, rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
            return finalSize;

        bool stacked = IsStacked(finalSize.Width);

        // Прицепляем режим к детям: триггеры на IsStackedMode (например, MinHeight
        // пресет-списка 144 в стопке / 240 в двухпанельной) реагируют после Arrange.
        for (int i = 0; i < children.Count; i++)
            SetIsStackedMode(children[i], stacked);
        if (stacked)
        {
            double y = 0;
            for (int i = 0; i < children.Count; i++)
            {
                double h = children[i].DesiredSize.Height;
                children[i].Arrange(new Rect(0, y, finalSize.Width, h));
                y += h + Gap;
            }
            return finalSize;
        }

        double freeWidth = Math.Max(0, finalSize.Width - Gap);
        double firstWidth = ResolveFirstWidth(freeWidth);
        var first = children[0];
        // NaN/∞ (FirstFraction не задан) — первая панель по содержимому: берём
        // DesiredSize.Width, иначе второй ребёнок уехал бы в X=∞ с нулевой шириной.
        double arrangedFirst = double.IsPositiveInfinity(firstWidth)
            ? first.DesiredSize.Width
            : firstWidth;
        first.Arrange(new Rect(0, 0, arrangedFirst, finalSize.Height));

        if (children.Count > 1)
        {
            double secondX = arrangedFirst + Gap;
            double secondWidth = Math.Max(0, finalSize.Width - secondX);
            children[1].Arrange(new Rect(secondX, 0, secondWidth, finalSize.Height));
        }
        return finalSize;
    }

    private bool IsStacked(double width) =>
        !double.IsNaN(width) && !double.IsInfinity(width) && width < Breakpoint;

    private double ResolveFirstWidth(double freeWidth)
    {
        double fraction = FirstFraction;
        if (double.IsNaN(fraction) || fraction <= 0 || fraction > 1)
            return double.PositiveInfinity; // по содержимому
        return Math.Max(0, freeWidth * fraction);
    }
}
