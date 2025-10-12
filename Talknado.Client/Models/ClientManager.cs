using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Talknado.Client.Models.Helpers;

namespace Talknado.Client.Models
{
    public interface IClientManager
    {
        string? TryConnect(string connectionKey, string username);
        void SendMessage(string message);
        void ToggleScreenShare();
    }

    public class ClientManager(IUsersInfo usersInfo,
        INetworkUtils networkUtils,
        IConnectionInfo connectionInfo,
        ICryptoSessionManager cryptoSessionManager,
        IScreenShareManager screenShareManager,
        IMessagesManager messagesManager,
        IScreenSharePlayer screenSharePlayer,
        IAudioManager audioManager) : IClientManager, IDisposable
    {
        private readonly IUsersInfo _usersInfo = usersInfo;
        private readonly INetworkUtils _networkUtils = networkUtils;
        private readonly IConnectionInfo _connectionInfo = connectionInfo;
        private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;
        private readonly IScreenShareManager _screenShareManager = screenShareManager;
        private readonly IMessagesManager _messagesManager = messagesManager;
        private readonly IScreenSharePlayer _screenSharePlayer = screenSharePlayer;
        private readonly IAudioManager _audioManager = audioManager;

        private const string ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        private volatile bool _portsConfirmed = false;

        private readonly string _clientVersion = "v1.0.0";
        private TcpClient _tcpMainClient = new(AddressFamily.InterNetwork);

        private readonly CancellationTokenSource _receiveCancellationTokenSource = new();
        private Thread? _receiveThread;

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

                var (serverIP, serverPort) = GetServerIPAndPort(connectionKey);

                if (IsIPEndPointOpen(serverIP, serverPort))
                {
                    _tcpMainClient.Connect(serverIP, serverPort);
                    _tcpMainClient.ReceiveTimeout = 3000;
                }
                else
                    throw new Exception("Сервер недоступен");

                NetworkStream stream = _tcpMainClient.GetStream();

                SendClientVersion(stream, token);
                _cryptoSessionManager.SharedSecretExchange(stream, token);
                VerifyPassword(stream, password, token);
                _cryptoSessionManager.ReceiveAndSetSessionKey(stream, token);
                SetClientInformation(stream, username, token);

                Thread.Sleep(1); // Чтобы сервер успел обработать информацию о клиенте (костыль)

                _networkUtils.ReceiveAndSetServerPorts(serverIP, stream, token).GetAwaiter().GetResult();
                _ = StartUdpPing(token).ConfigureAwait(false);
                ReceivePortsConfirmation(stream, token);

                ReceiveClientsList(stream, token);

                _tcpMainClient.ReceiveTimeout = 0;

                _connectionInfo.ServerIP = serverIP;
                _connectionInfo.ServerPort = serverPort;

                _audioManager.ToggleMicrophoneStatus();

                var receiveToken = _receiveCancellationTokenSource.Token;
                _receiveThread = new(() => ReceiveMessages(receiveToken))
                {
                    IsBackground = true
                };
                _receiveThread.Start();

                Debug.WriteLine("Connected to server");

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

            _tcpMainClient ??= new(AddressFamily.InterNetwork);
            
            try
            {
                _tcpMainClient.Connect(_connectionInfo.ServerIP, _connectionInfo.ServerPort);
                _tcpMainClient.ReceiveTimeout = 3000;

                NetworkStream stream = _tcpMainClient.GetStream();

                SendLocalUserId(stream, token);

                _ = StartUdpPing(token).ConfigureAwait(false);
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

        private static bool IsIPEndPointOpen(string ip, int port, int timeout = 1000)
        {
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect(ip, port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(timeout);

                if (success)
                {
                    client.EndConnect(result);
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void SendClientVersion(NetworkStream stream, CancellationToken token)
        {
            var data = Encoding.UTF8.GetBytes(_clientVersion);
            _networkUtils.WritePacketAsync(stream, data, token);
        }

        private bool SendLocalUserId(NetworkStream stream, CancellationToken token)
        {
            var data = _cryptoSessionManager.EncryptMessage(BitConverter.GetBytes(_connectionInfo.LocalUserId));
            _networkUtils.WritePacketAsync(stream, data, token).GetAwaiter().GetResult();

            var answer = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
            if (Encoding.UTF8.GetString(_cryptoSessionManager.DecryptMessage(answer)) == "#SYP")
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
                throw new ArgumentException("Incorrect password");
        }

        private static byte[] GetSha256Bytes(string password)
        {
            var bytes = Encoding.UTF8.GetBytes(password);
            return SHA256.HashData(bytes);
        }

        private async Task StartUdpPing(CancellationToken token)
        {
            _portsConfirmed = false;

            var userId = _connectionInfo.LocalUserId;
            var userIdBytes = BitConverter.GetBytes(userId);

            var audioPacket = _cryptoSessionManager.EncryptMessage([.. Encoding.UTF8.GetBytes("#A"), .. userIdBytes]);
            var screenSharePacket = _cryptoSessionManager.EncryptMessage([.. Encoding.UTF8.GetBytes("#S"), .. userIdBytes]);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(3));

            while (!_portsConfirmed && !cts.Token.IsCancellationRequested)
            {
                try
                {
                    await _networkUtils.PingServerAccessEndPoint(audioPacket, screenSharePacket, cts.Token);
                    await Task.Delay(200, cts.Token);
                }
                catch
                {
                    return;
                }
            }
        }

        private void ReceivePortsConfirmation(NetworkStream stream, CancellationToken token)
        {
            var packet = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();

            var confirmation = _cryptoSessionManager.DecryptMessage(packet);
            if (confirmation.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("#APC")))
            {
                _portsConfirmed = true;
                _networkUtils.BindUdpClients();
            }
            else
                throw new ArgumentException("Ports not confirmed by the server");
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
                var countBytes = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
                int count = BitConverter.ToInt32(countBytes, 0);

                for (int i = 0; i < count; i++)
                {
                    var encryptedData = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
                    var data = _cryptoSessionManager.DecryptMessage(encryptedData).AsSpan();

                    ushort userId = BitConverter.ToUInt16(data);
                    string username = Encoding.UTF8.GetString(data[2..]);

                    _usersInfo.AddUser(userId, username, false, false);
                }
            }
            catch
            {
                throw new IOException("Failed to receive information about active clients");
            }
        }

        public void SendMessage(string message)
        {
            try
            {
                var encryptedMessage = _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes($"#MSG{message}"));
                var stream = _tcpMainClient.GetStream();
                var token = _receiveCancellationTokenSource.Token;

                _networkUtils.WritePacketAsync(stream, encryptedMessage, token).GetAwaiter().GetResult();
            }
            catch
            {
                throw new IOException("Failed to send message to server");
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
                    if (TryReconnect() != null)
                    {
                        return;
                    }
                }
                catch
                {
                    return;
                }
            }
        }

