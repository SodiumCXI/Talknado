using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Talknado.Client.Views.Converters;

/// <summary>
/// Отрезки дорожки — пары долей «начало, конец» от 0 до 1 — в кисть, которая
/// закрашивает их цветом <see cref="Fill"/> по всей ширине элемента.
/// </summary>
public sealed class BufferedRangesBrushConverter : IValueConverter
{
    public Brush? Fill { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double[] { Length: >= 2 } ranges || Fill == null)
            return null;

        var group = new GeometryGroup();
        for (int i = 0; i + 1 < ranges.Length; i += 2)
            group.Children.Add(new RectangleGeometry(new Rect(ranges[i], 0, ranges[i + 1] - ranges[i], 1)));

        var brush = new DrawingBrush(new GeometryDrawing(Fill, null, group))
        {
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
