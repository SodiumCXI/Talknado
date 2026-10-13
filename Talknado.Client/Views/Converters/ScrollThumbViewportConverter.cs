using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Talknado.Client.Views.Converters;

/// <summary>
/// Держит бегунок прокрутки не короче предела. Длину бегунка <c>Track</c> считает
/// по пропорции «видимая часть к общей», и <c>MinHeight</c> на неё не влияет. Поэтому
/// видимая часть подаётся увеличенной ровно настолько, чтобы бегунок вышел нужной
/// длины; на саму прокрутку это значение не влияет.
/// </summary>
public class ScrollThumbViewportConverter : IMultiValueConverter
{
    private const double FallbackMinLength = 40;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4
            || values[0] is not double viewport
            || values[1] is not double minimum
            || values[2] is not double maximum
            || values[3] is not double trackLength)
        {
            return DependencyProperty.UnsetValue;
        }

        double range = maximum - minimum;
        double minLength = Application.Current?.TryFindResource("ScrollThumbMinLength") as double? ?? FallbackMinLength;

        if (range <= 0 || trackLength <= minLength)
            return viewport;

        double needed = minLength * range / (trackLength - minLength);
        return Math.Max(viewport, needed);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
