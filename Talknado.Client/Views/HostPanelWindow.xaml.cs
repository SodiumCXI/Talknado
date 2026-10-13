using System.ComponentModel;
using System.Windows;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class HostPanelWindow : TalknadoWindow, IDisposable
{
    public HostPanelWindow()
    {
        UseSystemCaption = true;
        UseStateAnimations = false;

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    // Значение применяется по уходу фокуса; Enter применяет его, не уводя фокус.
    private void BandwidthBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    protected override void OnCloseButtonClick()
    {
        if (DataContext is HostPanelViewModel vm)
        {
            vm.HostPanelManager.IsWindowVisible = false;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is HostPanelViewModel oldVm &&
            oldVm.HostPanelManager is INotifyPropertyChanged oldNotifier)
        {
            PropertyChangedEventManager.RemoveHandler(oldNotifier, OnManagerPropertyChanged!, "");
        }

        if (e.NewValue is HostPanelViewModel newVm &&
            newVm.HostPanelManager is INotifyPropertyChanged newNotifier)
        {
            PropertyChangedEventManager.AddHandler(newNotifier, OnManagerPropertyChanged!, nameof(IHostPanelManager.IsWindowVisible));
            UpdateVisibility(newVm.HostPanelManager.IsWindowVisible, newVm);
        }
    }

    private void OnManagerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IHostPanelManager.IsWindowVisible) &&
            DataContext is HostPanelViewModel vm)
        {
            UpdateVisibility(vm.HostPanelManager.IsWindowVisible, vm);
        }
    }

    private void UpdateVisibility(bool isVisible, HostPanelViewModel vm)
    {
        if (isVisible)
        {
            vm.StartRefresh();
            Show();
        }
        else
        {
            vm.StopRefresh();
            Hide();
        }
    }

    protected override void OnMinimizeButtonClick() { }

    protected override void OnMaximizeButtonClick() { }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (DataContext is HostPanelViewModel vm)
        {
            vm.HostPanelManager.IsWindowVisible = false;
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
