using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace Talknado.Client.Views.Converters;

public class SliderWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double val && parameter is Slider slider)
        {
            double min = slider.Minimum;
            double max = slider.Maximum;
            double width = slider.ActualWidth;
            return (val - min) / (max - min) * width;
        }
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
