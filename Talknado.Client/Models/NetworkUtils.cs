using LiteNetLib;
using LiteNetLib.Utils;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Talknado.Client.Models;

public interface INetworkUtils
{
    void ConnectToUdp(byte[] udpKey, CancellationToken token);
    Task SendAudioPacketAsync(byte[] packet);
    Task<byte[]> ReceiveAudioPacketAsync(CancellationToken token);
    Task SendScreenSharePacketAsync(byte[] packet);
    Task<byte[]> ReceiveScreenSharePacketAsync(CancellationToken token);
    Task WritePacketAsync(NetworkStream stream, byte[] data, CancellationToken token);
    Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token);
}

public class NetworkUtils(IConnectionInfo connectionInfo) : INetworkUtils, INetEventListener, IDisposable
{
    private readonly IConnectionInfo _connectionInfo = connectionInfo;

    private NetManager? _netManager;
    private NetPeer? _serverPeer;

    private readonly Queue<byte[]> _audioPackets = new();
    private readonly Queue<byte[]> _screenSharePackets = new();
    private readonly SemaphoreSlim _audioSemaphore = new(0);
    private readonly SemaphoreSlim _screenShareSemaphore = new(0);
    private readonly object _audioLock = new();
    private readonly object _screenLock = new();

    private const byte AudioChannel = 0;
    private const byte ScreenShareChannel = 1;

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
            ChannelsCount = 2,
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
                Console.WriteLine("[Client] Successfully connected!");
                return;
            }

            Thread.Sleep(50);
        }
    }

    private async Task PollLoop(CancellationToken token)
    {
        while (_netManager != null && !token.IsCancellationRequested)
        {
            _netManager?.PollEvents();
            await Task.Delay(10, token);
        }
    }

    public async Task SendAudioPacketAsync(byte[] packet)
    {
        if (_serverPeer?.ConnectionState != ConnectionState.Connected)
            return;

        try
        {
            var writer = new NetDataWriter();
            writer.Put(packet);
            _serverPeer.Send(writer, AudioChannel, DeliveryMethod.ReliableSequenced);
            _netManager?.PollEvents();
        }
        catch { /* ignore */ }

        await Task.CompletedTask;
    }

    public async Task<byte[]> ReceiveAudioPacketAsync(CancellationToken token)
    {
        await _audioSemaphore.WaitAsync(token);

        lock (_audioLock)
        {
            return _audioPackets.Dequeue();
        }
    }

    public async Task SendScreenSharePacketAsync(byte[] packet)
    {
        if (_serverPeer?.ConnectionState != ConnectionState.Connected)
            return;

        try
        {
            var writer = new NetDataWriter();
            writer.Put(packet);
            _serverPeer.Send(writer, ScreenShareChannel, DeliveryMethod.ReliableOrdered);
            _netManager?.PollEvents();
        }
        catch { /* ignore */ }

        await Task.CompletedTask;
    }

    public async Task<byte[]> ReceiveScreenSharePacketAsync(CancellationToken token)
    {
        await _screenShareSemaphore.WaitAsync(token);

        lock (_screenLock)
        {
            return _screenSharePackets.Dequeue();
        }
    }

    public async Task WritePacketAsync(NetworkStream stream, byte[] data, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanWrite)
            throw new IOException("Stream is not writable");

        byte[] lengthPrefix = BitConverter.GetBytes(data.Length);

        await stream.WriteAsync(lengthPrefix, token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
            throw new IOException("Stream is not readable");

        byte[] lengthBuffer = await ReadExactBytesAsync(stream, 4, token).ConfigureAwait(false);
        int length = BitConverter.ToInt32(lengthBuffer, 0);

        if (length <= 0)
            throw new IOException("Connection closed");

        return await ReadExactBytesAsync(stream, length, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactBytesAsync(NetworkStream stream, int length, CancellationToken receiveToken)
    {
        byte[] buffer = new byte[length];
        int totalRead = 0;

        while (totalRead < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), receiveToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("Connection closed during receive");

            totalRead += read;
        }

        return buffer;
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
        var data = reader.GetRemainingBytes();

        if (channelNumber == AudioChannel)
        {
            lock (_audioLock)
            {
                _audioPackets.Enqueue(data);
            }
            _audioSemaphore.Release();
        }
        else if (channelNumber == ScreenShareChannel)
        {
            lock (_screenLock)
            {
                _screenSharePackets.Enqueue(data);
            }
            _screenShareSemaphore.Release();
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