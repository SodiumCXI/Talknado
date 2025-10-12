using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Talknado.Server.Core;

public interface IUsersInfo
{
    void AddUser(ushort userId, string username, TcpClient connection);
    void UpdateUser(ushort userId, IPEndPoint? audioEndPoint, IPEndPoint? screenShareEndPoint);
    void DisconnectUser(ushort userId, bool isInitial);
    (NetworkStream, CancellationToken) GetUserStreamAndToken(ushort userId);
    HashSet<(NetworkStream, ushort)> GetUserStreamsWithId(ushort currentUserId);
    HashSet<IPEndPoint> GetAudioEndPoints(ushort currentUserId);
    HashSet<IPEndPoint> GetScreenShareEndPoints(ushort currentUserId);
    HashSet<(ushort, string)> GetUsersPublicInfo();
    ushort FindUserIdByEndPoint(IPEndPoint endPoint);
    bool CheckUserConnection(ushort userId);
}

public class UsersInfo : IUsersInfo, IDisposable
{
    private readonly ConcurrentDictionary<ushort, UserItem> _clientsInfo = [];

    public void AddUser(ushort userId, string username, TcpClient connection)
    {
        if (_clientsInfo.TryGetValue(userId, out var existingUser))
        {
            existingUser.IsConnected = false;
            existingUser.Username = username;
            existingUser.Connection = connection;

            existingUser.AudioEndPoint = null;
            existingUser.ScreenShareEndPoint = null;
        }
        else
        {
            var newUser = new UserItem(username, connection);

            _clientsInfo[userId] = newUser;
        }
    }

    public void UpdateUser(ushort userId, IPEndPoint? audioEndPoint, IPEndPoint? screenShareEndPoint)
    {
        if (_clientsInfo.TryGetValue(userId, out var existingUser))
        {
            if (existingUser.IsConnected)
                return;

            if (audioEndPoint != null)
                existingUser.AudioEndPoint = audioEndPoint;
            if (screenShareEndPoint != null)
                existingUser.ScreenShareEndPoint = screenShareEndPoint;

            if (existingUser.AudioEndPoint != null && existingUser.ScreenShareEndPoint != null)
            {
                existingUser.RestartToken();
                existingUser.IsConnected = true;
            }
        }
    }

    public void DisconnectUser(ushort userId, bool isInitial)
    {
        if (isInitial)
        {
            _clientsInfo[userId].IsConnected = false;
            _clientsInfo[userId].TokenSource.Cancel();
            _clientsInfo[userId].AudioEndPoint = null;
            _clientsInfo[userId].ScreenShareEndPoint = null;
        }
        else
        {
            if (_clientsInfo.TryRemove(userId, out var userItem))
            {
                try
                {
                    userItem.Connection?.Client.Shutdown(SocketShutdown.Both);
                }
                catch { /* ignore */ }

                userItem.Dispose();
            }
        }
    }

    public (NetworkStream, CancellationToken) GetUserStreamAndToken(ushort userId)
    {
        if (_clientsInfo.TryGetValue(userId, out var userItem))
            return (userItem.Connection.GetStream(), userItem.TokenSource.Token);

        throw new KeyNotFoundException($"User with ID {userId} not found");
    }

    public HashSet<(NetworkStream, ushort)> GetUserStreamsWithId(ushort currentUserId)
    {
        return [.. _clientsInfo
            .Where(kvp => kvp.Key != currentUserId)
            .Select(kvp => (kvp.Value.Connection.GetStream(), kvp.Key))];
    }

    public HashSet<IPEndPoint> GetAudioEndPoints(ushort currentUserId)
    {
        return [.. _clientsInfo
            .Where(kvp => kvp.Key != currentUserId)
            .Select(kvp => kvp.Value.AudioEndPoint)];
    }

    public HashSet<IPEndPoint> GetScreenShareEndPoints(ushort currentUserId)
    {
        return [.. _clientsInfo
            .Where(kvp => kvp.Key != currentUserId)
            .Select(kvp => kvp.Value.ScreenShareEndPoint)];
    }

    public HashSet<(ushort, string)> GetUsersPublicInfo()
    {
        return [.. _clientsInfo
            .Select(kvp => (kvp.Key, kvp.Value.Username))];
    }

    public ushort FindUserIdByEndPoint(IPEndPoint endPoint)
    {
        foreach (var kvp in _clientsInfo)
        {
            if (endPoint.Equals(kvp.Value.AudioEndPoint) || endPoint.Equals(kvp.Value.ScreenShareEndPoint))
                return kvp.Key;
        }
        return 0;
    }

    public bool CheckUserConnection(ushort userId)
    {
        if (_clientsInfo.TryGetValue(userId, out var existingUser))
            return existingUser.IsConnected;

        throw new KeyNotFoundException($"User with ID {userId} not found");
    }

    public void Dispose()
    {
        foreach (var user in _clientsInfo.Values)
        {
            user.Dispose();
        }
        _clientsInfo.Clear();

        GC.SuppressFinalize(this);
    }

    private class UserItem(string username, TcpClient connection) : IDisposable
    {
        private CancellationTokenSource _tokenSource = new();
        private TcpClient _connection = connection;
        public bool IsConnected { get; set; } = false;
        public CancellationTokenSource TokenSource
        {
            get { return _tokenSource; }
        }

        public TcpClient Connection
        {
            get { return _connection; }
            set
            {
                _connection.Dispose();
                _connection = value;
            }
        }
        public string Username { get; set; } = username;
        public IPEndPoint? AudioEndPoint { get; set; }
        public IPEndPoint? ScreenShareEndPoint { get; set; }

        public void RestartToken()
        {
            _tokenSource?.Dispose();
            _tokenSource = new();
        }

        public void Dispose()
        {
            _tokenSource?.Cancel();
            _tokenSource?.Dispose();
            _connection?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}