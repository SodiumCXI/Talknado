using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Talknado.Server.Core
{
    public interface INetworkUtils
    {
        void Start(CancellationToken token);
        Task BroadcastAudioPacket(ushort currentUserId, byte[] data);
        Task<(byte[], ushort)?> ReceiveAudioPacketAsync(CancellationToken token);
        Task BroadcastScreenSharePacket(ushort currentUserId, byte[] data);
        Task<(byte[], ushort)?> ReceiveScreenSharePacketAsync(CancellationToken token);
        Task BroadcastMessage(ushort currentUserId, byte[] data, CancellationToken token);
        Task WritePacketAsync(NetworkStream stream, byte[] data, CancellationToken token);
        Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token);
    }

    public class NetworkUtils : INetworkUtils, INetEventListener, IDisposable
    {
        private readonly IUsersInfo _usersInfo;
        private readonly IServerInfo _serverInfo;
        private readonly ICryptoSessionManager _cryptoSessionManager;

        private readonly NetManager _netManager;

        private readonly Dictionary<IPEndPoint, ushort> _pendingConnections = [];

        private readonly Queue<(byte[], ushort)> _audioPackets = new();
        private readonly Queue<(byte[], ushort)> _screenSharePackets = new();
        private readonly SemaphoreSlim _audioSemaphore = new(0);
        private readonly SemaphoreSlim _screenSemaphore = new(0);
        private readonly object _audioLock = new();
        private readonly object _screenLock = new();

        private const byte AudioChannel = 0;
        private const byte ScreenShareChannel = 1;

        public NetworkUtils(IUsersInfo usersInfo, IServerInfo serverInfo, ICryptoSessionManager cryptoSessionManager)
        {
            _usersInfo = usersInfo;
            _serverInfo = serverInfo;
            _cryptoSessionManager = cryptoSessionManager;

            _netManager = new NetManager(this)
            {
                AutoRecycle = true,
                ChannelsCount = 2
            };
        }

        public void Start(CancellationToken token)
        {
            _netManager.Start(IPAddress.Any, IPAddress.IPv6Any, _serverInfo.Port);

            _ = Task.Run(() => PollLoop(token), token);
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                _netManager?.PollEvents();
                await Task.Delay(10, token);
            }
        }

        public async Task BroadcastAudioPacket(ushort currentUserId, byte[] data)
        {
            foreach (var peer in _usersInfo.GetNetPeers(currentUserId))
            {
                await SendAudioPacketAsync(data, peer);
            }
        }

        private static async Task SendAudioPacketAsync(byte[] packet, NetPeer peer)
        {
            try
            {
                var writer = new NetDataWriter();
                writer.Put(packet);
                peer.Send(writer, AudioChannel, DeliveryMethod.ReliableSequenced);
            }
            catch { /* ignore */ }

            await Task.CompletedTask;
        }

        public async Task<(byte[], ushort)?> ReceiveAudioPacketAsync(CancellationToken token)
        {
            await _audioSemaphore.WaitAsync(token);

            lock (_audioLock)
            {
                return _audioPackets.Dequeue();
            }
        }

        public async Task BroadcastScreenSharePacket(ushort currentUserId, byte[] data)
        {
            foreach (var peer in _usersInfo.GetNetPeers(currentUserId))
            {
                await SendScreenSharePacketAsync(data, peer);
            }
        }

        private static async Task SendScreenSharePacketAsync(byte[] packet, NetPeer peer)
        {
            try
            {
                var writer = new NetDataWriter();
                writer.Put(packet);
                peer.Send(writer, ScreenShareChannel, DeliveryMethod.ReliableOrdered);
            }
            catch { /* ignore */ }

            await Task.CompletedTask;
        }

        public async Task<(byte[], ushort)?> ReceiveScreenSharePacketAsync(CancellationToken token)
        {
            await _screenSemaphore.WaitAsync(token);

            lock (_screenLock)
            {
                return _screenSharePackets.Dequeue();
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

        public async Task WritePacketAsync(NetworkStream stream, byte[] data, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (!stream.CanWrite)
                throw new IOException("Stream is not writable");

            byte[] lengthPrefix = BitConverter.GetBytes(data.Length);

            await stream.WriteAsync(lengthPrefix, token);
            await stream.WriteAsync(data, token);
        }

        public async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (!stream.CanRead)
                throw new IOException("Stream is not readable");

            byte[] lengthBuffer = await ReadExactBytesAsync(stream, 4, token);
            int length = BitConverter.ToInt32(lengthBuffer, 0);

            if (length <= 0)
                throw new IOException("Connection closed");

            return await ReadExactBytesAsync(stream, length, token);
        }

        private static async Task<byte[]> ReadExactBytesAsync(NetworkStream stream, int length, CancellationToken receiveToken)
        {
            byte[] buffer = new byte[length];
            int totalRead = 0;

            while (totalRead < length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), receiveToken);
                if (read == 0)
                    throw new IOException("Connection closed during receive");

                totalRead += read;
            }

            return buffer;
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
            var data = reader.GetRemainingBytes();
            var userId = _usersInfo.FindUserIdByNetPeer(peer);

            if (userId == 0)
                return;

            if (channelNumber == AudioChannel)
            {
                lock (_audioLock)
                {
                    _audioPackets.Enqueue((data, userId));
                }
                _audioSemaphore.Release();
            }
            else if (channelNumber == ScreenShareChannel)
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

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
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
}