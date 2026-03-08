using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Talknado.Client.Views;

public partial class TalknadoWindow : Window
{
    private double _actualTop;
    private WindowState _previousWindowState;

    protected bool UseCustomClose { get; set; } = true;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    public TalknadoWindow()
    {
        Loaded += OnWindowLoaded;
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;
    }

    protected void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnMaximizeButtonClick();
            return;
        }

        var element = (UIElement)sender;
        var startPoint = e.GetPosition(null);
        element.CaptureMouse();

        MouseEventHandler? moveHandler = null;
        MouseButtonEventHandler? upHandler = null;

        moveHandler = (_, _) =>
        {
            var currentPoint = e.GetPosition(null);
            if (Math.Abs(currentPoint.X - startPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(currentPoint.Y - startPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                element.MouseMove -= moveHandler;
                element.MouseLeftButtonUp -= upHandler;
                element.ReleaseMouseCapture();

                if (WindowState == WindowState.Maximized)
                {
                    double ratio = currentPoint.X / ActualWidth;
                    var screenPos = PointToScreen(currentPoint);

                    WindowState = WindowState.Normal;
                    _previousWindowState = WindowState.Normal;
                    UpdateLayout();

                    Left = screenPos.X - ratio * Width;

                    var handle = new WindowInteropHelper(this).Handle;
                    MoveWindow(handle, (int)Left, 0, (int)Width, (int)Height, true);
                }

                DragMove();

                var dpiInfo = VisualTreeHelper.GetDpi(this);
                double dpiScaleY = dpiInfo.DpiScaleY;
                Point currentPos = PointToScreen(new Point(0, 0));
                _actualTop = currentPos.Y / dpiScaleY;
            }
        };

        upHandler = (_, _) =>
        {
            element.MouseMove -= moveHandler;
            element.MouseLeftButtonUp -= upHandler;
            element.ReleaseMouseCapture();
        };

        element.MouseMove += moveHandler;
        element.MouseLeftButtonUp += upHandler;
    }

    protected void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        OnMinimizeButtonClick();
    }

    protected virtual void OnMinimizeButtonClick()
    {
        if (WindowState == WindowState.Normal)
        {
            var sb = new Storyboard();

            double targetTop = _actualTop + 200;
            var move = new DoubleAnimation
            {
                From = _actualTop,
                To = targetTop,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(move, this);
            Storyboard.SetTargetProperty(move, new PropertyPath("Top"));
            sb.Children.Add(move);

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fade, this);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(fade);

            sb.Completed += (_, _) =>
            {
                WindowState = WindowState.Minimized;
                _previousWindowState = WindowState.Minimized;
                UpdateLayout();
            };
            sb.Begin();
        }
        else if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Minimized;
            _previousWindowState = WindowState.Minimized;
            UpdateLayout();
        }
    }

    protected void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        OnMaximizeButtonClick();
    }

    protected virtual void OnMaximizeButtonClick()
    {
        var borderAround = FindBorderAround();

        if (WindowState == WindowState.Normal)
        {
            if (borderAround != null)
            {
                MaxWidth = SystemParameters.WorkArea.Width + (borderAround.BorderThickness.Left + borderAround.BorderThickness.Right) + 1;
                MaxHeight = SystemParameters.WorkArea.Height + (borderAround.BorderThickness.Top + borderAround.BorderThickness.Bottom) + 1;
            }
            else
            {
                MaxWidth = SystemParameters.WorkArea.Width;
                MaxHeight = SystemParameters.WorkArea.Height;
            }

            WindowState = WindowState.Maximized;
            _previousWindowState = WindowState.Maximized;
            UpdateLayout();
        }
        else
        {
            WindowState = WindowState.Normal;
            _previousWindowState = WindowState.Normal;
            UpdateLayout();
        }
    }

    private System.Windows.Controls.Border? FindBorderAround()
    {
        if (Content is FrameworkElement content)
        {
            return content.FindName("BorderAround") as System.Windows.Controls.Border;
        }
        return null;
    }

    protected void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        OnCloseButtonClick();
    }

    protected virtual void OnCloseButtonClick()
    {
        Close();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && _previousWindowState == WindowState.Minimized)
        {
            var sb = new Storyboard();

            double targetTop = _actualTop + 200;
            var moveBack = new DoubleAnimation
            {
                From = targetTop,
                To = _actualTop,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(moveBack, this);
            Storyboard.SetTargetProperty(moveBack, new PropertyPath("Top"));
            sb.Children.Add(moveBack);

            var fadeBack = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeBack, this);
            Storyboard.SetTargetProperty(fadeBack, new PropertyPath("Opacity"));
            sb.Children.Add(fadeBack);

            sb.Completed += (_, _) =>
            {
                WindowState = WindowState.Normal;
                _previousWindowState = WindowState.Normal;
                UpdateLayout();
            };

            sb.Begin();
        }
        else if (WindowState == WindowState.Maximized && _previousWindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Maximized;
            _previousWindowState = WindowState.Maximized;
            UpdateLayout();
        }
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _previousWindowState = WindowState;

        var dpiInfo = VisualTreeHelper.GetDpi(this);
        double dpiScaleY = dpiInfo.DpiScaleY;
        Point currentPos = PointToScreen(new Point(0, 0));
        _actualTop = currentPos.Y / dpiScaleY;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (UseCustomClose)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(OnCloseButtonClick));
        }
    }
}
