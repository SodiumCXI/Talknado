using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Talknado.Client.Models;
using Talknado.Client.Views;

namespace Talknado.Client.ViewModels;

public partial class MainWindowViewModel(IClientManager clientManager,
    IUsersInfo usersInfo, IMessagesManager messagesManager, IScreenShareManager screenShareManager,
    IWindowsState windowsState, IConnectionInfo connectionInfo, IScreenSharePlayer screenSharePlayer,
    IAudioManager audioManager) : ObservableObject
{
    private readonly IClientManager _clientManager = clientManager;
    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly IMessagesManager _messagesManager = messagesManager;
    private readonly IScreenShareManager _screenShareManager = screenShareManager;
    private readonly IWindowsState _windowsState = windowsState;
    private readonly IConnectionInfo _connectionInfo = connectionInfo;
    private readonly IScreenSharePlayer _screenSharePlayer = screenSharePlayer;
    private readonly IAudioManager _audioManager = audioManager;

    public IScreenShareManager ScreenShareManager => _screenShareManager;
    public IConnectionInfo ConnectionInfo => _connectionInfo;
    public IAudioManager AudioManager => _audioManager;

    public ObservableCollection<MessagesManager.Message> Messages => _messagesManager.Messages;
    public ObservableCollection<UsersInfo.UserItem> Users => _usersInfo.Users;

    [ObservableProperty]
    private string _inputTextBoxValue = string.Empty;

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
    private void SoftReboot()
    {
        _windowsState.InvokeClientDisconnected();
    }
}