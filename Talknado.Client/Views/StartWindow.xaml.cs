using System.Windows;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class StartWindow : TalknadoWindow
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
}