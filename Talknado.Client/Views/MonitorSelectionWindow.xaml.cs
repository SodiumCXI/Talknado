using System.ComponentModel;
using System.Windows;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class MonitorSelectionWindow : TalknadoWindow, IDisposable
{
    public MonitorSelectionWindow()
    {
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
            ResizeToFitMonitors(vm);
            Show();
        }
        else
        {
            vm.ScreenMonitorManager.ClearMonitors();
            await Task.Delay(50).ContinueWith(_ => Application.Current.Dispatcher.Invoke(Hide));
        }
    }

    private void ResizeToFitMonitors(MonitorSelectionViewModel vm)
    {
        var monitors = vm.ScreenMonitorManager.Monitors;
        if (monitors.Count == 0) return;

        const double previewHeight = 176;
        const double itemMargin = 10;
        const double outerMargin = 10;
        const double padding = 42;

        int count = monitors.Count;
        int cols = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling((double)count / cols);

        vm.ScreenMonitorManager.GridColumns = cols;

        double maxAspect = 0;
        for (int i = 0; i < cols && i < count; i++)
            maxAspect = Math.Max(maxAspect, monitors[i].AspectRatio);

        double cellWidth = previewHeight * maxAspect + itemMargin * 2;
        double totalWidth = outerMargin * 2 + cellWidth * cols;
        double totalHeight = (padding + (previewHeight + itemMargin * 2) * rows) - itemMargin / 2 * (rows - 1);

        MinWidth = totalWidth;
        MinHeight = totalHeight;
        Width = totalWidth;
        Height = totalHeight;
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