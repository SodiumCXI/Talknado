using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using Talknado.Client.Models;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.ViewModels;

public partial class MonitorSelectionViewModel : ObservableObject
{
    private readonly IClientManager _clientManager;
    private readonly IScreenMonitorManager _screenMonitorManager;
    public IScreenMonitorManager ScreenMonitorManager => _screenMonitorManager;
    public ISettingsManager SettingsManager { get; }

    // Поле выбора в шаблоне темы показывает ToString, а не DisplayMemberPath.
    public record QualityOption(string Text, bool IsAvailable)
    {
        public override string ToString() => Text;
    }

    public MonitorSelectionViewModel(IClientManager clientManager, IScreenMonitorManager screenMonitorManager, ISettingsManager settingsManager)
    {
        _clientManager = clientManager;
        _screenMonitorManager = screenMonitorManager;
        SettingsManager = settingsManager;

        if (screenMonitorManager is INotifyPropertyChanged notifier)
            notifier.PropertyChanged += OnManagerPropertyChanged;
    }

    /// <summary>Выше потолка сервера и возможностей этой машины варианты недоступны.</summary>
    public QualityOption[] QualityOptions =>
    [
        new(Strings.ShareQualityLowText, true),
        new(Strings.ShareQualityMediumText, _screenMonitorManager.AvailableQuality >= 1),
        new(Strings.ShareQualityHighText, _screenMonitorManager.AvailableQuality >= 2)
    ];

    public int SelectedQuality
    {
        get => Math.Min(_screenMonitorManager.SelectedQuality, _screenMonitorManager.AvailableQuality);
        set
        {
            // -1 приходит, пока список вариантов пересоздаётся
            if (value >= 0 && value <= _screenMonitorManager.AvailableQuality)
                _screenMonitorManager.SelectedQuality = value;
        }
    }

    private void OnManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IScreenMonitorManager.AvailableQuality))
            return;

        OnPropertyChanged(nameof(QualityOptions));
        OnPropertyChanged(nameof(SelectedQuality));
    }

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
