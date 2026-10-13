using System.Globalization;
using System.Windows.Data;

namespace Talknado.Client.Views.Converters;

/// <summary>Отрицательное значение — задержка ещё не измерена.</summary>
public class PingTextConverter : IValueConverter
{
    private const int MaxShown = 999;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        int and > MaxShown => ">" + MaxShown,
        int ping and >= 0 => ping.ToString(culture),
        _ => string.Empty
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
