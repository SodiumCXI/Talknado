using System.Globalization;
using System.Windows.Data;

namespace Talknado.Client.Views.Converters;

/// <summary>
/// Предел роста строки ввода: без него лента сообщений сжимается до нескольких пикселей.
/// </summary>
public class InputTextBoxMaxHeightConverter : IMultiValueConverter
{
    /// <summary>Доля окна, которую строке ввода разрешено занять.</summary>
    private const double MaxShare = 0.4;

    /// <summary>Строка ввода не мельче одной строки текста даже в низком окне.</summary>
    private const double MinHeight = 34;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values[0] is double windowHeight && values[1] is double titleBarHeight)
        {
            return Math.Max(MinHeight, (windowHeight - titleBarHeight) * MaxShare);
        }
        return MinHeight;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
