using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

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
    void UpdateSpeakingState(ushort userId, bool isSpeaking);
    void UpdateScreenSharingState(ushort userId, bool isActive);
    void UpdatePings(ReadOnlySpan<byte> body);
}

public partial class UsersInfo(IConnectionInfo connectionInfo) : ObservableObject, IUsersInfo
{
    private readonly IConnectionInfo _connectionInfo = connectionInfo;

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
        if (userId == 0)
            return "System";

        return _userLookup.TryGetValue(userId, out var user) ? user.Username : string.Empty;
    }

    public void AddUser(ushort userId, string username, bool isMicrophoneActive, bool isScreenShareActive)
    {
        Application.Current?.Dispatcher.Invoke(() =>
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
            _connectionInfo.InvokeClientDisconnected();
        }

        if (_userLookup.TryGetValue(userId, out var userToRemove))
        {
            Application.Current?.Dispatcher.Invoke(() =>
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
            Application.Current?.Dispatcher.Invoke(() =>
            {
                user.IsMicrophoneActive = isActive;
            });
        }
    }

    public void UpdateSpeakingState(ushort userId, bool isSpeaking)
    {
        if (_userLookup.TryGetValue(userId, out var user))
        {
            if (user.IsSpeaking == isSpeaking)
                return;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                user.IsSpeaking = isSpeaking;
            });
        }
    }

    public void UpdateScreenSharingState(ushort userId, bool isActive)
    {
        if (_userLookup.TryGetValue(userId, out var user))
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                user.IsScreenShareActive = isActive;
            });
        }
    }

    /// <summary>
    /// Сервер рассылает задержку каждого участника до себя (пары «номер, мс»).
    /// Звук идёт через сервер, поэтому задержка до участника — моя плюс его;
    /// в своей строке — своя до сервера.
    /// </summary>
    public void UpdatePings(ReadOnlySpan<byte> body)
    {
        var serverPings = new Dictionary<ushort, int>();
        for (int i = 0; i + 4 <= body.Length; i += 4)
            serverPings[BitConverter.ToUInt16(body[i..])] = BitConverter.ToUInt16(body[(i + 2)..]);

        ushort localId = _connectionInfo.LocalUserId;
        int own = serverPings.GetValueOrDefault(localId, -1);

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var (id, user) in _userLookup)
            {
                user.Ping = own < 0 || !serverPings.TryGetValue(id, out var ping) ? -1
                    : id == localId ? own
                    : own + ping;
            }
        }, DispatcherPriority.Background);
    }

    public partial class UserItem : ObservableObject
    {
        /// <summary>Задержка туда и обратно, мс; -1 — не измерена.</summary>
        [ObservableProperty]
        private int _ping = -1;

        [ObservableProperty]
        private string _username;

        [ObservableProperty]
        private bool _isMicrophoneActive;

        [ObservableProperty]
        private bool _isSpeaking;

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