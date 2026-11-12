using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Talknado.Client.Models;
using Talknado.Client.Properties.Localization;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class ScreenShareWindow : TalknadoWindow, IDisposable
{
    // Сколько мышь стоит, прежде чем во весь экран спрячутся заголовок и курсор
    private static readonly TimeSpan IdleHideDelay = TimeSpan.FromSeconds(2.5);

    private readonly DispatcherTimer _idleTimer = new(DispatcherPriority.Background) { Interval = IdleHideDelay };
    private Point _lastMousePosition;
    private Point? _dragStart;

    public ScreenShareWindow()
    {
        UseSystemCaption = true;
        UseStateAnimations = false;

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        _idleTimer.Tick += (_, _) => HideControls();

        TitleBar.MouseLeftButtonDown += BeginDragOutOfFullScreen;
        TitleBar.MouseMove += DragOutOfFullScreen;
        TitleBar.MouseLeftButtonUp += (_, _) => EndDragOutOfFullScreen();
    }

    protected override void OnMaximizeButtonClick() => SetFullScreen(!IsFullScreen);

    /// <summary>
    /// Во весь экран заголовок окно не тянет, но перетаскивание за него выводит
    /// в обычное окно — как у развёрнутых окон Windows: окно встаёт под курсор на
    /// ту же долю ширины заголовка и дальше едет за мышью, с прилипанием к краям.
    /// Кнопки и громкость нажатие забирают себе, сюда оно не доходит.
    /// </summary>
    private void BeginDragOutOfFullScreen(object sender, MouseButtonEventArgs e)
    {
        if (!IsFullScreen || !TitleBar.CaptureMouse())
            return;

        _dragStart = e.GetPosition(TitleBar);
        e.Handled = true;
    }

    private void DragOutOfFullScreen(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;

        var position = e.GetPosition(TitleBar);
        if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        double share = position.X / TitleBar.ActualWidth;
        var cursor = PointToScreen(position);
        EndDragOutOfFullScreen();
        SetFullScreen(false);

        var toDip = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
        var cursorDip = toDip.Transform(cursor);
        Left = cursorDip.X - share * Width;
        Top = cursorDip.Y - position.Y;
        DragMove();
    }

    private void EndDragOutOfFullScreen()
    {
        _dragStart = null;
        if (TitleBar.IsMouseCaptured)
            TitleBar.ReleaseMouseCapture();
    }

    /// <summary>
    /// Развёрнутым окно демонстрации не бывает: разворот любым путём — двойным
    /// щелчком по заголовку, Win+↑, к верхнему краю — уводит во весь экран.
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Maximized && !IsFullScreen)
            Dispatcher.BeginInvoke(() => SetFullScreen(true));
    }

    private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !TitleBar.IsMouseOver)
            SetFullScreen(!IsFullScreen);
    }

    private void SetFullScreen(bool value)
    {
        IsFullScreen = value;

        // Во весь экран картинка уходит и под заголовок: иначе при его скрытии
        // и показе она меняла бы размер.
        Grid.SetRow(VideoImage, value ? 0 : 1);
        Grid.SetRowSpan(VideoImage, value ? 2 : 1);

        // Во весь экран сразу только картинка: заголовок вернёт движение мыши
        ShowControls();
        if (value)
            HideControls();
        else
            _idleTimer.Stop();
    }

    private void ShowControls()
    {
        TitleBar.Visibility = Visibility.Visible;
        ClearValue(CursorProperty);
        if (IsFullScreen)
        {
            _idleTimer.Stop();
            _idleTimer.Start();
        }
    }

    private void HideControls()
    {
        _idleTimer.Stop();
        if (!IsFullScreen)
            return;

        // Над заголовком мышь стоит, пока им пользуются: громкость, кнопки
        if (TitleBar.IsMouseOver)
        {
            _idleTimer.Start();
            return;
        }

        TitleBar.Visibility = Visibility.Collapsed;
        Cursor = Cursors.None;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        // MouseMove приходит и без движения: когда под курсором меняется элемент,
        // в том числе от скрытия заголовка, и когда окно встаёт на весь экран.
        // Движение — только смена места курсора на экране.
        var position = PointToScreen(e.GetPosition(this));
        if (position == _lastMousePosition)
            return;

        _lastMousePosition = position;
        if (IsFullScreen)
            ShowControls();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11)
            SetFullScreen(!IsFullScreen);
        else if (e.Key == Key.Escape && IsFullScreen)
            SetFullScreen(false);
        else
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
    }

    protected override void OnCloseButtonClick()
    {
        if (DataContext is ScreenShareViewModel ssvm)
        {
            ssvm.ScreenSharePlayer.IsWindowVisible = false;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ScreenShareViewModel oldVm &&
            oldVm.ScreenSharePlayer is INotifyPropertyChanged oldNotifier)
        {
            PropertyChangedEventManager.RemoveHandler(oldNotifier, OnPlayerPropertyChanged!, "");
        }

        if (e.NewValue is ScreenShareViewModel newVm &&
            newVm.ScreenSharePlayer is INotifyPropertyChanged newNotifier)
        {
            PropertyChangedEventManager.AddHandler(newNotifier, OnPlayerPropertyChanged!, nameof(IScreenSharePlayer.IsWindowVisible));
            UpdateVisibility(newVm.ScreenSharePlayer.IsWindowVisible, newVm);
        }
    }

    private void OnPlayerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IScreenSharePlayer.IsWindowVisible) &&
            DataContext is ScreenShareViewModel vm)
        {
            UpdateVisibility(vm.ScreenSharePlayer.IsWindowVisible, vm);
        }
    }

    private async void UpdateVisibility(bool isVisible, ScreenShareViewModel vm)
    {
        if (isVisible)
        {
            if (vm.UsersInfo.IsWaitingForScreenShareWindow)
                return;

            _ = WaitForKeyFrameAndShowAsync(vm);
        }
        else
        {
            SetFullScreen(false);
            vm.ScreenSharePlayer.IsKeyFrameInitialized = false;
            vm.ScreenSharePlayer.Clear();
            await Task.Delay(50).ContinueWith(_ => Application.Current.Dispatcher.Invoke(Hide));
        }
    }

    private async Task WaitForKeyFrameAndShowAsync(ScreenShareViewModel vm)
    {
        vm.UsersInfo.IsWaitingForScreenShareWindow = true;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            // Окно показывается с уже нарисованным кадром, а не с пустым или прежним.
            while (!vm.ScreenSharePlayer.HasFrame)
            {
                await Task.Delay(50, cts.Token);
            }
            Application.Current?.Dispatcher.Invoke(Show);
        }
        catch (OperationCanceledException)
        {
            if (vm.ScreenSharePlayer.IsKeyFrameInitialized)
            {
                Application.Current?.Dispatcher.Invoke(Show);
            }
            else if (vm.ScreenSharePlayer.HasLastKeyFrame)
            {
                Application.Current?.Dispatcher.Invoke(Show);
                vm.ScreenSharePlayer.UpdateSavedKeyFrame();
            }
            else
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(Strings.NoKeyframeReceivedText, Strings.ScreenDisplayErrorText,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    vm.ScreenSharePlayer.IsWindowVisible = false;
                });
            }
        }
        finally
        {
            vm.UsersInfo.IsWaitingForScreenShareWindow = false;
            cts.Dispose();
        }
    }

    protected override void OnMinimizeButtonClick() { }

    public void Dispose()
    {
        UseCustomClose = false;
        _idleTimer.Stop();

        try
        {
            Application.Current?.Dispatcher.Invoke(Close);
        }
        catch { /* ignore */ }

        GC.SuppressFinalize(this);
    }
}