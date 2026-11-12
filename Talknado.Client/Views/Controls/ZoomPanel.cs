using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Talknado.Client.Views.Controls;

/// <summary>
/// Приближение колесом мыши к точке под курсором и перетаскивание приближенного.
/// Содержимое раскладывается вписанным в панель, масштаб и сдвиг накладываются
/// через RenderTransform: на каждом шаге разметка не пересчитывается.
/// Zoom — во сколько раз крупнее вписанного размера.
/// </summary>
public sealed class ZoomPanel : Border
{
    public const double MaxZoom = 8;
    private const double WheelStep = 1.25;

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(ZoomPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ZoomPanel)d).OnZoomChanged()));

    private readonly MatrixTransform _transform = new();
    private double _appliedZoom = 1;
    private Vector _shift;
    private Point? _anchor;
    private Point _dragStart;
    private Vector _dragShift;

    public ZoomPanel()
    {
        ClipToBounds = true;
        // Без фона пустые поля вокруг картинки не ловят колесо
        Background = Brushes.Transparent;
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    private bool HasContent => Child is { IsVisible: true };

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        if (Child != null)
        {
            Child.RenderTransform = _transform;
            // RenderSize панели здесь ещё прежний: он обновится после возврата
            ApplyTransform(size);
        }
        return size;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!HasContent)
            return;

        // Предел здесь, а не в CoerceValue: урезанное значение привязка в источник не отдаёт
        _anchor = e.GetPosition(this);
        Zoom = Math.Clamp(Zoom * Math.Pow(WheelStep, e.Delta / 120.0), 1, MaxZoom);
        _anchor = null;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!HasContent || Zoom <= 1)
            return;

        // До захвата: CaptureMouse сразу же поднимает MouseMove
        _dragStart = e.GetPosition(this);
        _dragShift = _shift;
        e.Handled = CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured)
            return;

        _shift = _dragShift + (e.GetPosition(this) - _dragStart);
        ApplyTransform(RenderSize);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    /// <summary>Точка под курсором (или середина панели) остаётся на месте.</summary>
    private void OnZoomChanged()
    {
        if (Child != null)
        {
            var anchor = (Vector)(_anchor ?? new Point(ActualWidth / 2, ActualHeight / 2));
            var origin = VisualTreeHelper.GetOffset(Child);
            var content = (anchor - origin - _shift) / _appliedZoom;
            _shift = anchor - origin - content * Zoom;
        }

        Cursor = Zoom > 1 ? Cursors.SizeAll : null;
        ApplyTransform(RenderSize);
    }

    /// <summary>Приближенное не уезжает от края панели, меньшее панели стоит по центру.</summary>
    private void ApplyTransform(Size panel)
    {
        _appliedZoom = Zoom;
        if (Child == null)
            return;

        var origin = VisualTreeHelper.GetOffset(Child);
        _shift = new Vector(
            Fit(_shift.X, origin.X, Child.RenderSize.Width, Zoom, panel.Width),
            Fit(_shift.Y, origin.Y, Child.RenderSize.Height, Zoom, panel.Height));
        _transform.Matrix = new Matrix(Zoom, 0, 0, Zoom, _shift.X, _shift.Y);
    }

    /// <summary>
    /// Меньшее панели держится середины, где его поставила раскладка, а не середины
    /// панели: раскладка округляет место до пикселя, и без этого при масштабе 1
    /// оставался бы сдвиг в полпикселя, который приближение умножает.
    /// </summary>
    private static double Fit(double shift, double origin, double size, double zoom, double panel) =>
        size * zoom <= panel
            ? size * (1 - zoom) / 2
            : Math.Clamp(shift, panel - size * zoom - origin, -origin);
}
