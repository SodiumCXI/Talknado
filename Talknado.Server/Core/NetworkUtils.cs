using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;

namespace Talknado.Server.Core
{
    public interface INetworkUtils
    {
        Task SendServerPorts(NetworkStream stream, CancellationToken token);
        Task<(byte[], IPEndPoint)?> ReceiveAccessPacketAsync(CancellationToken token);
        Task BroadcastAudioPacket(ushort currentUserId, byte[] data, CancellationToken token);
        Task SendAudioPacketAsync(byte[] packet, IPEndPoint endPoint, CancellationToken token);
        Task<(byte[], ushort)?> ReceiveAudioPacketAsync(CancellationToken token);
        Task BroadcastScreenSharePacket(ushort currentUserId, byte[] data, CancellationToken token);
        Task SendScreenSharePacketAsync(byte[] packet, IPEndPoint endPoint, CancellationToken token);
        Task<(byte[], ushort)?> ReceiveScreenSharePacketAsync(CancellationToken token);
        Task BroadcastMessage(ushort currentUserId, byte[] data, CancellationToken token);
        Task WritePacketAsync(NetworkStream stream, byte[] data, CancellationToken token);
        Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token);
    }

    public class NetworkUtils(IUsersInfo usersInfo) : INetworkUtils
    {
        private readonly UdpClient _udpAccessClient = new(0);
        private readonly UdpClient _udpAudioClient = new(0);
        private readonly UdpClient _udpScreenShareClient = new(0);

        private readonly IUsersInfo _usersInfo = usersInfo;

        public async Task SendServerPorts(NetworkStream stream, CancellationToken token)
        {
            var accessPort = (_udpAccessClient.Client.LocalEndPoint as IPEndPoint)!.Port;
            var audioPort = (_udpAudioClient.Client.LocalEndPoint as IPEndPoint)!.Port;
            var screenSharePort = (_udpScreenShareClient.Client.LocalEndPoint as IPEndPoint)!.Port;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(accessPort);
            writer.Write(audioPort);
            writer.Write(screenSharePort);

            var portsData = ms.ToArray();

            await WritePacketAsync(stream, portsData, token);
        }

        public async Task<(byte[], IPEndPoint)?> ReceiveAccessPacketAsync(CancellationToken token)
        {
            var result = await _udpAccessClient.ReceiveAsync(token);

            if (result.Buffer == null)
                return null;

            return (result.Buffer, result.RemoteEndPoint);
        }

        public async Task BroadcastAudioPacket(ushort currentUserId, byte[] data, CancellationToken token)
        {
            foreach (var whiteEndPoint in _usersInfo.GetAudioEndPoints(currentUserId))
            {
                await SendAudioPacketAsync(data, whiteEndPoint, token);
            }
        }

        public async Task SendAudioPacketAsync(byte[] packet, IPEndPoint endPoint, CancellationToken token)
        {
            try
            {
                await _udpAudioClient.SendAsync(packet, endPoint, token);
            }
            catch { /* ignore */ }
        }

        public async Task<(byte[], ushort)?> ReceiveAudioPacketAsync(CancellationToken token)
        {
            var result = await _udpAudioClient.ReceiveAsync(token);

            IPEndPoint endPoint = result.RemoteEndPoint;

            var userId = _usersInfo.FindUserIdByEndPoint(endPoint);
            if (userId == 0)
                return null;

            return (result.Buffer, userId);
        }

        public async Task BroadcastScreenSharePacket(ushort currentUserId, byte[] data, CancellationToken token)
        {
            foreach (var whiteEndPoint in _usersInfo.GetScreenShareEndPoints(currentUserId))
            {
                await SendScreenSharePacketAsync(data, whiteEndPoint, token);
            }
        }

        public async Task SendScreenSharePacketAsync(byte[] packet, IPEndPoint endPoint, CancellationToken token)
        {
            try
            {
                await _udpScreenShareClient.SendAsync(packet, endPoint, token);
            }
            catch { /* ignore */ }
        }

        public async Task<(byte[], ushort)?> ReceiveScreenSharePacketAsync(CancellationToken token)
        {
            var result = await _udpScreenShareClient.ReceiveAsync(token);

            IPEndPoint endPoint = result.RemoteEndPoint;

            var userId = _usersInfo.FindUserIdByEndPoint(endPoint);
            if (userId == 0)
                return null;

            return (result.Buffer, userId);
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
    }
}
