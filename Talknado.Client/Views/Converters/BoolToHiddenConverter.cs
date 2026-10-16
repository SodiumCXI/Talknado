using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Talknado.Client.Views.Converters;

/// <summary>false — Hidden, а не Collapsed: место в разметке остаётся.</summary>
public class BoolToHiddenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
