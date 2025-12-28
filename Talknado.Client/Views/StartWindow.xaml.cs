using System.Windows;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class StartWindow : TalknadoWindow, IDisposable
{
    public StartWindow()
    {
        InitializeComponent();

        var startWindowViewModel = new StartWindowViewModel();
        DataContext = startWindowViewModel;

        startWindowViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(startWindowViewModel.IsVisible))
            {
                Visibility = startWindowViewModel.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        };
    }

    protected override void OnCloseButtonClick()
    {
        Application.Current.Shutdown();
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