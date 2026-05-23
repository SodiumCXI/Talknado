using System.Globalization;
using System.Windows.Data;

namespace Talknado.Client.Views.Converters;

public class MicIconConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isActive = values[0] is true;
        bool isSpeaking = values[1] is true;

        string path = !isActive ?  "/Resources/mic_off_white.svg" :
                      isSpeaking ? "/Resources/mic_on_green.svg" :
                                   "/Resources/mic_on_white.svg";

        return new Uri(path, UriKind.Relative);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}