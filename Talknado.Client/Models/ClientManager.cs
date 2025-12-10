using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
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
        IWindowsState windowsState,
        ISettingsManager settingsManager) : IClientManager, IDisposable
    {
        private readonly IUsersInfo _usersInfo = usersInfo;
        private readonly INetworkUtils _networkUtils = networkUtils;
        private readonly IConnectionInfo _connectionInfo = connectionInfo;
        private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;
        private readonly IScreenShareManager _screenShareManager = screenShareManager;
        private readonly IMessagesManager _messagesManager = messagesManager;
        private readonly IScreenSharePlayer _screenSharePlayer = screenSharePlayer;
        private readonly IWindowsState _windowsState = windowsState;
        private readonly ISettingsManager _settingsManager = settingsManager;

        private const string ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789$&";

        private readonly string _clientVersion = "v1.2.3";
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

                foreach (var ipAddress in ipAddresses)
                {
                    if (IsIPEndPointOpen(ipAddress, port))
                    {
                        _connectionInfo.ServerIP = ipAddress;
                        _connectionInfo.ServerPort = port;

                        break;
                    }
                }

                if (_connectionInfo.ServerIP == string.Empty || _connectionInfo.ServerPort == 0)
                    throw new IOException("Сервер не найден");

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
                throw new ArgumentException("Incorrect password");
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
            var encryptedUserId = _cryptoSessionManager.EncryptMessage(userIdBytes);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            _networkUtils.ConnectToUdp(encryptedUserId, cts.Token);
        }

        private void ReceivePortsConfirmation(NetworkStream stream, CancellationToken token)
        {
            var packet = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();

            var confirmation = _cryptoSessionManager.DecryptMessage(packet);
            if (!confirmation.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("#UCC")))
            {
                throw new ArgumentException("Server not confirm UDP connection");
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
                var commandBytes = Encoding.UTF8.GetBytes("#MSG");
                var userIdBytes = BitConverter.GetBytes(_connectionInfo.LocalUserId);
                var messageBytes = Encoding.UTF8.GetBytes(message);
                var result = new byte[commandBytes.Length + userIdBytes.Length + messageBytes.Length];

                Array.Copy(commandBytes, 0, result, 0, commandBytes.Length);
                Array.Copy(userIdBytes, 0, result, commandBytes.Length, userIdBytes.Length);
                Array.Copy(messageBytes, 0, result, commandBytes.Length + userIdBytes.Length, messageBytes.Length);

                var encryptedMessage = _cryptoSessionManager.EncryptMessage(result);
                var stream = _tcpMainClient.GetStream();
                var token = _receiveCancellationTokenSource.Token;

                _networkUtils.WritePacketAsync(stream, encryptedMessage, token).GetAwaiter().GetResult();
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
                var stream = _tcpMainClient.GetStream();
                var encryptedCommand = _cryptoSessionManager.EncryptMessage(Encoding.UTF8.GetBytes("#END"));
                _networkUtils.WritePacketAsync(stream, encryptedCommand, CancellationToken.None);
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
                        MessageBox.Show("Потеряно соединение с сервером", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
                        break;
                    }
                }
                catch
                {
                    break;
                }
            }

            _windowsState.InvokeClientDisconnected();
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

                    var userIdREM = BitConverter.ToUInt16(body);

                    _usersInfo.RemoveUser(userIdREM);

                    break;

                // Start Screen Sharing
                case var _ when command.Equals("#YYC"):

                    _screenShareManager.StartSharing(_settingsManager.ScreenShareWithAudio);

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
                    {
                        _screenSharePlayer.IsWindowVisible = false;
                    });

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
                    throw new ArgumentException("Недопустимый символ в ключе");
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
}
