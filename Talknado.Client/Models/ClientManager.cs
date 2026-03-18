using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using Talknado.Client.Models.Helpers;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.Models;

public interface IClientManager
{
    string? TryConnect(string connectionKey, string username);
    void SendMessage(string message);
    void CloseConnection();
    void ToggleScreenShare();
}

public class ClientManager(IUsersInfo usersInfo,
    INetworkUtils networkUtils,
    IConnectionInfo connectionInfo,
    ICryptoSessionManager cryptoSessionManager,
    IScreenShareManager screenShareManager,
    IMessagesManager messagesManager,
    IScreenSharePlayer screenSharePlayer,
    ISettingsManager settingsManager,
    IAudioManager audioManager,
    IScreenMonitorManager screenMonitorManager) : IClientManager, IDisposable
{
    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly IConnectionInfo _connectionInfo = connectionInfo;
    private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;
    private readonly IScreenShareManager _screenShareManager = screenShareManager;
    private readonly IMessagesManager _messagesManager = messagesManager;
    private readonly IScreenSharePlayer _screenSharePlayer = screenSharePlayer;
    private readonly ISettingsManager _settingsManager = settingsManager;
    private readonly IAudioManager _audioManager = audioManager;
    private readonly IScreenMonitorManager _screenMonitorManager = screenMonitorManager;

    private const string ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789$&";
    private readonly string _clientVersion = "v1.5.0";

    private TcpClient _tcpMainClient = null!;

    private readonly CancellationTokenSource _receiveCancellationTokenSource = new();
    private Thread? _receiveThread;

    private bool _disconnectInitiated;

    public string? TryConnect(string connectionKey, string username)
    {
        using var tokenSource = new CancellationTokenSource();
        var token = tokenSource.Token;

        try
        {
            string? password = null;
            int idx = connectionKey.IndexOf('?');
            if (idx != -1)
            {
                var left = connectionKey[..idx];
                var right = connectionKey[(idx + 1)..];

                connectionKey = left;
                password = right;
            }

            var (ipAddresses, port) = DecodeServerConnectionKey(connectionKey);

            var endpoint = FindOpenEndpoint(ipAddresses, port, token);
            if (endpoint is var (ip, p))
            {
                _connectionInfo.ServerIP = ip;
                _connectionInfo.ServerPort = p;
            }

            if (_connectionInfo.ServerIP == string.Empty || _connectionInfo.ServerPort == 0)
                throw new IOException(Strings.ServerNotFoundText);

            _tcpMainClient = new(AddressFamily.InterNetwork);
            _tcpMainClient.Connect(_connectionInfo.ServerIP, _connectionInfo.ServerPort);
            _connectionInfo.Port = ((IPEndPoint)_tcpMainClient.Client.LocalEndPoint!).Port;
            _tcpMainClient.ReceiveTimeout = 3000;

            NetworkStream stream = _tcpMainClient.GetStream();

            SendClientVersion(stream, token);
            _cryptoSessionManager.SharedSecretExchange(stream, token);
            VerifyPassword(stream, password, token);
            _cryptoSessionManager.ReceiveAndSetSessionKey(stream, token);
            SetClientInformation(stream, username, token);

            Task.Run(() => StartUdpConnect(token), token);
            ReceivePortsConfirmation(stream, token);

            ReceiveClientsList(stream, token);

            _tcpMainClient.ReceiveTimeout = 0;

            var receiveToken = _receiveCancellationTokenSource.Token;
            _receiveThread = new(() => ReceiveMessages(receiveToken))
            {
                IsBackground = true
            };
            _receiveThread.Start();

            _audioManager.ToggleMicrophoneStatus();

            return null;
        }
        catch (Exception ex)
        {
            tokenSource.Cancel();

            return ex.Message;
        }
    }

    public string? TryReconnect()
    {
        using var tokenSource = new CancellationTokenSource();
        var token = tokenSource.Token;

        _tcpMainClient = new(AddressFamily.InterNetwork);
        
        try
        {
            _tcpMainClient.Connect(_connectionInfo.ServerIP, _connectionInfo.ServerPort);
            _connectionInfo.Port = ((IPEndPoint)_tcpMainClient.Client.LocalEndPoint!).Port;
            _tcpMainClient.ReceiveTimeout = 3000;

            NetworkStream stream = _tcpMainClient.GetStream();

            SendLocalUserId(stream, token);

            Task.Run(() => StartUdpConnect(token), token);
            ReceivePortsConfirmation(stream, token);

            _tcpMainClient.ReceiveTimeout = 0;

            return null;
        }
        catch (Exception ex)
        {
            tokenSource.Cancel();

            return ex.Message;
        }
    }

    private (string ip, int port)? FindOpenEndpoint(IEnumerable<string> ipAddresses, int port, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        var tasks = ipAddresses.Select(ip => Task.Run(() =>
        {
            if (IsIPEndPointOpen(ip, port, cts.Token))
                return (string?)ip;
            return null;
        }, cts.Token)).ToList();

        while (tasks.Count > 0)
        {
            var completed = Task.WhenAny(tasks).GetAwaiter().GetResult();
            tasks.Remove(completed);

            var ip = completed.GetAwaiter().GetResult();
            if (ip is not null)
            {
                cts.Cancel();
                return (ip, port);
            }
        }

        return null;
    }

    private bool IsIPEndPointOpen(string ip, int port, CancellationToken token, int timeout = 1000)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);

            using var client = new TcpClient();
            client.ConnectAsync(ip, port, cts.Token).AsTask().GetAwaiter().GetResult();
            client.ReceiveTimeout = timeout;
            client.SendTimeout = timeout;

            var stream = client.GetStream();

            SendClientVersion(stream, cts.Token);

            return true;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void SendClientVersion(NetworkStream stream, CancellationToken token)
    {
        var data = Encoding.UTF8.GetBytes(_clientVersion);
        _networkUtils.WritePacketAsync(stream, data, token).GetAwaiter().GetResult();

        var answer = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
        if (Encoding.UTF8.GetString(answer).Equals("#PNO"))
            throw new ArgumentException("Server does not support your client version");
    }

    private bool SendLocalUserId(NetworkStream stream, CancellationToken token)
    {
        var data = _cryptoSessionManager.EncryptMessage(BitConverter.GetBytes(_connectionInfo.LocalUserId));
        _networkUtils.WritePacketAsync(stream, data, token).GetAwaiter().GetResult();

        var answer = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
        if (Encoding.UTF8.GetString(_cryptoSessionManager.DecryptMessage(answer)) == "#CTU")
            return true;

        return false;
    }

    private void VerifyPassword(NetworkStream stream, string? password, CancellationToken token)
    {
        password ??= new string([.. Enumerable.Range(0, new Random().Next(5, 20))
                .Select(_ => ALPHABET[new Random().Next(ALPHABET.Length)])]);

        var passwordHash = GetSha256Bytes(password);
        var encryptedPassword = _cryptoSessionManager.EncryptPassword(passwordHash);
        _networkUtils.WritePacketAsync(stream, encryptedPassword, token).GetAwaiter().GetResult();

        var answer = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
        if (!answer.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("#PIC")))
            throw new ArgumentException(Strings.IncorrectPassword);
    }

    private static byte[] GetSha256Bytes(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        return SHA256.HashData(bytes);
    }

    private void StartUdpConnect(CancellationToken token)
    {
        var userId = _connectionInfo.LocalUserId;
        var userIdBytes = BitConverter.GetBytes(userId);
        var encryptedUserIdBytes = _cryptoSessionManager.EncryptMessage(userIdBytes);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        _networkUtils.ConnectToUdp(encryptedUserIdBytes, cts.Token);
    }

    private void ReceivePortsConfirmation(NetworkStream stream, CancellationToken token)
    {
        var packet = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();

        var confirmation = _cryptoSessionManager.DecryptMessage(packet);
        if (!confirmation.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("#UCC")))
        {
            throw new ArgumentException(Strings.ServerNotConfirmUdpText);
        }
    }

    private void SetClientInformation(NetworkStream stream, string username, CancellationToken token)
    {
        try
        {
            _networkUtils.WritePacketAsync(stream, _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes(username)), token).GetAwaiter().GetResult();
            var encryptedlocalUserId = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
            var localUserId = BitConverter.ToUInt16(_cryptoSessionManager.DecryptMessage(encryptedlocalUserId));
            _connectionInfo.LocalUserId = localUserId;
        }
        catch
        {
            throw new IOException("Failed to set client information");
        }
    }

    private void ReceiveClientsList(NetworkStream stream, CancellationToken token)
    {
        try
        {
            var encryptedCountBytes = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
            var countBytes = _cryptoSessionManager.DecryptMessage(encryptedCountBytes);
            int count = BitConverter.ToInt32(countBytes, 0);

            for (int i = 0; i < count; i++)
            {
                var encryptedData = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
                var data = _cryptoSessionManager.DecryptMessage(encryptedData).AsSpan();

                var userId = BitConverter.ToUInt16(data);
                var username = Encoding.UTF8.GetString(data[2..]);

                _usersInfo.AddUser(userId, username, false, false);
            }

            var encryptedScreenSharerIdBytes = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
            var screenSharerIdBytes = _cryptoSessionManager.DecryptMessage(encryptedScreenSharerIdBytes);
            if (BitConverter.ToUInt16(screenSharerIdBytes) != 0)
            {
                ExecuteCommand("#SSS", screenSharerIdBytes.AsSpan());
            }
        }
        catch
        {
            throw new IOException("Failed to receive information about active clients");
        }
    }

    public void SendMessage(string message)
    {
        var headerBytes = Encoding.UTF8.GetBytes("MSG");
        var encryptedHeader = _cryptoSessionManager.EncryptMessage(headerBytes);

        var commandBytes = Encoding.UTF8.GetBytes("#MSG");
        var userIdBytes = BitConverter.GetBytes(_connectionInfo.LocalUserId);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var result = new byte[commandBytes.Length + userIdBytes.Length + messageBytes.Length];
        Array.Copy(commandBytes, 0, result, 0, commandBytes.Length);
        Array.Copy(userIdBytes, 0, result, commandBytes.Length, userIdBytes.Length);
        Array.Copy(messageBytes, 0, result, commandBytes.Length + userIdBytes.Length, messageBytes.Length);
        var encryptedMessage = _cryptoSessionManager.EncryptMessage(result);

        var packet = new byte[encryptedHeader.Length + encryptedMessage.Length];
        Array.Copy(encryptedHeader, 0, packet, 0, encryptedHeader.Length);
        Array.Copy(encryptedMessage, 0, packet, encryptedHeader.Length, encryptedMessage.Length);

        var stream = _tcpMainClient.GetStream();
        var token = _receiveCancellationTokenSource.Token;

        try
        {
            _networkUtils.WritePacketAsync(stream, packet, token).GetAwaiter().GetResult();
        }
        catch
        {
            throw new IOException("Failed to send message to server");
        }
    }

    public void SendCommand(string command)
    {
        var headerBytes = Encoding.UTF8.GetBytes("CMD");
        var encryptedHeader = _cryptoSessionManager.EncryptMessage(headerBytes);

        var commandBytes = Encoding.UTF8.GetBytes(command);
        var encryptedCommand = _cryptoSessionManager.EncryptMessage(commandBytes);

        var packet = new byte[encryptedHeader.Length + encryptedCommand.Length];
        Array.Copy(encryptedHeader, 0, packet, 0, encryptedHeader.Length);
        Array.Copy(encryptedCommand, 0, packet, encryptedHeader.Length, encryptedCommand.Length);

        var stream = _tcpMainClient.GetStream();
        var token = _receiveCancellationTokenSource.Token;

        try
        {
            _networkUtils.WritePacketAsync(stream, packet, token).GetAwaiter().GetResult();
        }
        catch
        {
            throw new IOException("Failed to send message to server");
        }
    }

    public void CloseConnection()
    {
        try
        {
            SendCommand("#END");
        }
        catch { /* ignore */ }
        finally
        {
            _disconnectInitiated = true;
        }
    }

    private void ReceiveMessages(CancellationToken receiveToken)
    {
        while (true)
        {
            try
            {
                var stream = _tcpMainClient.GetStream();
                var token = _receiveCancellationTokenSource.Token;

                while (!token.IsCancellationRequested)
                {
                    var encryptedData = _networkUtils.ReadPacketAsync(stream, receiveToken).GetAwaiter().GetResult();
                    var data = _cryptoSessionManager.DecryptMessage(encryptedData);
                    var command = Encoding.UTF8.GetString(data[..4]);

                    if (command.StartsWith('#'))
                    {
                        ExecuteCommand(command, data.AsSpan()[4..]);
                    }
                    else
                    {
                        throw new IOException("Unknown command");
                    }
                }
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                if (_disconnectInitiated)
                    break;

                if (TryReconnect() != null)
                {
                    Application.Current.Dispatcher.Invoke(() => 
                        MessageBox.Show(Strings.ConnectionLostText, Strings.ConnectionErrorText, MessageBoxButton.OK, MessageBoxImage.Error));
                    break;
                }
            }
            catch
            {
                break;
            }
        }

        _connectionInfo.InvokeClientDisconnected();
    }

    public void ToggleScreenShare()
    {
        if (!_screenShareManager.IsSharing)
        {
            SendCommand("#CIS");
        }
        else
        {
            SendCommand("#STO");
            _screenShareManager.StopSharing();
        }
    }

    public async Task StartMonitoringScreenShareErrors()
    {
        var token = _receiveCancellationTokenSource.Token;

        try
        {
            while (_screenShareManager.IsSharing && !token.IsCancellationRequested)
            {
                if (_screenShareManager.ThreadException != null)
                {
                    ToggleScreenShare();

                    var exceptionMessage = _screenShareManager.ThreadException.Message;
                    _screenShareManager.ThreadException = null;

                    Application.Current.Dispatcher.Invoke(() =>
                        MessageBox.Show(exceptionMessage, Strings.ScreenSharingErrorText, MessageBoxButton.OK, MessageBoxImage.Error));

                    break;
                }

                await Task.Delay(100, token);
            }
        }
        catch { }
    }

    private void ExecuteCommand(string command, ReadOnlySpan<byte> body)
    {
        switch (command)
        {
            case var _ when command.Equals("#MSG"):

                _messagesManager.AddMessage(BitConverter.ToUInt16(body[..2]), Encoding.UTF8.GetString(body[2..]));

                break;

            // Ping Check Command
            case var _ when command.Equals("#PCC"):

                SendMessage("#PCC");

                break;

            // Add new user
            case var _ when command.Equals("#ADD"):

                var userIdADD = BitConverter.ToUInt16(body[..2]);
                var usernameADD = Encoding.UTF8.GetString(body[2..]);

                _usersInfo.AddUser(userIdADD, usernameADD, false, false);

                break;

            // Remove user
            case var _ when command.Equals("#REM"):

                var userIdREM = BitConverter.ToUInt16(body);

                _usersInfo.RemoveUser(userIdREM);

                break;

            // Yes You Can
            case var _ when command.Equals("#YYC"):

                if (_screenMonitorManager.SelectedMonitor == null)
                    return;

                var adapterIndex = _screenMonitorManager.SelectedMonitor.AdapterIndex;
                var outputIndex = _screenMonitorManager.SelectedMonitor.OutputIndex;

                _screenShareManager.StartSharing(adapterIndex, outputIndex, _settingsManager.ShareScreenWithAudio);
                _ = StartMonitoringScreenShareErrors();

                break;

            // No You Can't
            case var _ when command.Equals("#NYC"):

                Application.Current.Dispatcher.Invoke(() =>
                    MessageBox.Show(Strings.OnlyOneScreenShareText, Strings.ScreenSharingErrorText, MessageBoxButton.OK, MessageBoxImage.Information));

                break;

            // Show Screen Sharing
            case var _ when command.Equals("#SSS"):

                var userIdSSS = BitConverter.ToUInt16(body);

                _screenSharePlayer.ScreenShareUsername = _usersInfo.GetUsernameByUserId(userIdSSS);
                _usersInfo.UpdateScreenSharingState(userIdSSS, true);
                if (_settingsManager.AutoOpenScreenShareWindow)
                    Application.Current.Dispatcher.Invoke(() =>
                        _screenSharePlayer.IsWindowVisible = true);

                break;

            // Close Screen Sharing
            case var _ when command.Equals("#CSS"):

                var userIdCSS = BitConverter.ToUInt16(body);

                Application.Current.Dispatcher.Invoke(() =>
                    _screenSharePlayer.IsWindowVisible = false);

                _screenSharePlayer.IsKeyFrameInitialized = false;
                _screenSharePlayer.ClearLastKeyFrame();
                _usersInfo.UpdateScreenSharingState(userIdCSS, false);

                break;

            default:

                break;
        }
    }

    private static (List<string> ipAddresses, int port) DecodeServerConnectionKey(string key)
    {
        BigInteger value = 0;
        foreach (char c in key)
        {
            int digit = ALPHABET.IndexOf(c);
            if (digit == -1)
                throw new ArgumentException(Strings.InvalidCharacterInKeyText);
            value = value * 64 + digit;
        }

        int port = (int)(value & 0xFFFF);
        value >>= 16;

        byte[] globalIpBytes = new byte[4];
        for (int i = 3; i >= 0; i--)
        {
            globalIpBytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        byte[] localIpBytes = new byte[4];
        for (int i = 3; i >= 0; i--)
        {
            localIpBytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        string localIp = new IPAddress(localIpBytes).ToString();
        string globalIp = new IPAddress(globalIpBytes).ToString();

        var ipAddresses = new List<string> { "127.0.0.1", localIp, globalIp };

        return (ipAddresses, port);
    }

    public void Dispose()
    {
        _receiveCancellationTokenSource?.Cancel();
        _tcpMainClient?.Dispose();

        GC.SuppressFinalize(this);
    }
}