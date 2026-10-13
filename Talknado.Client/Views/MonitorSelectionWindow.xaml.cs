using System.ComponentModel;
using System.Windows;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class MonitorSelectionWindow : TalknadoWindow, IDisposable
{
    public MonitorSelectionWindow()
    {
        // Своя анимация свёртывания требует AllowsTransparency, которого у окна нет.
        UseSystemCaption = true;
        UseStateAnimations = false;

        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnCloseButtonClick()
    {
        if (DataContext is MonitorSelectionViewModel vm)
            vm.ScreenMonitorManager.IsWindowVisible = false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MonitorSelectionViewModel oldVm &&
            oldVm.ScreenMonitorManager is INotifyPropertyChanged oldNotifier)
        {
            PropertyChangedEventManager.RemoveHandler(oldNotifier, OnManagerPropertyChanged!, "");
        }

        if (e.NewValue is MonitorSelectionViewModel newVm &&
            newVm.ScreenMonitorManager is INotifyPropertyChanged newNotifier)
        {
            PropertyChangedEventManager.AddHandler(newNotifier, OnManagerPropertyChanged!,
                nameof(IScreenMonitorManager.IsWindowVisible));
            UpdateVisibility(newVm.ScreenMonitorManager.IsWindowVisible, newVm);
        }
    }

    private void OnManagerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IScreenMonitorManager.IsWindowVisible) &&
            DataContext is MonitorSelectionViewModel vm)
            UpdateVisibility(vm.ScreenMonitorManager.IsWindowVisible, vm);
    }

    private async void UpdateVisibility(bool isVisible, MonitorSelectionViewModel vm)
    {
        if (isVisible)
        {
            vm.ScreenMonitorManager.BuildPreviews();
            SetGridColumns(vm);
            Show();
        }
        else
        {
            vm.ScreenMonitorManager.ClearMonitors();
            await Task.Delay(50).ContinueWith(_ => Application.Current.Dispatcher.Invoke(Hide));
        }
    }

    /// <summary>
    /// Мониторы раскладываются квадратной сеткой: три в ряд при девяти, два при трёх и четырёх.
    /// </summary>
    private static void SetGridColumns(MonitorSelectionViewModel vm)
    {
        int count = vm.ScreenMonitorManager.Monitors.Count;
        if (count == 0) return;

        vm.ScreenMonitorManager.GridColumns = (int)Math.Ceiling(Math.Sqrt(count));
    }

    protected override void OnMinimizeButtonClick() { }

    protected override void OnMaximizeButtonClick() { }

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