using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using LiteNetLib;
using Talknado.Server.Core.Helpers;

namespace Talknado.Server.Core;

public interface INetworkUtils
{
    void Start(CancellationToken token);
    void BroadcastAudioPacket(ushort currentUserId, ReadOnlySpan<byte> data);

    /// <summary>Пакет из пула: вызывающий возвращает его.</summary>
    (PooledBuffer Packet, ushort UserId) ReceiveAudioPacket(CancellationToken token);

    /// <summary>Заголовок меняется на месте: признак Resync у каждого адресата свой.</summary>
    void BroadcastScreenSharePacket(ushort currentUserId, Span<byte> packet);

    /// <summary>Пакет из пула: вызывающий возвращает его.</summary>
    (PooledBuffer Packet, ushort UserId) ReceiveScreenSharePacket(CancellationToken token);

    Task BroadcastMessage(ushort currentUserId, byte[] data, CancellationToken token);
    Task WritePacketAsync(NetworkStream stream, ReadOnlyMemory<byte> data, CancellationToken token);
    Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token);

    /// <summary>Пакет из пула: вызывающий возвращает его.</summary>
    ValueTask<PooledBuffer> ReadPooledPacketAsync(NetworkStream stream, CancellationToken token);
}

public class NetworkUtils : INetworkUtils, INetEventListener, IDisposable
{
    private readonly IUsersInfo _usersInfo;
    private readonly IServerInfo _serverInfo;
    private readonly ICryptoSessionManager _cryptoSessionManager;

    private readonly NetManager _netManager;

    private readonly Dictionary<IPEndPoint, ushort> _pendingConnections = [];

    private readonly Queue<(PooledBuffer, ushort)> _audioPackets = new();
    private readonly Queue<(PooledBuffer, ushort)> _screenSharePackets = new();
    private readonly SemaphoreSlim _audioSemaphore = new(0);
    private readonly SemaphoreSlim _screenSemaphore = new(0);
    private readonly object _audioLock = new();
    private readonly object _screenLock = new();

    // Пишут в один поток из нескольких мест сразу: команды, сообщения,
    // пересылка файлов.
    private readonly ConditionalWeakTable<NetworkStream, SemaphoreSlim> _writeLocks = new();

    private const byte AudioChannel = 0;
    private const byte FirstScreenShareChannel = 1;

    // Окно надёжного канала LiteNetLib — 64 пакета, одного канала на 1080p60 мало.
    public const int ScreenShareChannels = 8;

    // Сверх этой очереди к адресату кадры ему не уходят до следующего опорного.
    private const int MaxScreenShareBacklog = 256;
    private readonly HashSet<ushort> _screenShareSkipping = [];

