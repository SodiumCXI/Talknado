using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public interface IScreenShareWindow
{
    void Show();
    void Hide();
    void Close();
}

public partial class ScreenShareWindow : Window, IScreenShareWindow, IDisposable
{
    private double _actualTop;
    private WindowState _previousWindowState;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    public ScreenShareWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ScreenShareViewModel oldVm &&
            oldVm.ScreenSharePlayer is INotifyPropertyChanged oldNotifier)
        {
            PropertyChangedEventManager.RemoveHandler(oldNotifier, OnPlayerPropertyChanged, "");
        }

        if (e.NewValue is ScreenShareViewModel newVm &&
            newVm.ScreenSharePlayer is INotifyPropertyChanged newNotifier)
        {
            PropertyChangedEventManager.AddHandler(newNotifier, OnPlayerPropertyChanged, nameof(IScreenSharePlayer.IsWindowVisible));
            UpdateVisibility(newVm.ScreenSharePlayer.IsWindowVisible);
        }
    }

    private void OnPlayerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IScreenSharePlayer.IsWindowVisible) &&
            DataContext is ScreenShareViewModel vm)
        {
            UpdateVisibility(vm.ScreenSharePlayer.IsWindowVisible);
        }
    }

    private void UpdateVisibility(bool isVisible)
    {
        if (isVisible)
            Show();
        else
            Hide();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
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

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
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

            // Анимация прозрачности
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
        }
    }
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        var ssvm = DataContext as ScreenShareViewModel;
        ssvm!.ScreenSharePlayer.IsWindowVisible = false;
    }
    private void ToggleMaximizeRestore()
    {
        if (WindowState == WindowState.Normal)
        {
            MaxWidth = SystemParameters.WorkArea.Width + (BorderAround.BorderThickness.Left + BorderAround.BorderThickness.Right);
            MaxHeight = SystemParameters.WorkArea.Height + (BorderAround.BorderThickness.Top + BorderAround.BorderThickness.Bottom);

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

    private void ScreenShareWindow_StateChanged(object sender, EventArgs e)
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
            var sb = new Storyboard();

            var fadeBack = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeBack, this);
            Storyboard.SetTargetProperty(fadeBack, new PropertyPath("Opacity"));
            sb.Children.Add(fadeBack);

            sb.Completed += (_, _) => _previousWindowState = WindowState.Maximized;

            sb.Begin();
        }
    }

    private void ScreenShareWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _previousWindowState = WindowState;

        var dpiInfo = VisualTreeHelper.GetDpi(this);
        double dpiScaleY = dpiInfo.DpiScaleY;
        Point currentPos = PointToScreen(new Point(0, 0));
        _actualTop = currentPos.Y / dpiScaleY;
    }

    public void Dispose()
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(Close);
        }
        catch { /* ignore */ }

        GC.SuppressFinalize(this);
    }
}
