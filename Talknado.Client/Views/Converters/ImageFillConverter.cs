using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Talknado.Client.Views.Converters;

/// <summary>
/// Картинка в заливку рамки с обрезкой по краям. Через конвертер, а не привязкой
/// внутри ImageBrush: у кисти нет своего контекста данных, и такая привязка
/// срабатывает раньше, чем кисть попадает в дерево, — с ошибкой привязки.
/// </summary>
public sealed class ImageFillConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ImageSource image)
            return null;

        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
