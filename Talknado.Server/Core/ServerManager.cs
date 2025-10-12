using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Talknado.Server.Core;

public interface IServerManager
{
    (string?, string?, string?) Start(string? password);
}

public class ServerManager(INetworkUtils networkUtils,
    IServerInfo serverInfo, IClientManager clientManager,
    ICryptoSessionManager cryptoSessionManager) : IServerManager, IDisposable
{
    private const string ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private readonly CancellationTokenSource _mainTokenSource = new();
    private Thread? _serverThread;
    private TcpListener _listener = null!;

    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly IServerInfo _serverInfo = serverInfo;
    private readonly IClientManager _clientManager = clientManager;
    private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;

    public (string?, string?, string?) Start(string? password)
    {
        try
        {
            _listener = new(IPAddress.Any, 0);
            _listener.Start(5);

            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            var ip = GetOutboundIP();

            var connectionKey = GetServerConnectionKey(ip, port);
            var localConnectionKey = GetServerConnectionKey("127.0.0.1", port);
            if (password != null)
            {
                connectionKey += $"?{password}";
                localConnectionKey += $"?{password}";
            }

            _serverThread = new Thread(() => ServerHandle(password, _mainTokenSource.Token))
            {
                IsBackground = true
            };
            _serverThread.Start();

            return (null, localConnectionKey, connectionKey);
        }
        catch (Exception ex)
        {
            return (ex.Message, null, null);
        }

    }


    private void ServerHandle(string? password, CancellationToken token)
    {
        if (password != null)
            _serverInfo.SetServerPassword(password);

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = _listener.AcceptTcpClient();
                    tcpClient.ReceiveTimeout = 3000;

                    var stream = tcpClient.GetStream();

                    if (!ValidateClientConnection(stream, token, out var userId))
                    {
                        var data = _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes("#PNO"));
                        tcpClient.Close();
                        continue;
                    }

                    if (userId != 0)
                        _clientManager.ReconnectClient(tcpClient, userId, token);
                    else
                        _clientManager.ConnectClient(tcpClient, token);
                }
                catch
                {
                    continue;
                }
            }
        }
        finally
        {
            _listener.Stop();
            _listener.Dispose();
        }
    }

    private static string GetOutboundIP()
    {
        using var client = new HttpClient();
        try
        {
            var response = client.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult();
            return response.Trim();
        }
        catch
        {
            throw new IOException("No internet connection detected");
        }
    }

    private bool ValidateClientConnection(NetworkStream stream, CancellationToken token, out ushort userId)
    {
        userId = 0;
        var data = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();

        if (data == null)
            return false;

        if (data.Length == 32)
        {
            try
            {
                var userIdBytes = _cryptoSessionManager.DecryptMessage(data);
                userId = BitConverter.ToUInt16(userIdBytes);
                return true;
            }
            catch
            {
                return false;
            }
        }
        else
        {
            var clientVersion = Encoding.UTF8.GetString(data);

            foreach (var version in _serverInfo.GetValidClientVersions())
            {
                if (clientVersion == version) return true;
            }

            return false;
        }
    }

    private static string GetServerConnectionKey(string ip, int port)
    {
        byte[] ipBytes = IPAddress.Parse(ip).GetAddressBytes();
        ulong value = 0;

        for (int i = 0; i < 4; i++)
            value = (value << 8) | ipBytes[i];
        value = (value << 16) | (ushort)port;

        var result = "";
        while (value > 0)
        {
            int digit = (int)(value % 52);
            result = ALPHABET[digit] + result;
            value /= 52;
        }

        return result.PadLeft(9, 'A');
    }

    public void Dispose()
    {
        _mainTokenSource?.Cancel();

        GC.SuppressFinalize(this);
    }
}