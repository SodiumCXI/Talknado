using LiteNetLib;
using LiteNetLib.Utils;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Talknado.Client.Models.Helpers.Network;

namespace Talknado.Client.Models;

public interface INetworkUtils
{
    void ConnectToUdp(byte[] udpKey, CancellationToken token);
    void SendAudioPacket(ReadOnlySpan<byte> packet);

    /// <summary>Ждёт пакета. Буфер возвращает вызывающий.</summary>
    PooledBuffer ReceiveAudioPacket(CancellationToken token);

    void SendScreenSharePacket(ReadOnlySpan<byte> packet);

    int ScreenShareBacklog { get; }

    /// <summary>Ждёт пакета. Буфер возвращает вызывающий.</summary>
    PooledBuffer ReceiveScreenSharePacket(CancellationToken token);

    Task WritePacketAsync(NetworkStream stream, ReadOnlyMemory<byte> data, CancellationToken token);
    Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token);

    /// <summary>Тело пакета в буфере из пула. Буфер возвращает вызывающий.</summary>
    ValueTask<PooledBuffer> ReadPooledPacketAsync(NetworkStream stream, CancellationToken token);
}

public class NetworkUtils(IConnectionInfo connectionInfo) : INetworkUtils, INetEventListener, IDisposable
{
    private readonly IConnectionInfo _connectionInfo = connectionInfo;

    private NetManager? _netManager;
    private NetPeer? _serverPeer;

    private readonly Queue<PooledBuffer> _audioPackets = new();
    private readonly Queue<PooledBuffer> _screenSharePackets = new();
    private readonly SemaphoreSlim _audioSemaphore = new(0);
    private readonly SemaphoreSlim _screenShareSemaphore = new(0);
    private readonly object _audioLock = new();
    private readonly object _screenLock = new();

    // Пишут в один поток из нескольких мест сразу: сообщения, команды,
    // передача файлов. Префикс длины и тело уходят двумя записями, и без
    // блокировки чужой пакет мог бы вклиниться между ними.
    private readonly ConditionalWeakTable<NetworkStream, SemaphoreSlim> _writeLocks = new();

    private const byte AudioChannel = 0;
    private const byte FirstScreenShareChannel = 1;

    // Обязано совпадать с сервером.
    private const int ScreenShareChannels = 16;

    private CancellationTokenSource? _pollLoopTokenSourse = null!;

    public void ConnectToUdp(byte[] udpKey, CancellationToken token)
    {
        _pollLoopTokenSourse?.Cancel();
        _pollLoopTokenSourse?.Dispose();

        _pollLoopTokenSourse = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pollLoopToken = _pollLoopTokenSourse.Token;

        var serverEndPoint = IPEndPoint.Parse($"{_connectionInfo.ServerIP}:{_connectionInfo.ServerPort}");

        _netManager?.Stop();
        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            ChannelsCount = 1 + ScreenShareChannels,
        };
        _netManager.Start(_connectionInfo.Port);

        _ = Task.Run(() => PollLoop(pollLoopToken), pollLoopToken).ConfigureAwait(false);

        try
        {
            var writer = new NetDataWriter();
            writer.Put(udpKey);
            _serverPeer = _netManager.Connect(serverEndPoint, writer);
        }
        catch
        {
            throw new IOException("Unable connect to UDP");
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !pollLoopToken.IsCancellationRequested)
        {
            if (_serverPeer?.ConnectionState == ConnectionState.Connected)
            {
                return;
            }

            Thread.Sleep(10);
        }
    }

    // Один таймер на весь цикл: Task.Delay заводил бы новый сто раз в секунду.
    private async Task PollLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        do
        {
            _netManager?.PollEvents();
        }
        while (_netManager != null && await timer.WaitForNextTickAsync(token));
    }

    // Unreliable, а не Sequenced: тот выбрасывает пакет, обогнанный следующим, хотя
    // буфер по номерам поставил бы его на место. Такие пакеты всегда приходят на канал 0.
    public void SendAudioPacket(ReadOnlySpan<byte> packet) =>
        Send(packet, AudioChannel, DeliveryMethod.Unreliable);

    public void SendScreenSharePacket(ReadOnlySpan<byte> packet)
    {
        if (ScreenFrame.TryRead(packet, out var header))
            Send(packet, (byte)(FirstScreenShareChannel + (header.Number + header.Index) % ScreenShareChannels), DeliveryMethod.ReliableOrdered);
    }

    public int ScreenShareBacklog
    {
        get
        {
            var peer = _serverPeer;
            if (peer == null)
                return 0;

            int backlog = 0;
            for (int i = 0; i < ScreenShareChannels; i++)
                backlog += peer.GetPacketsCountInReliableQueue((byte)(FirstScreenShareChannel + i), true);
            return backlog;
        }
    }

    private void Send(ReadOnlySpan<byte> packet, byte channel, DeliveryMethod method)
    {
        var peer = _serverPeer;
        if (peer?.ConnectionState != ConnectionState.Connected)
            return;

        try
        {
            peer.Send(packet, channel, method);
            _netManager?.PollEvents();
        }
        catch { /* ignore */ }
    }

    public PooledBuffer ReceiveAudioPacket(CancellationToken token) =>
        Dequeue(_audioSemaphore, _audioLock, _audioPackets, token);

    public PooledBuffer ReceiveScreenSharePacket(CancellationToken token) =>
        Dequeue(_screenShareSemaphore, _screenLock, _screenSharePackets, token);

    private static PooledBuffer Dequeue(SemaphoreSlim signal, object gate, Queue<PooledBuffer> queue, CancellationToken token)
    {
        signal.Wait(token);

        lock (gate)
        {
            return queue.Dequeue();
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

        await writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet.Memory, token).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
            packet.Return();
        }
    }

    public async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token)
    {
        var packet = await ReadPooledPacketAsync(stream, token).ConfigureAwait(false);
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
            await stream.ReadExactlyAsync(lengthBuffer.Memory, token).ConfigureAwait(false);
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
            await stream.ReadExactlyAsync(packet.Memory, token).ConfigureAwait(false);
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
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var data = PooledBuffer.CopyOf(reader.GetRemainingBytesSegment().AsSpan());

        if (channelNumber == AudioChannel)
        {
            lock (_audioLock)
            {
                _audioPackets.Enqueue(data);
            }
            _audioSemaphore.Release();
        }
        else if (channelNumber <= ScreenShareChannels)
        {
            lock (_screenLock)
            {
                _screenSharePackets.Enqueue(data);
            }
            _screenShareSemaphore.Release();
        }
        else
        {
            data.Return();
        }
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
    }
    
    public void Dispose()
    {
        _pollLoopTokenSourse?.Cancel();

        _netManager?.Stop();
        _netManager = null;

        GC.SuppressFinalize(this);
    }
}