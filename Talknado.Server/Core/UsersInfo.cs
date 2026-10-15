using LiteNetLib;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Talknado.Server.Core;

/// <summary>Ping — задержка до сервера туда и обратно, мс; -1 — пока не измерена.</summary>
public readonly record struct SessionUser(ushort Id, string Username, string Address, int Ping);

public readonly record struct PeerEntry(ushort UserId, NetPeer Peer);

public interface IUsersInfo
{
    IReadOnlyList<SessionUser> GetSessionUsers();
    string GetUserAddress(ushort userId);
    void AddUser(ushort userId, string username, TcpClient connection);
    void UpdateUserTcpClient(ushort userId, TcpClient tcpClient);
    void UpdateUserPeer(ushort userId, NetPeer screenShareEndPoint);
    void DisconnectUser(ushort userId, bool isInitial);
    (NetworkStream, CancellationToken) GetUserStreamAndToken(ushort userId);
    HashSet<(NetworkStream, ushort)> GetUserStreamsWithId(ushort currentUserId);
    /// <summary>Снимок участников с UDP-соединением; массив не изменять.</summary>
    PeerEntry[] GetPeers();
    HashSet<(ushort, string)> GetUsersPublicInfo();
    ushort FindUserIdByNetPeer(NetPeer peer);
    void AddPingSample(NetPeer peer, int roundTripMs);
    bool CheckUserConnection(ushort userId);
    bool HasClient(ushort id);
}

public class UsersInfo : IUsersInfo, IDisposable
{
    private readonly ConcurrentDictionary<ushort, UserItem> _clientsInfo = [];

    // Пересылка голоса и кадров обходит участников на каждый пакет, а состав
    // меняется редко — поэтому обход идёт по снимку, а не по словарю.
    private volatile PeerEntry[] _peers = [];
    private readonly object _peersLock = new();

    private void UpdatePeers()
    {
        lock (_peersLock)
        {
            _peers = [.. _clientsInfo
                .Where(kvp => kvp.Value.Peer != null)
                .Select(kvp => new PeerEntry(kvp.Key, kvp.Value.Peer!))];
        }
    }

    public void AddUser(ushort userId, string username, TcpClient client)
    {
        if (_clientsInfo.TryGetValue(userId, out var existingUser))
        {
            existingUser.IsConnected = false;
            existingUser.Username = username;
            existingUser.Client = client;
            existingUser.Peer = null;
            UpdatePeers();
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
            existingUser.ResetPing();
            existingUser.RestartToken();
            existingUser.IsConnected = true;
            UpdatePeers();
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

        UpdatePeers();
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

    public PeerEntry[] GetPeers() => _peers;

    public IReadOnlyList<SessionUser> GetSessionUsers()
    {
        return [.. _clientsInfo
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new SessionUser(
                kvp.Key,
                kvp.Value.Username,
                GetAddress(kvp.Value),
                kvp.Value.Peer != null ? kvp.Value.Ping : -1))];
    }

    public string GetUserAddress(ushort userId)
    {
        return _clientsInfo.TryGetValue(userId, out var userItem) ? GetAddress(userItem) : string.Empty;
    }

    // Сокет уже может быть закрыт к моменту опроса.
    private static string GetAddress(UserItem userItem)
    {
        try
        {
            return userItem.Client.Client.RemoteEndPoint is IPEndPoint endPoint
                ? endPoint.Address.ToString()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public HashSet<(ushort, string)> GetUsersPublicInfo()
    {
        return [.. _clientsInfo
            .Select(kvp => (kvp.Key, kvp.Value.Username))];
    }

    public ushort FindUserIdByNetPeer(NetPeer peer)
    {
        foreach (var entry in _peers)
        {
            if (peer.Equals(entry.Peer))
                return entry.UserId;
        }
        return 0;
    }

    public void AddPingSample(NetPeer peer, int roundTripMs)
    {
        var userId = FindUserIdByNetPeer(peer);
        if (userId != 0 && _clientsInfo.TryGetValue(userId, out var userItem))
            userItem.AddPing(roundTripMs);
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
        _peers = [];

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

        // Медиана трёх замеров отсекает одиночный выброс.
        private readonly int[] _pings = new int[3];
        private int _pingCount;
        private int _pingNext;

        public int Ping
        {
            get
            {
                lock (_pings)
                {
                    if (_pingCount == 0)
                        return -1;

                    Span<int> sorted = stackalloc int[_pingCount];
                    _pings.AsSpan(0, _pingCount).CopyTo(sorted);
                    sorted.Sort();
                    return sorted[(_pingCount - 1) / 2];
                }
            }
        }

        public void AddPing(int roundTripMs)
        {
            lock (_pings)
            {
                _pings[_pingNext] = roundTripMs;
                _pingNext = (_pingNext + 1) % _pings.Length;
                _pingCount = Math.Min(_pingCount + 1, _pings.Length);
            }
        }

        public void ResetPing()
        {
            lock (_pings)
            {
                _pingCount = 0;
                _pingNext = 0;
            }
        }

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