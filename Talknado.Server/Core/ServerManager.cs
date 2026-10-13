using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using Talknado.Server.Core.Helpers;

namespace Talknado.Server.Core;

public interface IServerManager
{
    (bool, string) Start(string? password);
}

public class ServerManager(INetworkUtils networkUtils,
    IServerInfo serverInfo, IClientManager clientManager,
    ICryptoSessionManager cryptoSessionManager, IFileChannel fileChannel) : IServerManager, IDisposable
{
    private const string ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789$&";

    private readonly CancellationTokenSource _mainTokenSource = new();
    private Thread? _serverThread;
    private TcpListener _listener = null!;

    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly IServerInfo _serverInfo = serverInfo;
    private readonly IClientManager _clientManager = clientManager;
    private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;
    private readonly IFileChannel _fileChannel = fileChannel;

    public (bool, string) Start(string? password)
    {
        try
        {
            _listener = new(IPAddress.Any, 0);
            _listener.Start(5);

            _serverInfo.Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            var token = _mainTokenSource.Token;

            var connectionKey = GetServerConnectionKey(_serverInfo.Port, token);

            if (password != null)
                connectionKey += $"?{password}";

            _networkUtils.Start(token);

            _serverThread = new Thread(() => ServerHandle(password, token))
            {
                IsBackground = false
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

                    if (tcpClient.Client.RemoteEndPoint is IPEndPoint remote
                        && _serverInfo.IsAddressBanned(remote.Address.ToString()))
                    {
                        tcpClient.Close();
                        continue;
                    }

                    var stream = tcpClient.GetStream();
                    var hello = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();

                    if (_fileChannel.IsHello(hello, out var fileChannelUserId))
                    {
                        _networkUtils.WritePacketAsync(stream, Encoding.UTF8.GetBytes("#ZBS"), token).GetAwaiter().GetResult();
                        _fileChannel.Accept(tcpClient, fileChannelUserId);
                        continue;
                    }

                    if (!ValidateClientConnection(hello, out var userId))
                    {
                        var data = Encoding.UTF8.GetBytes("#PNO");
                        _networkUtils.WritePacketAsync(stream, data, token).GetAwaiter().GetResult();
                        tcpClient.Close();
                        continue;
                    }

                    // Вслед за #ZBS — версия сервера: клиент сам решает, не слишком ли
                    // стар для него сервер. Клиентам до 2.0.0 сюда не дойти — им #PNO.
                    var acceptData = Encoding.UTF8.GetBytes($"#ZBS{_serverInfo.ServerVersion.ToString(3)}");
                    _networkUtils.WritePacketAsync(stream, acceptData, token).GetAwaiter().GetResult();

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

    private bool ValidateClientConnection(byte[] data, out ushort userId)
    {
        userId = 0;

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
            return _serverInfo.IsClientVersionSupported(Encoding.UTF8.GetString(data));
        }
    }

    private static string GetServerConnectionKey(int port, CancellationToken token)
    {
        var globalIP = GetGlobalNetworkIP(token);
        bool hasGlobal = globalIP != "0.0.0.0";

        var w = new LocalIPsPacker.BitWriter();
        w.Write(hasGlobal ? 1 : 0, 1);
        if (hasGlobal)
        {
            byte[] gb = IPAddress.Parse(globalIP).GetAddressBytes();
            foreach (var b in gb) w.Write(b, 8);
        }
        w.Write(port, 16);
        LocalIPsPacker.Pack(w);

        byte[] bytes = w.ToArray();
        BigInteger value = new(bytes, isUnsigned: true, isBigEndian: true);
        if (value == 0) return "A";

        var result = "";
        while (value > 0)
        {
            result = ALPHABET[(int)(value % 64)] + result;
            value /= 64;
        }
        return result;
    }

    public void Dispose()
    {
        _mainTokenSource.Cancel();
        _listener?.Stop();
        _serverThread?.Join();
        _mainTokenSource.Dispose();

        GC.SuppressFinalize(this);
    }
}