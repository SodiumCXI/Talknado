using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;

namespace Talknado.Client.Models;

public interface IUsersInfo
{
    ObservableCollection<UsersInfo.UserItem> Users { get; }
    bool IsWaitingForScreenShareWindow { get; set; }
    float GetVolumeByUserId(ushort userId);
    string GetUsernameByUserId(ushort userId);
    void AddUser(ushort userId, string username, bool isMicrophoneActive, bool isScreenShareActive);
    void RemoveUser(ushort userId);
    void UpdateMicrophoneState(ushort userId, bool isActive);
    void UpdateScreenSharingState(ushort userId, bool isActive);
}

public partial class UsersInfo(IConnectionInfo connectionInfo, IWindowsState windowsState) : ObservableObject, IUsersInfo
{
    private readonly IConnectionInfo _connectionInfo = connectionInfo;
    private readonly IWindowsState _windowsState = windowsState;

    [ObservableProperty]
    private ObservableCollection<UserItem> _users = [];
    private readonly ConcurrentDictionary<ushort, UserItem> _userLookup = [];

    [ObservableProperty]
    private float _screenShareVolume = 50f;

    public bool IsWaitingForScreenShareWindow
    {
        get => _userLookup.Values.FirstOrDefault(u => u.IsScreenShareActive)
               ?.IsWaitingForScreenShareWindow ?? true;
        set
        {
            var user = _userLookup.Values.FirstOrDefault(u => u.IsScreenShareActive);
            if (user != null)
            {
                user.IsWaitingForScreenShareWindow = value;
            }
        }
    }

    public float GetVolumeByUserId(ushort userId)
    {
        if (userId == 0)
            return ScreenShareVolume;
        else
            return _userLookup.TryGetValue(userId, out var user) ? user.Volume : 0f;
    }

    public string GetUsernameByUserId(ushort userId)
    {
        return _userLookup.TryGetValue(userId, out var user) ? user.Username : string.Empty;
    }

    public void AddUser(ushort userId, string username, bool isMicrophoneActive, bool isScreenShareActive)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_userLookup.TryGetValue(userId, out var existingUser))
            {
                existingUser.Username = username;
                existingUser.IsMicrophoneActive = isMicrophoneActive;
                existingUser.IsScreenShareActive = isScreenShareActive;
            }
            else
            {
                var newUser = new UserItem(username, isMicrophoneActive, isScreenShareActive);
                if (userId == _connectionInfo.LocalUserId)
                    newUser.Volume = 0f;


                Users.Add(newUser);

                _userLookup[userId] = newUser;
            }
        });
    }

    public void RemoveUser(ushort userId)
    {
        if (userId == _connectionInfo.LocalUserId)
        {
            _windowsState.InvokeClientDisconnected();
        }

        if (_userLookup.TryGetValue(userId, out var userToRemove))
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Users.Remove(userToRemove);
            });
            _userLookup.Remove(userId, out _);
        }
    }

    public void UpdateMicrophoneState(ushort userId, bool isActive)
    {
        if (_userLookup.TryGetValue(userId, out var user))
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                user.IsMicrophoneActive = isActive;
            });
        }
    }

    public void UpdateScreenSharingState(ushort userId, bool isActive)
    {
        if (_userLookup.TryGetValue(userId, out var user))
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                user.IsScreenShareActive = isActive;
            });
        }
    }

    public partial class UserItem : ObservableObject
    {
        [ObservableProperty]
        private string _username;

        [ObservableProperty]
        private bool _isMicrophoneActive;

        [ObservableProperty]
        private bool _isScreenShareActive;

        [ObservableProperty]
        private float _volume = 50f;

        [ObservableProperty]
        private bool _isWaitingForScreenShareWindow;

        public UserItem(string username, bool isMicrophoneActive, bool isScreenSharingActive)
        {
            Username = username;
            IsMicrophoneActive = isMicrophoneActive;
            IsScreenShareActive = isScreenSharingActive;
        }
    }
}