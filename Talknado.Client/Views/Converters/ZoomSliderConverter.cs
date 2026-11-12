using System.Globalization;
using System.Windows.Data;
using Talknado.Client.Views.Controls;

namespace Talknado.Client.Views.Converters;

/// <summary>
/// Масштаб в положение ползунка 0–1 по логарифму: каждое удвоение занимает
/// равный отрезок, иначе первые два-три раза ушли бы в самое начало дорожки.
/// </summary>
public class ZoomSliderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Math.Log((double)value) / Math.Log(ZoomPanel.MaxZoom);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Math.Pow(ZoomPanel.MaxZoom, (double)value);
}
