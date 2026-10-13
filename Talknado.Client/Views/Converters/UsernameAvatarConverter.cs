using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Talknado.Client.Views.Converters;

/// <summary>
/// Первая буква ника для аватара. Для составного ника — две первые буквы слов.
/// </summary>
public class UsernameToInitialsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = (value as string)?.Trim();
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var initials = words.Length > 1
            ? string.Concat(FirstLetter(words[0]), FirstLetter(words[1]))
            : FirstLetter(words[0]);

        return initials.ToUpper(culture);
    }

    private static string FirstLetter(string word)
    {
        // char.IsSurrogatePair: эмодзи в нике — одна буква из двух char.
        var length = word.Length > 1 && char.IsSurrogatePair(word, 0) ? 2 : 1;
        return word.Substring(0, length);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Цвет аватара по нику: один и тот же ник всегда получает один и тот же цвет.
/// </summary>
public class UsernameToAvatarBrushConverter : IValueConverter
{
    private const int PaletteSize = 6;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = value as string ?? string.Empty;

        // Своя сумма, а не GetHashCode: тот рандомизирован между запусками,
        // и цвет аватара менялся бы при каждом старте приложения.
        var sum = 0;
        foreach (var c in name)
            sum = (sum + c) % (PaletteSize * 1000);

        var brush = Application.Current?.TryFindResource($"Avatar{sum % PaletteSize + 1}Brush");
        return brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
