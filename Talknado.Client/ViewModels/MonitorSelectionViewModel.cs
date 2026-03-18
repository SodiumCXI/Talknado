using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using Talknado.Client.Models;

namespace Talknado.Client.ViewModels;

public partial class MonitorSelectionViewModel(IClientManager clientManager, IScreenMonitorManager screenMonitorManager) : ObservableObject
{
    private readonly IClientManager _clientManager = clientManager;
    private readonly IScreenMonitorManager _screenMonitorManager = screenMonitorManager;
    public IScreenMonitorManager ScreenMonitorManager => _screenMonitorManager;

    [RelayCommand]
    private void SelectMonitor(ScreenMonitorManager.MonitorSnapshot snapshot)
    {
        _screenMonitorManager.SelectedMonitor = snapshot;
        _clientManager.ToggleScreenShare();
        _screenMonitorManager.IsWindowVisible = false;
    }

    public void LoadMonitors()
    {
        _screenMonitorManager.CaptureAll();
    }
}