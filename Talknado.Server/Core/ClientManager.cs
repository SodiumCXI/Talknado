using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Talknado.Server.Core.Helpers;

namespace Talknado.Server.Core;

public interface IClientManager
{
    void ConnectClient(TcpClient tcpClient, CancellationToken token);
    void ReconnectClient(TcpClient tcpClient, ushort userId, CancellationToken token);
}

internal class ClientManager(IUsersInfo usersInfo, INetworkUtils networkUtils,
    ICryptoSessionManager cryptoSessionManager, IServerInfo serverInfo,
    IScreenShareManager screenShareManager) : IClientManager, IDisposable
{
    private readonly HashSet<int> _usedClientIds = [];
    private readonly object _lockCommandExecution = new();

    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;
    private readonly IServerInfo _serverInfo = serverInfo;
    private readonly IScreenShareManager _screenShareManager = screenShareManager;

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

            SendClientsList(stream, token);
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
            _usersInfo.UpdateUserTcpClient(userId, tcpClient);

            var data = _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes("CTU"));
            _networkUtils.WritePacketAsync(stream, data, token).GetAwaiter().GetResult();
        }
        catch
        {
            DisconnectClient(userId);
            throw;
        }
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
                    var data = _cryptoSessionManager.DecryptMessage(encryptedData);
                    var command = Encoding.UTF8.GetString(data[..4]);

                    lock (_lockCommandExecution)
                    {
                        if (command.StartsWith("#MSG"))
                            _networkUtils.BroadcastMessage(userId, encryptedData, token).GetAwaiter().GetResult();
                        else
                            ExecuteCommand(stream, userId, command, data.AsSpan()[4..], token);
                    }
                }
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
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
        _usersInfo.DisconnectUser(userId, false);
        _cryptoSessionManager.TryRemoveSharedSecret(userId);
        _usedClientIds.Remove(userId);
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

    private void ExecuteCommand(NetworkStream stream, ushort userId, string command, ReadOnlySpan<byte> body, CancellationToken token)
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

                    var acceptMessage = Encoding.UTF8.GetBytes($"#YYC");
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

                using (var ms = new MemoryStream())
                {
                    ms.Write(Encoding.UTF8.GetBytes($"#CSS"));
                    ms.Write(BitConverter.GetBytes(userId));
                    var message = ms.ToArray();
                    _networkUtils.BroadcastMessage(userId, _cryptoSessionManager.EncryptMessage(message), token);
                    _networkUtils.WritePacketAsync(stream, _cryptoSessionManager.EncryptMessage(message), token);
                }
                _screenShareManager.ScreenSharerId = 0;

                break;

            default:

                break;
        }
    }

    private static void WriteLog(string message)
    {
        Debug.WriteLine($"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] {message}");
    }

    public void Dispose()
    {
        _usedClientIds.Clear();
        GC.SuppressFinalize(this);
    }
}