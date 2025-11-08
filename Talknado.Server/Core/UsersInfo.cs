using LiteNetLib;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Talknado.Server.Core;

public interface IUsersInfo
{
    void AddUser(ushort userId, string username, TcpClient connection);
    void UpdateUserTcpClient(ushort userId, TcpClient tcpClient);
    void UpdateUserPeer(ushort userId, NetPeer screenShareEndPoint);
    void DisconnectUser(ushort userId, bool isInitial);
    (NetworkStream, CancellationToken) GetUserStreamAndToken(ushort userId);
    HashSet<(NetworkStream, ushort)> GetUserStreamsWithId(ushort currentUserId);
    HashSet<NetPeer> GetNetPeers(ushort currentUserId);
    HashSet<(ushort, string)> GetUsersPublicInfo();
    ushort FindUserIdByNetPeer(NetPeer peer);
    bool CheckUserConnection(ushort userId);
    bool HasClient(ushort id);
}

public class UsersInfo : IUsersInfo, IDisposable
{
    private readonly ConcurrentDictionary<ushort, UserItem> _clientsInfo = [];

    public void AddUser(ushort userId, string username, TcpClient client)
    {
        if (_clientsInfo.TryGetValue(userId, out var existingUser))
        {
            existingUser.IsConnected = false;
            existingUser.Username = username;
            existingUser.Client = client;
            existingUser.Peer = null;
        }
        else
        {
            var newUser = new UserItem(username, client);

            _clientsInfo[userId] = newUser;
        }
    }

    public void UpdateUserTcpClient(ushort userId, TcpClient tcpClient)
    {
        if (_clientsInfo.TryGetValue(userId, out var existingUser))
        {
            existingUser.Client = tcpClient;
        }
    }

    public void UpdateUserPeer(ushort userId, NetPeer udpPeer)
    {
        if (_clientsInfo.TryGetValue(userId, out var existingUser))
        {
            if (existingUser.IsConnected)
                return;

            existingUser.Peer = udpPeer;
            existingUser.RestartToken();
            existingUser.IsConnected = true;
        }
    }

    public void DisconnectUser(ushort userId, bool isInitial)
    {
        if (isInitial)
        {
            _clientsInfo[userId].IsConnected = false;
            _clientsInfo[userId].TokenSource.Cancel();
            _clientsInfo[userId].Client.Dispose();
            _clientsInfo[userId].Peer = null;
        }
        else
        {
            if (_clientsInfo.TryRemove(userId, out var userItem))
            {
                userItem.Dispose();
            }
        }
    }

    public (NetworkStream, CancellationToken) GetUserStreamAndToken(ushort userId)
    {
        if (_clientsInfo.TryGetValue(userId, out var userItem))
            return (userItem.Client.GetStream(), userItem.TokenSource.Token);

        throw new KeyNotFoundException($"User with ID {userId} not found");
    }

    public HashSet<(NetworkStream, ushort)> GetUserStreamsWithId(ushort currentUserId)
    {
        return [.. _clientsInfo
            .Where(kvp => kvp.Key != currentUserId)
            .Select(kvp => (kvp.Value.Client.GetStream(), kvp.Key))];
    }

    public HashSet<NetPeer> GetNetPeers(ushort currentUserId)
    {
        return [.. _clientsInfo
            .Where(kvp => kvp.Key != currentUserId && kvp.Value.Peer != null)
            .Select(kvp => kvp.Value.Peer)];
    }

    public HashSet<(ushort, string)> GetUsersPublicInfo()
    {
        return [.. _clientsInfo
            .Select(kvp => (kvp.Key, kvp.Value.Username))];
    }

    public ushort FindUserIdByNetPeer(NetPeer peer)
    {
        foreach (var kvp in _clientsInfo)
        {
            if (peer.Equals(kvp.Value.Peer))
                return kvp.Key;
        }
        return 0;
    }

    public TcpClient GetTcpClientById(ushort userId)
    {
        foreach (var kvp in _clientsInfo)
        {
            if (userId.Equals(kvp.Key))
                return kvp.Value.Client;
        }

        return null!;
    }

    public bool HasClient(ushort id)
    {
        return _clientsInfo.ContainsKey(id);
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

    private class UserItem(string username, TcpClient client) : IDisposable
    {
        private CancellationTokenSource _tokenSource = new();
        private TcpClient _client = client;
        public bool IsConnected { get; set; } = false;
        public CancellationTokenSource TokenSource
        {
            get { return _tokenSource; }
        }

        public TcpClient Client
        {
            get { return _client; }
            set
            {
                _client.Dispose();
                _client = value;
            }
        }
        public string Username { get; set; } = username;
        public NetPeer? Peer { get; set; }

        public void RestartToken()
        {
            _tokenSource?.Dispose();
            _tokenSource = new();
        }

        public void Dispose()
        {
            _tokenSource?.Cancel();
            _tokenSource?.Dispose();
            _client?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}