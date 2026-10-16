using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Talknado.Server.Core.Helpers;

namespace Talknado.Server.Core;

public interface IClientManager
{
    void ConnectClient(TcpClient tcpClient, CancellationToken token);
    void ReconnectClient(TcpClient tcpClient, ushort userId, CancellationToken token);
    void KickUser(ushort userId);
    void StopScreenShare();
}

public class ClientManager(IUsersInfo usersInfo, INetworkUtils networkUtils,
    ICryptoSessionManager cryptoSessionManager, IServerInfo serverInfo,
    IScreenShareManager screenShareManager, IFileChannel fileChannel) : IClientManager, IDisposable
{
    private readonly HashSet<int> _usedClientIds = [];
    private readonly HashSet<ushort> _kickedIds = [];
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    private Timer? _pingTimer;

    // Опорный кадр дорогой: просьбы зрителей к демонстрирующему схлопываются.
    private static readonly TimeSpan KeyFrameRequestInterval = TimeSpan.FromMilliseconds(500);
    private readonly object _keyFrameLock = new();
    private long _lastKeyFrameRequest;

    // Участники, закончившие вход. Пока идёт рукопожатие, клиент ждёт от
    // сервера строго определённых ответов, и рассылка задержек, попавшая
    // между ними, сорвала бы вход.
    private readonly ConcurrentDictionary<ushort, byte> _readyIds = [];
    private readonly object _lockCommandExecution = new();

    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;
    private readonly IServerInfo _serverInfo = serverInfo;
    private readonly IScreenShareManager _screenShareManager = screenShareManager;
    private readonly IFileChannel _fileChannel = fileChannel;

    public void ConnectClient(TcpClient tcpClient, CancellationToken token)
    {
        var stream = tcpClient.GetStream();
        var userId = FindFreeClientId();

        try
        {
            SharedSecretExchange(stream, userId, token);

            if (!ValidatePassword(stream, userId, token))
                throw new ArgumentException("Incorrect password");

            SendSessionKey(stream, userId, token);
            ClientInfoExchange(stream, userId, token, out var username);
            _usersInfo.AddUser(userId, username, tcpClient);

            if (!WaitUntil(() => _usersInfo.CheckUserConnection(userId),
                TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100),
                stream, token))
                throw new TimeoutException("User did not connect to UDP");

            BroadcastAddNewUser(userId, username, token);
            StartPingBroadcast();
            _networkUtils.KeyFrameNeeded ??= RequestKeyFrame;

            SendClientsList(stream, token);
            _readyIds[userId] = 0;
        }
        catch
        {
            DisconnectClient(userId);
            throw;
        }

        Task.Run(() => HandleClient(userId), token);
    }

    public void ReconnectClient(TcpClient tcpClient, ushort userId, CancellationToken token)
    {
        var stream = tcpClient.GetStream();

        try
        {
            _readyIds.TryRemove(userId, out _);
            _usersInfo.UpdateUserTcpClient(userId, tcpClient);

            var data = _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes("#CTU"));
            _networkUtils.WritePacketAsync(stream, data, token).GetAwaiter().GetResult();
            _readyIds[userId] = 0;
        }
        catch
        {
            DisconnectClient(userId);
            throw;
        }
    }

    /// <summary>
    /// Порядок важен: #KIK уходит до разрыва, иначе клиент его не получит.
    /// Пометка в _kickedIds не даёт читающему циклу ждать возврата выгнанного.
    /// </summary>
    public void KickUser(ushort userId)
    {
        lock (_lockCommandExecution)
        {
            _kickedIds.Add(userId);

            try
            {
                var (stream, token) = _usersInfo.GetUserStreamAndToken(userId);
                var message = _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes("#KIK"));
                _networkUtils.WritePacketAsync(stream, message, token).GetAwaiter().GetResult();
            }
            catch { /* ignore */ }

            // Петлевой адрес не запрещаем: на нём сидит сам хост.
            var address = _usersInfo.GetUserAddress(userId);
            if (address != "127.0.0.1" && address != "::1")
                _serverInfo.BanAddress(address);

            try
            {
                _usersInfo.DisconnectUser(userId, true);
            }
            catch { /* ignore */ }
        }
    }

    public void StopScreenShare()
    {
        var sharerId = _screenShareManager.ScreenSharerId;
        if (sharerId == 0)
            return;

        _screenShareManager.ScreenSharerId = 0;

        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes("#CSS"));
        ms.Write(BitConverter.GetBytes(sharerId));
        var message = _cryptoSessionManager.EncryptMessage(ms.ToArray());

        _networkUtils.BroadcastMessage(sharerId, message, CancellationToken.None);

        try
        {
            var (stream, token) = _usersInfo.GetUserStreamAndToken(sharerId);
            _networkUtils.WritePacketAsync(stream, message, token).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    private bool ValidatePassword(NetworkStream stream, ushort userId, CancellationToken token)
    {
        var data = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
        if (data == null)
            return false;

        var decryptedPasswordHash = _cryptoSessionManager.DecryptPassword(userId, data);
        if (_serverInfo.VerifyPassword(decryptedPasswordHash))
        {
            _networkUtils.WritePacketAsync(stream, Encoding.UTF8.GetBytes("#PIC"), token);
            return true;
        }
        else
        {
            _networkUtils.WritePacketAsync(stream, Encoding.UTF8.GetBytes("#PII"), token);
            return false;
        }
    }

    private void ClientInfoExchange(NetworkStream stream, ushort userId, CancellationToken token, out string username)
    {
        var data = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult()
            ?? throw new IOException("Invalid packet");

        username = Encoding.UTF8.GetString(_cryptoSessionManager.DecryptMessage(data));

        var encryptedId = _cryptoSessionManager.EncryptMessage(BitConverter.GetBytes(userId));
        _networkUtils.WritePacketAsync(stream, encryptedId, token).GetAwaiter().GetResult();
    }

    private void SendClientsList(NetworkStream stream, CancellationToken token)
    {
        try
        {
            var usersPublicInfo = _usersInfo.GetUsersPublicInfo();
            var encryptedUsersPublicInfoCountBytes = _cryptoSessionManager.EncryptMessage(BitConverter.GetBytes(usersPublicInfo.Count));
            _networkUtils.WritePacketAsync(stream, encryptedUsersPublicInfoCountBytes, token).GetAwaiter().GetResult();

            foreach (var user in usersPublicInfo)
            {
                var userIdBytes = BitConverter.GetBytes(user.Item1);
                var usernameBytes = Encoding.UTF8.GetBytes(user.Item2);

                var resultData = new byte[userIdBytes.Length + usernameBytes.Length];
                Buffer.BlockCopy(userIdBytes, 0, resultData, 0, userIdBytes.Length);
                Buffer.BlockCopy(usernameBytes, 0, resultData, userIdBytes.Length, usernameBytes.Length);

                var encryptedData = _cryptoSessionManager.EncryptMessage(resultData);
                _networkUtils.WritePacketAsync(stream, encryptedData, token).GetAwaiter().GetResult();
            }

            var encryptedScreenSharerIdBytes = _cryptoSessionManager.EncryptMessage(BitConverter.GetBytes(_screenShareManager.ScreenSharerId));
            _networkUtils.WritePacketAsync(stream, encryptedScreenSharerIdBytes, token).GetAwaiter().GetResult();
        }
        catch
        {
            throw new IOException("Failed to send information about active clients");
        }
    }

    private void StartPingBroadcast()
    {
        if (_pingTimer != null)
            return;

        var timer = new Timer(_ => BroadcastPings());
        if (Interlocked.CompareExchange(ref _pingTimer, timer, null) == null)
            timer.Change(PingInterval, PingInterval);
        else
            timer.Dispose();
    }

    /// <summary>
    /// #PNG: пары «номер участника, задержка до сервера туда и обратно в мс».
    /// Клиент складывает свою задержку с чужой — звук идёт через сервер.
    /// Прежние сборки команду игнорируют.
    /// </summary>
    private void BroadcastPings()
    {
        try
        {
            using var ms = new MemoryStream();
            ms.Write("#PNG"u8);

            foreach (var user in _usersInfo.GetSessionUsers())
            {
                if (user.Ping < 0)
                    continue;

                ms.Write(BitConverter.GetBytes(user.Id));
                ms.Write(BitConverter.GetBytes((ushort)Math.Min(user.Ping, ushort.MaxValue)));
            }

            var message = _cryptoSessionManager.EncryptMessage(ms.ToArray());

            foreach (var userId in _readyIds.Keys)
            {
                try
                {
                    var (stream, token) = _usersInfo.GetUserStreamAndToken(userId);
                    _ = _networkUtils.WritePacketAsync(stream, message, token);
                }
                catch { /* участник уходит или переподключается: следующая рассылка через секунду */ }
            }
        }
        catch { /* исключение в обратном вызове таймера уронило бы сервер */ }
    }

    private void BroadcastAddNewUser(ushort userId, string username, CancellationToken token)
    {
        using var ms = new MemoryStream();

        ms.Write(Encoding.UTF8.GetBytes($"#ADD"));
        ms.Write(BitConverter.GetBytes(userId));
        ms.Write(Encoding.UTF8.GetBytes(username));
        var message = ms.ToArray();
        _networkUtils.BroadcastMessage(userId, _cryptoSessionManager.EncryptMessage(message), token);
    }

    public void SharedSecretExchange(NetworkStream stream, ushort userId, CancellationToken token)
    {
        var serverPublicKey = _cryptoSessionManager.GetServerPublicKey();
        _networkUtils.WritePacketAsync(stream, serverPublicKey, token).GetAwaiter().GetResult();

        var data = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult() ?? throw new IOException("Stream exception");
        _cryptoSessionManager.SetSharedSecret(userId, data);
    }

    public void SendSessionKey(NetworkStream stream, ushort userId, CancellationToken token)
    {
        var encryptedSessionKey = _cryptoSessionManager.GetEncryptedSessionKey(userId);
        _networkUtils.WritePacketAsync(stream, encryptedSessionKey, token).GetAwaiter().GetResult();
    }

    private void HandleClient(ushort userId)
    {
        while (true)
        {
            var (stream, token) = _usersInfo.GetUserStreamAndToken(userId);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var encryptedData = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
                    var headerBytes = _cryptoSessionManager.DecryptMessage(encryptedData[..32]);
                    var header = Encoding.UTF8.GetString(headerBytes);

                    lock (_lockCommandExecution)
                    {
                        if (header.StartsWith("MSG"))
                        {
                            _networkUtils.BroadcastMessage(userId, encryptedData[32..], token).GetAwaiter().GetResult();
                        }
                        else
                        {
                            var commandBytes = _cryptoSessionManager.DecryptMessage(encryptedData[32..]);
                            var command = Encoding.UTF8.GetString(commandBytes);
                            ExecuteCommand(stream, userId, command,  token);
                        }
                    }
                }
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                if (_kickedIds.Contains(userId))
                    break;

                _usersInfo.DisconnectUser(userId, true);

                try
                {
                    if (!WaitUntil(() => _usersInfo.CheckUserConnection(userId),
                        TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100),
                        stream, token))
                        break;
                }
                catch
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }

        DisconnectClient(userId);
    }

    private void DisconnectClient(ushort userId)
    {
        if (_screenShareManager.ScreenSharerId == userId)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(Encoding.UTF8.GetBytes($"#CSS"));
                ms.Write(BitConverter.GetBytes(userId));
                var message = ms.ToArray();
                _networkUtils.BroadcastMessage(userId, _cryptoSessionManager.EncryptMessage(message), CancellationToken.None);
            }
            _screenShareManager.ScreenSharerId = 0;
        }

        using (var ms = new MemoryStream())
        {
            ms.Write(Encoding.UTF8.GetBytes($"#REM"));
            ms.Write(BitConverter.GetBytes(userId));
            var message = ms.ToArray();
            _networkUtils.BroadcastMessage(userId, _cryptoSessionManager.EncryptMessage(message), CancellationToken.None);
        }    
        _readyIds.TryRemove(userId, out _);
        _usersInfo.DisconnectUser(userId, false);
        _fileChannel.RemoveUser(userId);
        _cryptoSessionManager.TryRemoveSharedSecret(userId);
        _usedClientIds.Remove(userId);
        _kickedIds.Remove(userId);
    }

    private bool WaitUntil(Func<bool> condition, TimeSpan timeout, TimeSpan pollInterval, NetworkStream stream, CancellationToken token)
    {
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            if (condition())
            {
                var messageAPC = Encoding.UTF8.GetBytes("#UCC");
                _networkUtils.WritePacketAsync(stream, _cryptoSessionManager.EncryptMessage(messageAPC), token);
                return true;
            }

            Thread.Sleep(pollInterval);
        }

        var messagePNC = Encoding.UTF8.GetBytes("#UNC");
        try
        {
            _networkUtils.WritePacketAsync(stream, _cryptoSessionManager.EncryptMessage(messagePNC), token).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        return false;
    }

    private ushort FindFreeClientId()
    {
        ushort id = 1;
        while (_usedClientIds.Contains(id)) id++;
        _usedClientIds.Add(id);
        return id;
    }

    private void ExecuteCommand(NetworkStream stream, ushort userId, string command, CancellationToken token)
    {
        switch (command)
        {
            case var _ when command.Equals("#END"):

                throw new OperationCanceledException("User initiated disconnect");

            case var _ when command.Equals("#CIS"):

                if (_screenShareManager.ScreenSharerId != 0)
                {
                    var message = Encoding.UTF8.GetBytes($"#NYC");
                    _networkUtils.WritePacketAsync(stream, _cryptoSessionManager.EncryptMessage(message), token);
                }
                else
                {
                    _screenShareManager.ScreenSharerId = userId;

                    // Пятый байт — потолок качества. Прежние сборки читают
                    // только первые четыре и лишний игнорируют.
                    var acceptMessage = Encoding.UTF8.GetBytes($"#YYC").Append(_serverInfo.MaxShareQuality).ToArray();
                    _networkUtils.WritePacketAsync(stream, _cryptoSessionManager.EncryptMessage(acceptMessage), token);

                    using (var ms = new MemoryStream())
                    {
                        ms.Write(Encoding.UTF8.GetBytes($"#SSS"));
                        ms.Write(BitConverter.GetBytes(userId));
                        var message = ms.ToArray();
                        _networkUtils.BroadcastMessage(userId, _cryptoSessionManager.EncryptMessage(message), token);
                        _networkUtils.WritePacketAsync(stream, _cryptoSessionManager.EncryptMessage(message), token);
                    }
                }

                break;

            case var _ when command.Equals("#STO"):

                StopScreenShare();

                break;

            case var _ when command.Equals("#KFR"):

                var sharerId = _screenShareManager.ScreenSharerId;
                if (sharerId != 0 && sharerId != userId)
                    RequestKeyFrame(sharerId);

                break;

            default:

                break;
        }
    }

    private void RequestKeyFrame(ushort sharerId)
    {
        lock (_keyFrameLock)
        {
            if (Stopwatch.GetElapsedTime(_lastKeyFrameRequest) < KeyFrameRequestInterval)
                return;
            _lastKeyFrameRequest = Stopwatch.GetTimestamp();
        }

        try
        {
            var (stream, token) = _usersInfo.GetUserStreamAndToken(sharerId);
            _ = _networkUtils.WritePacketAsync(stream, _cryptoSessionManager.EncryptMessage("#KFR"u8.ToArray()), token);
        }
        catch { /* демонстрирующий уходит или переподключается */ }
    }

    private static void WriteLog(string message)
    {
        Debug.WriteLine($"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {message}");
    }

    public void Dispose()
    {
        _pingTimer?.Dispose();
        _usedClientIds.Clear();
        GC.SuppressFinalize(this);
    }
}