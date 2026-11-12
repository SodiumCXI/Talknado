using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Talknado.Client.Views.Controls;

/// <summary>
/// Уровни картинки для приближения, крупный первым, — друг на друге во всю рамку
/// элемента: выведенный и все крупнее него (закрытые выведенным). Так крупные с
/// самого открытия уже в видеокарте, и переключение уровня ничего не стоит.
/// Рисуются здесь, а не списком ItemsControl: при работающем клиенте специальных
/// возможностей WPF держит пункты списка и после закрытия окна, а с ними и картинки.
/// </summary>
public sealed class ZoomLayersView : FrameworkElement
{
    public static readonly DependencyProperty LayersProperty = DependencyProperty.Register(
        nameof(Layers), typeof(IReadOnlyList<BitmapSource>), typeof(ZoomLayersView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShownLayerProperty = DependencyProperty.Register(
        nameof(ShownLayer), typeof(int), typeof(ZoomLayersView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<BitmapSource>? Layers
    {
        get => (IReadOnlyList<BitmapSource>?)GetValue(LayersProperty);
        set => SetValue(LayersProperty, value);
    }

    public int ShownLayer
    {
        get => (int)GetValue(ShownLayerProperty);
        set => SetValue(ShownLayerProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Layers is not { } layers)
            return;

        var frame = new Rect(RenderSize);
        for (int i = 0; i < layers.Count && i <= ShownLayer; i++)
            drawingContext.DrawImage(layers[i], frame);
    }
}
