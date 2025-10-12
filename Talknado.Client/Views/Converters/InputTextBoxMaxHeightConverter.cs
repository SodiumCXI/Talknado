using System.Globalization;
using System.Windows.Data;

namespace Talknado.Client.Views.Converters;

public class InputTextBoxMaxHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values[0] is double windowHeight && values[1] is double titleBarHeight)
        {
            return windowHeight - titleBarHeight * 2;
        }
        return 0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
