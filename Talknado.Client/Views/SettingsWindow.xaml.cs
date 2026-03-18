using System.ComponentModel;
using System.Windows;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class SettingsWindow : TalknadoWindow, IDisposable
{
    public SettingsWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnCloseButtonClick()
    {
        if (DataContext is SettingsWindowViewModel ssvm)
        {
            ssvm.SettingsManager.IsWindowVisible = false;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsWindowViewModel oldVm &&
            oldVm.SettingsManager is INotifyPropertyChanged oldNotifier)
        {
            PropertyChangedEventManager.RemoveHandler(oldNotifier, OnPlayerPropertyChanged!, "");
        }

        if (e.NewValue is SettingsWindowViewModel newVm &&
            newVm.SettingsManager is INotifyPropertyChanged newNotifier)
        {
            PropertyChangedEventManager.AddHandler(newNotifier, OnPlayerPropertyChanged!, nameof(ISettingsManager.IsWindowVisible));
            UpdateVisibility(newVm.SettingsManager.IsWindowVisible, newVm);
        }
    }

    private void OnPlayerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ISettingsManager.IsWindowVisible) &&
            DataContext is SettingsWindowViewModel vm)
        {
            UpdateVisibility(vm.SettingsManager.IsWindowVisible, vm);
        }
    }

    private void UpdateVisibility(bool isVisible, SettingsWindowViewModel vm)
    {
        if (isVisible)
        {
            vm.SettingsManager.LoadAudioDevices();
            Show();
        }
        else
            Hide();
    }

    protected override void OnMinimizeButtonClick() { }

    protected override void OnMaximizeButtonClick() { }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (DataContext is SettingsWindowViewModel ssvm)
        {
            ssvm.SettingsManager.IsWindowVisible = false;
        }
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
