using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using System.Diagnostics;
using Talknado.Client.Models;

namespace Talknado.Client.ViewModels;

public partial class MainWindowViewModel(IClientManager clientManager,
    IUsersInfo usersInfo, IMessagesManager messagesManager, IScreenShareManager screenShareManager,
    IWindowsState windowsState, IConnectionInfo connectionInfo, IScreenSharePlayer screenSharePlayer,
    IAudioManager audioManager, ISettingsManager settingsManager) : ObservableObject
{
    private readonly IClientManager _clientManager = clientManager;
    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly IMessagesManager _messagesManager = messagesManager;
    private readonly IScreenShareManager _screenShareManager = screenShareManager;
    private readonly IWindowsState _windowsState = windowsState;
    private readonly IConnectionInfo _connectionInfo = connectionInfo;
    private readonly IScreenSharePlayer _screenSharePlayer = screenSharePlayer;
    private readonly IAudioManager _audioManager = audioManager;
    private readonly ISettingsManager _settingsManager = settingsManager;

    public IScreenShareManager ScreenShareManager => _screenShareManager;
    public IConnectionInfo ConnectionInfo => _connectionInfo;
    public IAudioManager AudioManager => _audioManager;

    public ObservableCollection<MessagesManager.Message> Messages => _messagesManager.Messages;
    public ObservableCollection<UsersInfo.UserItem> Users => _usersInfo.Users;

    [ObservableProperty]
    private string _inputTextBoxValue = string.Empty;
    public bool UseDisconnect { get; set; } = false;

    [RelayCommand]
    private void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(InputTextBoxValue))
        {
            return;
        }
        _clientManager.SendMessage(InputTextBoxValue);
        _messagesManager.AddMessage(_connectionInfo.LocalUserId, InputTextBoxValue);
        InputTextBoxValue = string.Empty;
    }

    [RelayCommand]
    private void CloseConnection()
    {
        _clientManager.CloseConnection();
    }

    [RelayCommand]
    private void ToggleMicrophone()
    {
        _audioManager.ToggleMicrophoneStatus();
    }

    [RelayCommand]
    private void ToggleScreenShare()
    {
        _clientManager.ToggleScreenShare();
    }

    [RelayCommand]
    private void ViewScreenShare()
    {
        _screenSharePlayer.IsWindowVisible = true;
    }

    [RelayCommand]
    private void ViewSettings()
    {
        _settingsManager.IsWindowVisible = true;
    }

    [RelayCommand]
    private void SoftReboot()
    {
        UseDisconnect = true;
        _windowsState.InvokeClientDisconnected();
    }

    [RelayCommand]
    private static void OpenLink()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/SodiumCXI/Talknado",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть ссылку: {ex.Message}");
        }
    }
}