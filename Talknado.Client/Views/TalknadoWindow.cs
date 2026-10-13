using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Talknado.Client.Views;

public partial class TalknadoWindow : Window
{
    private double _actualTop;
    private WindowState _previousWindowState;

    protected bool UseCustomClose { get; set; } = true;

    /// <summary>
    /// Своя анимация свёртывания требует Window.Opacity, а он работает только
    /// при AllowsTransparency.
    /// </summary>
    protected bool UseStateAnimations { get; set; } = true;

    /// <summary>
    /// Окна, где заголовок отдан системе через WindowChrome с ненулевым
    /// CaptionHeight: перетаскивание, разворот и прилипание делает Windows.
    /// </summary>
    protected bool UseSystemCaption { get; set; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    public TalknadoWindow()
    {
        Loaded += OnWindowLoaded;
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;
        SourceInitialized += OnSourceInitialized;

        // Свернули или переключились на другое окно — указатель уже не над
        // кнопкой, но MouseLeave не приходит. Пересчитываем положение мыши.
        Activated += (_, _) => ResyncMouse();
        Deactivated += (_, _) => ResyncMouse();
    }

    /// <summary>
    /// У развёрнутого окна с WS_CAPTION рамка изменения размера остаётся
    /// за пределами рабочей области, а WindowChrome отдаёт её содержимому —
    /// без поправки края уезжают за экран. Обычные окна Windows ведут себя
    /// так же, просто у них эта рамка нерабочая и невидимая.
    /// </summary>
    private void ApplyMaximizedPadding()
    {
        if (Content is not FrameworkElement root)
            return;

        if (WindowState != WindowState.Maximized)
        {
            root.Margin = default;
            return;
        }

        var padded = GetSystemMetrics(SM_CXPADDEDBORDER);
        double x = GetSystemMetrics(SM_CXSIZEFRAME) + padded;
        double y = GetSystemMetrics(SM_CYSIZEFRAME) + padded;
        root.Margin = new Thickness(x, y, x, y);
    }

    private void ResyncMouse()
    {
        if (UseStateAnimations)
            return;

        Dispatcher.BeginInvoke(new Action(UpdateTitleBarHover), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Подсветка кнопок заголовка считается по настоящему положению курсора.
    /// IsMouseOver для этого не годится: при свёртывании окно перестаёт
    /// получать сообщения мыши, MouseLeave не приходит, и кнопка остаётся
    /// гореть после восстановления.
    /// </summary>
    private void UpdateTitleBarHover()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            SetHover(_minimizeButton, false);
            SetHover(_maximizeButton, false);
            SetHover(_closeButton, false);
            return;
        }

        GetCursorPos(out var cursor);

        SetHover(_minimizeButton, IsUnder(_minimizeButton, cursor));
        SetHover(_maximizeButton, IsUnder(_maximizeButton, cursor));
        SetHover(_closeButton, IsUnder(_closeButton, cursor));
    }

    private bool IsUnder(System.Windows.Controls.Button? button, POINT cursor)
    {
        if (button is null || !button.IsVisible)
            return false;

        try
        {
            var topLeft = button.PointToScreen(new Point(0, 0));
            var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
            return cursor.X >= topLeft.X && cursor.X < bottomRight.X
                && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void SetHover(System.Windows.Controls.Button? button, bool isHovered)
    {
        if (button is null)
            return;

        var value = isHovered ? "hover" : null;
        if (!Equals(button.Tag, value))
            button.Tag = value;
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
        if (!UseStateAnimations)
        {
            WindowState = WindowState.Minimized;
            _previousWindowState = WindowState.Minimized;
            return;
        }

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
        if (UseSystemCaption)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            _previousWindowState = WindowState;
            return;
        }

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
        if (!UseStateAnimations)
        {
            _previousWindowState = WindowState;

            ApplyMaximizedPadding();

            // После свёртывания и разворота указатель обычно уже не над кнопкой,
            // но события ухода мыши не приходит: пересчитываем подсветку сами.
            ResyncMouse();
            return;
        }

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
        _minimizeButton = FindTitleBarButton("MinimizeButton");
        _maximizeButton = FindTitleBarButton("MaximizeButton");
        _closeButton = FindTitleBarButton("CloseButton");

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

    // ---------- Системный заголовок ----------

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSELEAVE = 0x02A3;
    private const int HTMAXBUTTON = 9;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    private const uint TME_LEAVE = 0x00000002;
    private const uint TME_NONCLIENT = 0x00000010;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);




    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    private System.Windows.Controls.Button? _minimizeButton;
    private System.Windows.Controls.Button? _maximizeButton;
    private System.Windows.Controls.Button? _closeButton;
    private bool _isTrackingNonClientMouse;

    private System.Windows.Controls.Button? FindTitleBarButton(string name) =>
        (Content as FrameworkElement)?.FindName(name) as System.Windows.Controls.Button;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WindowProc);

        // Пиксель рамки Windows внизу окна (GlassFrameThickness) даёт окну тень.
        // При изменении размера содержимое на мгновение отстаёт, и рамка видна
        // на новом краю — у Windows 10 по умолчанию она белая. Тёмный режим
        // рамки красит её в тёмный. 20 — номер атрибута с Windows 10 2004,
        // у сборок 1809–1909 тот же атрибут был под номером 19.
        int darkFrame = 1;
        if (DwmSetWindowAttribute(source.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFrame, sizeof(int)) != 0)
            DwmSetWindowAttribute(source.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkFrame, sizeof(int));

        // Этим цветом WPF заливает окно до первой отрисовки содержимого;
        // по умолчанию он белый.
        if (Background is SolidColorBrush background)
            source.CompositionTarget.BackgroundColor = background.Color;

        if (!UseSystemCaption)
            return;

        // Без WS_CAPTION Windows не анимирует свёртывание и разворот.
        var style = GetWindowLong(source.Handle, GWL_STYLE);
        SetWindowLong(source.Handle, GWL_STYLE, style | WS_CAPTION);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_GETMINMAXINFO:
                // Без этого развёрнутое окно накрывает панель задач.
                // Считается монитор, на котором окно сейчас, а не основной.
                // handled обязателен: иначе WindowChrome обработает сообщение
                // следом и перезапишет размеры своими, с запасом на рамку.
                ApplyWorkArea(hwnd, lParam);
                handled = true;
                break;

            case WM_NCHITTEST when UseSystemCaption:
                // Раскладки Windows 11 появляются, только если кнопка
                // разворота отвечает HTMAXBUTTON на проверку попадания.
                var overMaximize = HitTestMaximizeButton(lParam);
                UpdateTitleBarHover();
                if (overMaximize)
                {
                    TrackNonClientMouseLeave(hwnd);
                    handled = true;
                    return HTMAXBUTTON;
                }
                break;

            case WM_NCMOUSELEAVE when UseSystemCaption:
                _isTrackingNonClientMouse = false;
                UpdateTitleBarHover();
                break;


            case WM_MOUSEMOVE when UseSystemCaption:
            case WM_MOUSELEAVE when UseSystemCaption:
                UpdateTitleBarHover();
                break;

            case WM_NCLBUTTONDOWN when UseSystemCaption && wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                break;

            case WM_NCLBUTTONUP when UseSystemCaption && wParam.ToInt32() == HTMAXBUTTON:
                // Щелчок пришёл в нерабочую область, обработчик Click не сработает
                OnMaximizeButtonClick();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }



