using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Talknado.Server.Core.Helpers;

namespace Talknado.Server.Core;

public interface IFileChannel
{
    bool IsHello(byte[] packet, out ushort userId);
    void Accept(TcpClient client, ushort userId);
    void RemoveUser(ushort userId);
}

/// <summary>
/// Канал файлов — отдельное TCP-соединение каждого участника на том же порту.
/// Файлы не стоят в одной очереди с командами и сообщениями главного соединения.
/// <para>
/// Пакет: зашифрованный заголовок 32 байта с адресатом и тело. Тело зашифровано
/// отправителем и уходит адресату как есть. Скорость ограничена общим потоком
/// сервера (<see cref="IServerInfo.FileBandwidth"/>): у каждого адресата своя
/// очередь, а время отправки порции резервируется на общей шкале, поэтому
/// адресаты делят поток поровну.
/// </para>
/// </summary>
public sealed class FileChannel(INetworkUtils networkUtils, ICryptoSessionManager cryptoSessionManager,
    IUsersInfo usersInfo, IServerInfo serverInfo) : IFileChannel, IDisposable
{
    private const int HeaderSize = 32;
    private static readonly byte[] HelloPrefix = Encoding.UTF8.GetBytes("#FCH");

    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly ICryptoSessionManager _cryptoSessionManager = cryptoSessionManager;
    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly IServerInfo _serverInfo = serverInfo;

    private readonly ConcurrentDictionary<ushort, Peer> _peers = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly object _scheduleLock = new();
    private long _nextFreeTicks;

    /// <summary>
    /// Приветствие канала — открытый префикс и зашифрованный номер участника.
    /// Префикс открытый намеренно: сервер прежней сборки примет пакет за номер
    /// версии, ответит отказом и закроет соединение.
    /// </summary>
    public bool IsHello(byte[] packet, out ushort userId)
    {
        userId = 0;
        if (packet.Length <= HelloPrefix.Length || !packet.AsSpan(0, HelloPrefix.Length).SequenceEqual(HelloPrefix))
            return false;

        try
        {
            userId = BitConverter.ToUInt16(_cryptoSessionManager.DecryptMessage(packet[HelloPrefix.Length..]));
            return _usersInfo.HasClient(userId);
        }
        catch
        {
            return false;
        }
    }

    public void Accept(TcpClient client, ushort userId)
    {
        client.ReceiveTimeout = 0;
        // Без Нейгла: хвост каждой порции иначе ждёт отложенного подтверждения
        // получателя, на Windows до 200 мс.
        client.NoDelay = true;
        // Простаивающий канал за NAT умирает молча; проба раз в 15 с держит его,
        // а мёртвый закрывается через 30 с, и место освобождается под новый.
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

        var peer = new Peer(client);
        if (_peers.TryGetValue(userId, out var previous))
            previous.Close();
        _peers[userId] = peer;

        _ = Task.Run(() => ReceiveLoop(userId, peer));
        _ = Task.Run(() => SendLoop(peer));
    }

    public void RemoveUser(ushort userId)
    {
        if (_peers.TryRemove(userId, out var peer))
            peer.Close();
    }

    private async Task ReceiveLoop(ushort userId, Peer peer)
    {
        try
        {
            while (!peer.Token.IsCancellationRequested)
            {
                var packet = await _networkUtils.ReadPooledPacketAsync(peer.Stream, peer.Token);
                bool forwarded = false;
                try
                {
                    if (packet.Length > HeaderSize &&
                        TryReadTarget(packet.Span[..HeaderSize], out var target) &&
                        target != userId && _peers.TryGetValue(target, out var recipient))
                    {
                        forwarded = recipient.Queue.Writer.TryWrite(packet);
                    }
                }
                finally
                {
                    if (!forwarded)
                        packet.Return();
                }
            }
        }
        catch { /* соединение закрыто: клиент переподключит канал сам */ }
        finally
        {
            _peers.TryRemove(new KeyValuePair<ushort, Peer>(userId, peer));
            peer.Close();
        }
    }

    private bool TryReadTarget(ReadOnlySpan<byte> header, out ushort target)
    {
        Span<byte> plain = stackalloc byte[HeaderSize];
        int length = _cryptoSessionManager.Decrypt(header, plain);
        target = length >= sizeof(ushort) ? BitConverter.ToUInt16(plain) : (ushort)0;
        return length >= sizeof(ushort);
    }

    // В очереди лежит пакет целиком, адресату уходит тело без заголовка.
    private async Task SendLoop(Peer peer)
    {
        try
        {
            await foreach (var packet in peer.Queue.Reader.ReadAllAsync(peer.Token))
            {
                try
                {
                    var body = packet.Array.AsMemory(HeaderSize, packet.Length - HeaderSize);

                    var delay = Reserve(body.Length);
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, peer.Token);

                    await _networkUtils.WritePacketAsync(peer.Stream, body, peer.Token);
                }
                finally
                {
                    packet.Return();
                }
            }
        }
        catch { /* адресат ушёл: порции теряются, получатель перезапросит недостающее */ }
        finally
        {
            while (peer.Queue.Reader.TryRead(out var stale))
                stale.Return();
        }
    }

    private TimeSpan Reserve(int bytes)
    {
        long bytesPerSecond = _serverInfo.FileBandwidth;
        if (bytesPerSecond <= 0)
            return TimeSpan.Zero;

        long now = Stopwatch.GetTimestamp();
        long duration = bytes * Stopwatch.Frequency / bytesPerSecond;

        lock (_scheduleLock)
        {
            long start = Math.Max(now, _nextFreeTicks);
            _nextFreeTicks = start + duration;
            return Stopwatch.GetElapsedTime(now, start);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var peer in _peers.Values)
            peer.Close();
        _peers.Clear();
    }

    private sealed class Peer(TcpClient client)
    {
        private readonly CancellationTokenSource _cts = new();

        public NetworkStream Stream { get; } = client.GetStream();
        public CancellationToken Token => _cts.Token;
        public Channel<PooledBuffer> Queue { get; } = Channel.CreateUnbounded<PooledBuffer>(new UnboundedChannelOptions { SingleReader = true });

        public void Close()
        {
            Queue.Writer.TryComplete();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            client.Close();
        }
    }
}
