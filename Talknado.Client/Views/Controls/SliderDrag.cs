using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Talknado.Client.Views.Controls;

/// <summary>
/// Нажатие на дорожку ставит бегунок под курсор и сразу начинает его тянуть.
/// Сам Slider при IsMoveToPointEnabled только переносит бегунок: нажатие он
/// помечает обработанным, и бегунок мыши не захватывает.
/// </summary>
public static class SliderDrag
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SliderDrag), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static readonly MouseButtonEventHandler PreviewDownHandler = OnPreviewMouseLeftButtonDown;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
            return;

        slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, PreviewDownHandler);
        if ((bool)e.NewValue)
            slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, PreviewDownHandler, true);
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider { Template: { } template } slider
            || template.FindName("PART_Track", slider) is not Track { Thumb: { IsMouseOver: false, IsDragging: false } thumb })
            return;

        // Бегунок уже получил новое значение, но стоит на старом месте; захват
        // запоминает точку внутри бегунка, и без пересчёта она съехала бы на
        // расстояние от старого места до курсора.
        slider.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb
        });
    }
}
