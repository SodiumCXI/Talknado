using System.ComponentModel;
using System.Windows;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class ScreenShareWindow : TalknadoWindow, IDisposable
{
    public ScreenShareWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
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