using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace Talknado.Client.Models;

public interface IUsersInfo
{
    void AddUser(ushort userId, string username, bool isMicrophoneActive, bool isScreenShareActive);
    void RemoveUser(ushort userId);
    void UpdateMicrophoneState(ushort userId, bool isActive);
    void UpdateScreenSharingState(ushort userId, bool isActive);
    float GetVolumeByUserId(ushort userId);
    string GetUsernameByUserId(ushort userId);
    ObservableCollection<UsersInfo.UserItem> Users { get; }
}

public partial class UsersInfo : ObservableObject, IUsersInfo
{
    [ObservableProperty]
    private ObservableCollection<UserItem> _users = [];
    private readonly ConcurrentDictionary<ushort, UserItem> _userLookup = [];

    private readonly IUsersAudioPlayer _usersAudioPlayer;
    private readonly IConnectionInfo _connectionInfo;

    public UsersInfo(IUsersAudioPlayer usersAudioPlayer, IConnectionInfo connectionInfo)
    {
        _usersAudioPlayer = usersAudioPlayer;
        _connectionInfo = connectionInfo;

        _usersAudioPlayer.UserAdded += userId => UpdateMicrophoneState(userId, true);
        _usersAudioPlayer.UserRemoved += userId => UpdateMicrophoneState(userId, false);
    }

    public float GetVolumeByUserId(ushort userId)
    {
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
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_userLookup.TryGetValue(userId, out var userToRemove))
            {
                Users.Remove(userToRemove);
                _userLookup.Remove(userId, out _);
            }
        });
    }

    public void UpdateMicrophoneState(ushort userId, bool isActive)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_userLookup.TryGetValue(userId, out var user))
            {
                user.IsMicrophoneActive = isActive;
            }
        });
    }

    public void UpdateScreenSharingState(ushort userId, bool isActive)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_userLookup.TryGetValue(userId, out var user))
            {
                user.IsScreenShareActive = isActive;
            }
        });
    }

    public partial class UserItem : ObservableObject
    {
        [ObservableProperty]
        public string _username;

        [ObservableProperty]
        public bool _isMicrophoneActive;

        [ObservableProperty]
        public bool _isScreenShareActive;

        [ObservableProperty]
        public float _volume = 50f;

        public UserItem(string username, bool isMicrophoneActive, bool isScreenSharingActive)
        {
            Username = username;
            IsMicrophoneActive = isMicrophoneActive;
            IsScreenShareActive = isScreenSharingActive;
        }
    }
}