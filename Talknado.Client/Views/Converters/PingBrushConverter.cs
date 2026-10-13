using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Talknado.Client.Views.Converters;

/// <summary>Цвет задержки: меньше 100 мс, 100–499 мс, от 500 мс.</summary>
public sealed class PingBrushConverter : IValueConverter
{
    public Brush? Good { get; set; }
    public Brush? Fair { get; set; }
    public Brush? Poor { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        int and < 100 => Good,
        int and < 500 => Fair,
        _ => Poor
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
