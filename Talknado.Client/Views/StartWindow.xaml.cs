using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class StartWindow : TalknadoWindow, IDisposable
{
    public static string ApplicationTitle { get; } =
        $"Talknado v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    public StartWindow()
    {
        // Своя анимация свёртывания требует AllowsTransparency, которого у окна нет.
        UseSystemCaption = true;
        UseStateAnimations = false;

        InitializeComponent();

        var startWindowViewModel = new StartWindowViewModel();
        startWindowViewModel.SetDispatcher(Dispatcher);
        DataContext = startWindowViewModel;

        startWindowViewModel.PropertyChanged += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(startWindowViewModel.IsVisible))
                {
                    if (startWindowViewModel.IsVisible)
                    {
                        Show();
                        Activate();
                    }
                    else
                        Hide();
                }
                else if (e.PropertyName == nameof(startWindowViewModel.IsServerTabSelected))
                {
                    startWindowViewModel.ErrorMessage = string.Empty;
                }
            });
        };
    }

    // TabItem и TabControl по щелчку переносят фокус в первое поле вкладки,
    // поэтому вкладка выбирается здесь, а фокус снимается до смены.
    private void ModeTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement((ItemsControl)sender, source) is not TabItem tab)
            return;

        e.Handled = true;
        if (tab.IsSelected)
            return;

        if (((UIElement)sender).IsKeyboardFocusWithin)
            Keyboard.ClearFocus();
        tab.IsSelected = true;
    }

    /// <summary>
    /// Каретка в конец подставленного ника. По <c>GotKeyboardFocus</c>, а не <c>Loaded</c>:
    /// фокус приходит и позже, например при щелчке по уже выбранной вкладке.
    /// </summary>
    private void PrefilledInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.CaretIndex = box.Text.Length;
    }

    protected override void OnMaximizeButtonClick()
    {
        return;
    }

    protected override void OnCloseButtonClick()
    {
        Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
    }

    public void Dispose()
    {
        UseCustomClose = false;

        try
        {
            Application.Current?.Dispatcher.Invoke(Close);
        }
        catch { /* ignore */ }

        GC.SuppressFinalize(this);
    }
}