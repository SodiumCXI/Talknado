using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class MediaViewerWindow : TalknadoWindow
{
    private const double SeekStepSeconds = 5;

    // Доля рабочей области монитора, которую окно занимает при открытии.
    private const double InitialScreenShare = 0.75;

    public MediaViewerWindow()
    {
        // Своя анимация свёртывания требует AllowsTransparency, которого у окна нет.
        UseSystemCaption = true;
        UseStateAnimations = false;

        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Щелчок по дорожке ползунок обрабатывает сам и помечает событие
        // обработанным: без handledEventsToo начало перемотки не отмечалось бы,
        // и таймер позиции возвращал бы бегунок на место.
        PositionSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, _) => ViewModel?.BeginSeek()), true);
        PositionSlider.AddHandler(PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) => ViewModel?.EndSeek()), true);
    }

    private MediaViewerViewModel? ViewModel => DataContext as MediaViewerViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MediaViewerViewModel { Manager: INotifyPropertyChanged oldNotifier })
            PropertyChangedEventManager.RemoveHandler(oldNotifier, OnManagerPropertyChanged!, nameof(IMediaViewerManager.IsWindowVisible));

        if (e.NewValue is MediaViewerViewModel { Manager: INotifyPropertyChanged newNotifier })
            PropertyChangedEventManager.AddHandler(newNotifier, OnManagerPropertyChanged!, nameof(IMediaViewerManager.IsWindowVisible));
    }

    private void OnManagerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        if (vm.Manager.IsWindowVisible)
            ShowViewer(vm);
        else
            HideViewer(vm);
    }

    private void ShowViewer(MediaViewerViewModel vm)
    {
        var owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        var (workArea, pixelWidth, pixelHeight) = GetMonitor(owner);

        if (IsVisible)
            vm.OnClosed();
        else
            PlaceOn(workArea, vm.Manager.Current);

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();

        vm.OnOpened(pixelWidth, pixelHeight);
    }

    /// <summary>Окно по пропорциям вложения, на мониторе главного окна, по центру.</summary>
    private void PlaceOn(Rect workArea, Attachment? attachment)
    {
        double maxWidth = workArea.Width * InitialScreenShare;
        double maxHeight = workArea.Height * InitialScreenShare;
        double width = maxWidth, height = maxHeight;

        // Счёт в пикселях экрана. Высота подбирается так, чтобы ширина по
        // пропорции вышла целым числом пикселей (у 16:9 — высота, кратная 9):
        // при любом дробном остатке разметка округляет картинку, и сверху или
        // сбоку остаётся пиксель фона. Нет такой высоты в пределах 64 пикселей —
        // берётся исходная.
        if (attachment is { PixelWidth: > 0, PixelHeight: > 0 })
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double chrome = Math.Round(TitleBar.Height * dpi.DpiScaleY);
            if (attachment.IsVideo)
            {
                ControlBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                chrome += Math.Round(ControlBar.DesiredSize.Height * dpi.DpiScaleY);
            }
            double scale = Math.Min(maxWidth * dpi.DpiScaleX / attachment.PixelWidth,
                (maxHeight * dpi.DpiScaleY - chrome) / attachment.PixelHeight);
            scale = Math.Min(1, scale);
            double aspect = (double)attachment.PixelWidth / attachment.PixelHeight;
            double target = Math.Round(attachment.PixelHeight * scale);
            double mediaHeight = target;
            for (double h = target; h > target - 64 && h > 1; h--)
            {
                if (Math.Abs(h * aspect - Math.Round(h * aspect)) < 0.01)
                {
                    mediaHeight = h;
                    break;
                }
            }
            double mediaWidth = Math.Round(mediaHeight * aspect);
            width = Math.Max(MinWidth, mediaWidth / dpi.DpiScaleX);
            height = Math.Max(MinHeight, (mediaHeight + chrome) / dpi.DpiScaleY);
        }

        Width = width;
        Height = height;
        Left = workArea.Left + (workArea.Width - width) / 2;
        Top = workArea.Top + (workArea.Height - height) / 2;
    }

    private void HideViewer(MediaViewerViewModel vm)
    {
        vm.OnClosed();
        Hide();
    }

    protected override void OnCloseButtonClick()
    {
        if (ViewModel is { } vm)
            vm.Manager.IsWindowVisible = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Окно живёт всё время работы приложения и только прячется. При выходе
        // из приложения WPF отмену закрытия не учитывает.
        e.Cancel = true;
        if (ViewModel is { } vm)
            vm.Manager.IsWindowVisible = false;

        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.Manager.IsWindowVisible = false;
                break;
            case Key.Space:
                vm.TogglePlayCommand.Execute(null);
                break;
            case Key.Left:
                vm.SeekBy(-SeekStepSeconds);
                break;
            case Key.Right:
                vm.SeekBy(SeekStepSeconds);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Video_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.TogglePlayCommand.Execute(null);
    }

    private void MediaArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel is not { } vm || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;

        var size = target.TransformToDevice.Transform(new Point(e.NewSize.Width, e.NewSize.Height));
        vm.SetViewport((int)size.X, (int)size.Y);
    }

    /// <summary>Рабочая область монитора в единицах WPF и размер монитора в пикселях.</summary>
    private static (Rect WorkArea, int PixelWidth, int PixelHeight) GetMonitor(Window? reference)
    {
        var handle = reference != null ? new WindowInteropHelper(reference).Handle : IntPtr.Zero;
        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return (SystemParameters.WorkArea, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);

        var toDip = reference != null && PresentationSource.FromVisual(reference) is { CompositionTarget: { } target }
            ? target.TransformFromDevice
            : Matrix.Identity;

        var work = info.rcWork;
        var topLeft = toDip.Transform(new Point(work.Left, work.Top));
        var bottomRight = toDip.Transform(new Point(work.Right, work.Bottom));
        var screen = info.rcMonitor;
        return (new Rect(topLeft, bottomRight), screen.Right - screen.Left, screen.Bottom - screen.Top);
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
