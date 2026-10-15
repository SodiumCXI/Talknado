using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace Talknado.Client.Views.Controls;

/// <summary>
/// Полоса плавно доезжает до нового значения, а не перескакивает к нему.
/// Значение приходит раз в 50 мс, а ход длится 150 мс — заполнение едет без
/// остановок. Назад полоса не едет: уменьшение значит новую загрузку или
/// другое вложение в переиспользованной строке списка.
/// </summary>
public static class SmoothProgress
{
    private static readonly Duration Glide = new(TimeSpan.FromMilliseconds(150));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(SmoothProgress), new PropertyMetadata(0d, OnValueChanged));

    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);

    // Вложение, к которому относится текущий ход: строка списка переиспользуется
    // для другого вложения, и ехать от чужого значения нельзя.
    private static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(object), typeof(SmoothProgress));

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase bar)
            return;

        double target = (double)e.NewValue;
        bool sameItem = ReferenceEquals(bar.GetValue(ItemProperty), bar.DataContext);
        bar.SetValue(ItemProperty, bar.DataContext);

        if (!sameItem || !bar.IsVisible || target < bar.Value)
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null);
            bar.Value = target;
            return;
        }

        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(target, Glide), HandoffBehavior.SnapshotAndReplace);
    }
}
