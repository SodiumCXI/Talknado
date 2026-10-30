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
    public partial class QualityOption(string text) : ObservableObject
    {
        public string Text { get; } = text;

        [ObservableProperty]
        private bool _isAvailable;

        public override string ToString() => Text;
    }

    public MonitorSelectionViewModel(IClientManager clientManager, IScreenMonitorManager screenMonitorManager, ISettingsManager settingsManager)
    {
        _clientManager = clientManager;
        _screenMonitorManager = screenMonitorManager;
        SettingsManager = settingsManager;

        UpdateAvailability();
        if (screenMonitorManager is INotifyPropertyChanged notifier)
            notifier.PropertyChanged += OnManagerPropertyChanged;
    }

    /// <summary>
    /// Выше потолка сервера и возможностей этой машины варианты недоступны.
    /// Список не пересоздаётся: при смене ItemsSource поле выбора теряет
    /// выбранный элемент и остаётся пустым.
    /// </summary>
    public QualityOption[] QualityOptions { get; } =
    [
        new(Strings.ShareQualityLowText),
        new(Strings.ShareQualityMediumText),
        new(Strings.ShareQualityHighText)
    ];

    public int SelectedQuality
    {
        get => Math.Min(_screenMonitorManager.SelectedQuality, _screenMonitorManager.AvailableQuality);
        set
        {
            if (value >= 0 && value <= _screenMonitorManager.AvailableQuality)
                _screenMonitorManager.SelectedQuality = value;
        }
    }

    private void UpdateAvailability()
    {
        for (int i = 0; i < QualityOptions.Length; i++)
            QualityOptions[i].IsAvailable = i <= _screenMonitorManager.AvailableQuality;
    }

    private void OnManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IScreenMonitorManager.AvailableQuality))
            return;

        UpdateAvailability();
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
