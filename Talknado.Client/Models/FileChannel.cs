using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Talknado.Client.Models.Helpers.Network;

namespace Talknado.Client.Models;

/// <summary>Пакет действителен только на время вызова.</summary>
public delegate void FilePacketHandler(ReadOnlySpan<byte> packet);

public interface IFileChannel
{
    FilePacketHandler? PacketReceived { get; set; }
    void Start(string host, int port);
    void Stop();
    void Send(ushort targetUserId, ReadOnlySpan<byte> body);
}

/// <summary>
/// Канал файлов — отдельное TCP-соединение на тот же порт сервера, что и главное.
/// Обрыв канала не трогает сообщения, голос и демонстрацию: канал переподключается
/// сам, а недополученное получатель запрашивает заново.
/// </summary>
public sealed class FileChannel(INetworkUtils networkUtils, ICryptoSessionManager cryptoSessionManager,
    IConnectionInfo connectionInfo) : IFileChannel, IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;
    private readonly IConnectionInfo _connectionInfo = connectionInfo;

    private CancellationTokenSource? _cts;
    private volatile TcpClient? _client;

    public FilePacketHandler? PacketReceived { get; set; }

    public void Start(string host, int port)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(host, port, token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _client?.Close();
        _client = null;
    }

    /// <summary>Пакет: зашифрованный номер адресата и отдельно зашифрованное тело.</summary>
    public void Send(ushort targetUserId, ReadOnlySpan<byte> body)
    {
        var client = _client ?? throw new IOException("File channel is not connected");

        Span<byte> target = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(target, targetUserId);

        var packet = PooledBuffer.Rent(CryptoSessionManager.EncryptedLength(target.Length) + CryptoSessionManager.EncryptedLength(body.Length));
        try
        {
            int length = _cryptoSessionManager.Encrypt(target, packet.Array);
            length += _cryptoSessionManager.Encrypt(body, packet.Array.AsSpan(length));

            _networkUtils.WritePacketAsync(client.GetStream(), packet.Array.AsMemory(0, length), CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            packet.Return();
        }
    }

    private async Task RunAsync(string host, int port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Без Нейгла: иначе хвост каждой порции ждёт подтверждения, а Windows
            // подтверждает с задержкой до 200 мс — на каждые 512 КБ простой.
            var client = new TcpClient { NoDelay = true };
            // Без проверки живости простаивающий канал молча умирает за NAT или
            // межсетевым экраном: обе стороны считают его открытым, и запросы
            // файлов уходят в никуда. Проба раз в 15 с не даёт забыть соединение,
            // а три пропущенных ответа подряд рвут его — канал пересоздаётся.
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

            try
            {
                await client.ConnectAsync(host, port, token);
                var stream = client.GetStream();

                var prefix = Encoding.UTF8.GetBytes("#FCH");
                var userId = _cryptoSessionManager.EncryptMessage(BitConverter.GetBytes(_connectionInfo.LocalUserId));
                byte[] hello = [.. prefix, .. userId];
                await _networkUtils.WritePacketAsync(stream, hello, token);

                var answer = await _networkUtils.ReadPacketAsync(stream, token);
                if (Encoding.UTF8.GetString(answer) != "#ZBS")
                    return;

                _client = client;

                while (!token.IsCancellationRequested)
                {
                    var packet = await _networkUtils.ReadPooledPacketAsync(stream, token);
                    try
                    {
                        Deliver(packet);
                    }
                    finally
                    {
                        packet.Return();
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || NetworkExceptionHelper.IsNetworkException(ex)) { }
            catch { /* повреждённый пакет: соединение пересоздаётся */ }
            finally
            {
                if (_client == client)
                    _client = null;
                client.Close();
            }

            try
            {
                await Task.Delay(ReconnectDelay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Deliver(PooledBuffer packet)
    {
        var plain = PooledBuffer.Rent(packet.Length);
        try
        {
            int length = _cryptoSessionManager.Decrypt(packet.Span, plain.Array);
            PacketReceived?.Invoke(plain.Array.AsSpan(0, length));
        }
        finally
        {
            plain.Return();
        }
    }

    public void Dispose() => Stop();
}