        public void ToggleScreenShare()
        {
            var stream = _tcpMainClient.GetStream();
            var token = _receiveCancellationTokenSource.Token;

            if (!_screenShareManager.IsSharing)
            {
                var encryptedMessage = _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes("#CIS"));
                _networkUtils.WritePacketAsync(stream, encryptedMessage, token).GetAwaiter().GetResult();
            }
            else
            {
                var encryptedMessage = _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes("#STO"));
                _networkUtils.WritePacketAsync(stream, encryptedMessage, token).GetAwaiter().GetResult();
                _screenShareManager.StopSharing();
            }
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

                    var userIdREM = BitConverter.ToUInt16(body[2..]);

                    _usersInfo.RemoveUser(userIdREM);

                    break;

                // Start Screen Sharing
                case var _ when command.Equals("#YYC"):

                    _screenShareManager.StartSharing();

                    break;

                // Show Screen Sharing
                case var _ when command.Equals("#SSS"):

                    var userIdSSS = BitConverter.ToUInt16(body);

                    _screenSharePlayer.ScreenShareUsername = _usersInfo.GetUsernameByUserId(userIdSSS);
                    _usersInfo.UpdateScreenSharingState(userIdSSS, true);

                    break;

                // Close Screen Sharing
                case var _ when command.Equals("#CSS"):

                    var userIdCSS = BitConverter.ToUInt16(body);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _screenSharePlayer.IsWindowVisible = false;
                    });

                    _usersInfo.UpdateScreenSharingState(userIdCSS, false);

                    break;

                default:

                    break;
            }
        }

        public static (string, int) GetServerIPAndPort(string connectionKey)
        {
            ulong value = 0;

            foreach (char c in connectionKey)
            {
                value = value * 52 + (ulong)ALPHABET.IndexOf(c);
            }

            int port = (int)(value & 0xFFFF);
            value >>= 16;

            byte[] ipBytes = new byte[4];
            for (int i = 3; i >= 0; i--)
            {
                ipBytes[i] = (byte)(value & 0xFF);
                value >>= 8;
            }

            return (new IPAddress(ipBytes).ToString(), port);
        }

        public void Dispose()
        {
            _receiveCancellationTokenSource?.Cancel();
            _receiveThread?.Join();
            _receiveThread = null;
            _receiveCancellationTokenSource?.Dispose();
            _tcpMainClient?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
