using System.Globalization;
using System.Windows.Data;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.Views.Converters;

/// <summary>
/// Счётчик кадров с единицей: фраза одним ключом, единица в языках стоит по-разному.
/// </summary>
public class FpsTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Format(culture, Strings.FpsFormat, value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
