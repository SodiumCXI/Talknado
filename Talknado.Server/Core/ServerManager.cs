using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Text;

namespace Talknado.Server.Core;

public interface IServerManager
{
    (bool, string) Start(string? password);
}

public class ServerManager(INetworkUtils networkUtils,
    IServerInfo serverInfo, IClientManager clientManager,
    ICryptoSessionManager cryptoSessionManager) : IServerManager, IDisposable
{
    private const string ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789&%";

    private readonly CancellationTokenSource _mainTokenSource = new();
    private Thread? _serverThread;
    private TcpListener _listener = null!;

    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly IServerInfo _serverInfo = serverInfo;
    private readonly IClientManager _clientManager = clientManager;
    private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;

    public (bool, string) Start(string? password)
    {
        try
        {
            var port = _serverInfo.Port;

            _listener = new(IPAddress.Any, port);
            _listener.Start(5);

            var token = _mainTokenSource.Token;

            var localIP = GetLocalNetworkIP();
            var globalIP = GetGlobalNetworkIP(token);

            var connectionKey = GetServerConnectionKey(localIP, globalIP, port);

            if (password != null)
                connectionKey += $"?{password}";

            _networkUtils.Start(token);

            _serverThread = new Thread(() => ServerHandle(password, token))
            {
                IsBackground = true
            };
            _serverThread.Start();

            return (false, connectionKey);
        }
        catch (Exception ex)
        {
            return (true, ex.Message);
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
                    if (token.IsCancellationRequested)
                        break;

                    continue;
                }
            }
        }
        finally
        {
            _listener.Dispose();
        }
    }

    private static string GetLocalNetworkIP()
    {
        var ni = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => new
            {
                Interface = n,
                IPProps = n.GetIPProperties(),
                Priority = GetInterfacePriority(n)
            })
            .Where(x => x.IPProps.UnicastAddresses.Any(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.Interface.Speed)
            .FirstOrDefault()
            ?? throw new IOException("No internet connection detected");

        var ipProps = ni.IPProps;
        var outwardIp = ipProps.UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString()
            ?? throw new IOException("No internet connection detected");

        return outwardIp;
    }

    private static int GetInterfacePriority(NetworkInterface networkInterface)
    {
        if (networkInterface.Name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase))
            return 10;

        var hasGateway = networkInterface.GetIPProperties().GatewayAddresses
            .Any(g => !g.Address.ToString().StartsWith("0.0.0.0") &&
                      g.Address.AddressFamily == AddressFamily.InterNetwork);

        return networkInterface.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet when hasGateway => 5,
            NetworkInterfaceType.Wireless80211 when hasGateway => 4,
            NetworkInterfaceType.Ethernet => 3,
            NetworkInterfaceType.Wireless80211 => 2,
            _ => 1
        };
    }

    private static string GetGlobalNetworkIP(CancellationToken token)
    {
        using var client = new HttpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var response = client.GetStringAsync("https://api.ipify.org", cts.Token).GetAwaiter().GetResult();
            return response.Trim();
        }
        catch
        {
            return "0.0.0.0";
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

    private static string GetServerConnectionKey(string localIp, string globalIp, int port)
    {
        byte[] localIpBytes = IPAddress.Parse(localIp).GetAddressBytes();
        byte[] globalIpBytes = IPAddress.Parse(globalIp).GetAddressBytes();

        BigInteger value = 0;

        for (int i = 0; i < 4; i++)
            value = (value << 8) | localIpBytes[i];

        for (int i = 0; i < 4; i++)
            value = (value << 8) | globalIpBytes[i];

        value = (value << 16) | (ushort)port;

        if (value == 0) return "A";

        var result = "";
        while (value > 0)
        {
            int digit = (int)(value % 64);
            result = ALPHABET[digit] + result;
            value /= 64;
        }

        return result;
    }

    public void Dispose()
    {
        _mainTokenSource.Cancel();
        _listener.Stop();
        _serverThread?.Join();
        _mainTokenSource.Dispose();

        GC.SuppressFinalize(this);
    }
}