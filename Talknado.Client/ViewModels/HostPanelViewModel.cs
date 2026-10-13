using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Threading;
using Talknado.Client.Models;
using Talknado.Client.Models.Helpers.ScreenShare;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.ViewModels;

public partial class HostPanelViewModel : ObservableObject
{
    private readonly IHostPanelManager _hostPanelManager;

    // Задержка и адрес меняются постоянно: опрос по таймеру и только пока окно
    // открыто, привязывать список к событиям сети напрямую нельзя.
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    public IHostPanelManager HostPanelManager => _hostPanelManager;

    public string[] QualityLevels { get; } =
    [
        Strings.ShareQualityLowText,
        Strings.ShareQualityMediumText,
        Strings.ShareQualityHighText
    ];

    // Неверный ввод не применяется: поле возвращается к действующему значению.
    public string FileBandwidthText
    {
        get => _hostPanelManager.FileBandwidthMbits.ToString(System.Globalization.CultureInfo.CurrentCulture);
        set
        {
            if (int.TryParse(value, out var mbits) && mbits >= 0)
                _hostPanelManager.FileBandwidthMbits = mbits;
            OnPropertyChanged();
        }
    }

    public HostPanelViewModel(IHostPanelManager hostPanelManager)
    {
        _hostPanelManager = hostPanelManager;
        _refreshTimer.Tick += (_, _) => _hostPanelManager.Refresh();
    }

    public void StartRefresh()
    {
        OnPropertyChanged(nameof(FileBandwidthText));
        _hostPanelManager.Refresh();
        _refreshTimer.Start();
    }

    public void StopRefresh()
    {
        _refreshTimer.Stop();
    }

    [RelayCommand]
    private void Kick(HostPanelManager.SessionUserItem user)
    {
        _hostPanelManager.Kick(user.Id);
    }
}