    private void ApplyWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        // Размер развёрнутого окна — по рабочей области монитора, на котором окно сейчас.
        // Вместе с DWM-рамкой (GlassFrameThickness в разметке) система берёт эти значения
        // как есть, поправка на рамку не нужна.
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition.X = info.WorkArea.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = info.WorkArea.Top - info.Monitor.Top;
        mmi.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
        mmi.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;
        ApplyMinTrackSize(ref mmi);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    /// <summary>
    /// MinWidth и MinHeight окна WPF применяет в своём обработчике этого же
    /// сообщения. Хук выше объявляет его обработанным, поэтому очередь до WPF
    /// не доходит, и без этой строки окно сжимается до нуля. Значения идут
    /// в пикселях устройства, а свойства окна заданы в аппаратно-независимых.
    /// </summary>
    private void ApplyMinTrackSize(ref MINMAXINFO mmi)
    {
        var transform = (PresentationSource.FromVisual(this) as HwndSource)?
            .CompositionTarget?.TransformToDevice ?? Matrix.Identity;

        if (MinWidth > 0 && !double.IsInfinity(MinWidth))
            mmi.MinTrackSize.X = (int)Math.Ceiling(MinWidth * transform.M11);

        if (MinHeight > 0 && !double.IsInfinity(MinHeight))
            mmi.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * transform.M22);
    }

    private bool HitTestMaximizeButton(IntPtr lParam)
    {
        if (_maximizeButton is null || !_maximizeButton.IsVisible)
            return false;

        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));

        var topLeft = _maximizeButton.PointToScreen(new Point(0, 0));
        var bottomRight = _maximizeButton.PointToScreen(
            new Point(_maximizeButton.ActualWidth, _maximizeButton.ActualHeight));

        return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
    }

    /// <summary>
    /// WM_NCMOUSELEAVE не приходит сам по себе: его нужно заказать.
    /// Без заказа подсветка кнопки разворота остаётся гореть после ухода мыши.
    /// </summary>
    private void TrackNonClientMouseLeave(IntPtr hwnd)
    {
        if (_isTrackingNonClientMouse)
            return;

        var track = new TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE | TME_NONCLIENT,
            hwndTrack = hwnd,
            dwHoverTime = 0
        };

        _isTrackingNonClientMouse = TrackMouseEvent(ref track);
    }

}