    public NetworkUtils(IUsersInfo usersInfo, IServerInfo serverInfo, ICryptoSessionManager cryptoSessionManager)
    {
        _usersInfo = usersInfo;
        _serverInfo = serverInfo;
        _cryptoSessionManager = cryptoSessionManager;

        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            ChannelsCount = 1 + ScreenShareChannels
        };
    }

    public void Start(CancellationToken token)
    {
        _netManager.Start(IPAddress.Any, IPAddress.IPv6Any, _serverInfo.Port);

        _ = Task.Run(() => PollLoop(token), token);
    }

    // Один таймер на весь цикл: Task.Delay заводил бы новый сто раз в секунду.
    private async Task PollLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        do
        {
            _netManager?.PollEvents();
        }
        while (await timer.WaitForNextTickAsync(token));
    }

    public void BroadcastAudioPacket(ushort currentUserId, ReadOnlySpan<byte> data) =>
        Broadcast(currentUserId, data, AudioChannel, DeliveryMethod.ReliableSequenced);

    // Зовётся из одного потока пересылки демонстрации.
    public void BroadcastScreenSharePacket(ushort currentUserId, Span<byte> packet)
    {
        if (!ScreenFrame.TryRead(packet, out var number, out var flags))
            return;

        bool key = (flags & ScreenFrame.Key) != 0;
        var channel = (byte)(FirstScreenShareChannel + number % ScreenShareChannels);

        foreach (var (userId, peer) in _usersInfo.GetPeers())
        {
            if (userId == currentUserId)
                continue;

            int backlog = 0;
            for (int i = 0; i < ScreenShareChannels; i++)
                backlog += peer.GetPacketsCountInReliableQueue((byte)(FirstScreenShareChannel + i), true);

            var outgoing = flags;
            if (_screenShareSkipping.Contains(userId))
            {
                if (!key || backlog > MaxScreenShareBacklog / 2)
                    continue;

                _screenShareSkipping.Remove(userId);
                outgoing |= ScreenFrame.Resync;
            }
            else if (backlog > MaxScreenShareBacklog)
            {
                _screenShareSkipping.Add(userId);
                continue;
            }

            ScreenFrame.SetFlags(packet, outgoing);
            try
            {
                peer.Send(packet, channel, DeliveryMethod.ReliableOrdered);
            }
            catch { /* ignore */ }
        }
    }

    private void Broadcast(ushort currentUserId, ReadOnlySpan<byte> data, byte channel, DeliveryMethod method)
    {
        foreach (var (userId, peer) in _usersInfo.GetPeers())
        {
            if (userId == currentUserId)
                continue;

            try
            {
                peer.Send(data, channel, method);
            }
            catch { /* ignore */ }
        }
    }

    public (PooledBuffer Packet, ushort UserId) ReceiveAudioPacket(CancellationToken token) =>
        Dequeue(_audioSemaphore, _audioLock, _audioPackets, token);

    public (PooledBuffer Packet, ushort UserId) ReceiveScreenSharePacket(CancellationToken token) =>
        Dequeue(_screenSemaphore, _screenLock, _screenSharePackets, token);

    private static (PooledBuffer, ushort) Dequeue(SemaphoreSlim signal, object gate, Queue<(PooledBuffer, ushort)> queue, CancellationToken token)
    {
        signal.Wait(token);

        lock (gate)
        {
            return queue.Dequeue();
        }
    }

    public async Task BroadcastMessage(ushort currentUserId, byte[] data, CancellationToken token)
    {
        var streamsWithId = _usersInfo.GetUserStreamsWithId(currentUserId);

        foreach (var item in streamsWithId)
        {
            try
            {
                await WritePacketAsync(item.Item1, data, token);
            }
            catch (IOException)
            {
                _usersInfo.DisconnectUser(item.Item2, true);
            }
        }
    }

    // Длина и тело уходят одной записью из одного буфера.
    public async Task WritePacketAsync(NetworkStream stream, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanWrite)
            throw new IOException("Stream is not writable");

        var packet = PooledBuffer.Rent(sizeof(int) + data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.Array, data.Length);
        data.Span.CopyTo(packet.Array.AsSpan(sizeof(int)));

        var writeLock = _writeLocks.GetValue(stream, _ => new SemaphoreSlim(1, 1));

        await writeLock.WaitAsync(token);
        try
        {
            await stream.WriteAsync(packet.Memory, token);
        }
        finally
        {
            writeLock.Release();
            packet.Return();
        }
    }

    public async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token)
    {
        var packet = await ReadPooledPacketAsync(stream, token);
        try
        {
            return packet.Span.ToArray();
        }
        finally
        {
            packet.Return();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<PooledBuffer> ReadPooledPacketAsync(NetworkStream stream, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
            throw new IOException("Stream is not readable");

        int length;
        var lengthBuffer = PooledBuffer.Rent(sizeof(int));
        try
        {
            await stream.ReadExactlyAsync(lengthBuffer.Memory, token);
            length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer.Array);
        }
        finally
        {
            lengthBuffer.Return();
        }

        if (length <= 0)
            throw new IOException("Connection closed");

        var packet = PooledBuffer.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(packet.Memory, token);
            return packet;
        }
        catch
        {
            packet.Return();
            throw;
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        if (_pendingConnections.TryGetValue(IPEndPoint.Parse($"{peer.Address}:{peer.Port}"), out var userId))
        {
            _usersInfo.UpdateUserPeer(userId, peer);
            _pendingConnections.Remove(peer);
        }
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        var userId = _usersInfo.FindUserIdByNetPeer(peer);
        if (userId != 0)
        {
            _usersInfo.DisconnectUser(userId, true);
        }

        _pendingConnections.Remove(peer);
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (channelNumber > ScreenShareChannels)
            return;

        var userId = _usersInfo.FindUserIdByNetPeer(peer);
        if (userId == 0)
            return;

        var data = PooledBuffer.CopyOf(reader.GetRemainingBytesSegment().AsSpan());

        if (channelNumber == AudioChannel)
        {
            lock (_audioLock)
            {
                _audioPackets.Enqueue((data, userId));
            }
            _audioSemaphore.Release();
        }
        else
        {
            lock (_screenLock)
            {
                _screenSharePackets.Enqueue((data, userId));
            }
            _screenSemaphore.Release();
        }
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    // NetPeer.Ping не годится: потерянный ответ LiteNetLib усредняет как замер
    // в секунду. latency — половина времени туда и обратно.
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        _usersInfo.AddPingSample(peer, latency * 2);
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        var key = request.Data.GetRemainingBytes();
        ushort userId;

        try
        {
            var userIdBytes = _cryptoSessionManager.DecryptMessage(key);
            userId = BitConverter.ToUInt16(userIdBytes);
        }
        catch
        {
            request.Reject();
            return;
        }

        if (userId != 0 && _usersInfo.HasClient(userId))
        {
            _pendingConnections[request.RemoteEndPoint] = userId;
            request.Accept();
        }
        else
        {
            request.Reject();
        }
    }

    public void Dispose()
    {
        _netManager?.Stop();
        _pendingConnections?.Clear();

        GC.SuppressFinalize(this);
    }
}
