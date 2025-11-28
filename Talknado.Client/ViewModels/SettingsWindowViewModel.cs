using CommunityToolkit.Mvvm.ComponentModel;
using Talknado.Client.Models;

namespace Talknado.Client.ViewModels;

public partial class SettingsWindowViewModel(ISettingsManager settingsManager) : ObservableObject
{
    private readonly ISettingsManager _settingManager = settingsManager;
    public ISettingsManager SettingManager => _settingManager;
}
