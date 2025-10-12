using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Talknado.Client.Models;

public interface INetworkUtils
{
    Task ReceiveAndSetServerPorts(string serverIP, NetworkStream stream, CancellationToken token);
    Task PingServerAccessEndPoint(byte[] audioPacket, byte[] screenSharePacket, CancellationToken token);
    void BindUdpClients();
    Task SendAudioPacketAsync(byte[] packet, CancellationToken token);
    Task<byte[]> ReceiveAudioPacketAsync(CancellationToken token);
    Task SendScreenSharePacketAsync(byte[] packet, CancellationToken token);
    Task<byte[]> ReceiveScreenSharePacketAsync(CancellationToken token);
    Task WritePacketAsync(NetworkStream stream, byte[] data, CancellationToken token);
    Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token);
}

public class NetworkUtils : INetworkUtils
{
    private IPEndPoint? _serverAccessEndPoint;
    private IPEndPoint? _serverAudioEndPoint;
    private IPEndPoint? _serverScreenShareEndPoint;

    private UdpClient _udpAudioClient = new(0);
    private UdpClient _udpScreenShareClient = new(0);

    public async Task ReceiveAndSetServerPorts(string serverIP, NetworkStream stream, CancellationToken token)
    {
        var portsData = await ReadPacketAsync(stream, token);

        using var ms = new MemoryStream(portsData);
        using var reader = new BinaryReader(ms);

        var accessPort = reader.ReadInt32();
        var audioPort = reader.ReadInt32();
        var screenSharePort = reader.ReadInt32();

        SetServerEndPoints(serverIP, accessPort, audioPort, screenSharePort);
    }

    private void SetServerEndPoints(string serverIP, int accessPort, int audioPort, int screenSharePort)
    {
        var serverAccessEndPoint = IPEndPoint.Parse($"{serverIP}:{accessPort}");
        var serverAudioEndPoint = IPEndPoint.Parse($"{serverIP}:{audioPort}");
        var serverScreenShareEndPoint = IPEndPoint.Parse($"{serverIP}:{screenSharePort}");

        _serverAccessEndPoint = serverAccessEndPoint;
        _serverAudioEndPoint = serverAudioEndPoint;
        _serverScreenShareEndPoint = serverScreenShareEndPoint;
    }

    public async Task PingServerAccessEndPoint(byte[] audioPacket, byte[] screenSharePacket, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(_serverAccessEndPoint);

        if (_udpAudioClient.Client.Connected || _udpScreenShareClient.Client.Connected)
        {
            _udpAudioClient.Dispose();
            _udpAudioClient = new();

            _udpScreenShareClient.Dispose();
            _udpScreenShareClient = new();
        }

        await _udpAudioClient.SendAsync(audioPacket, _serverAccessEndPoint, token);
        await _udpScreenShareClient.SendAsync(screenSharePacket, _serverAccessEndPoint, token);
    }

    public void BindUdpClients()
    {
        ArgumentNullException.ThrowIfNull(_serverAudioEndPoint);
        ArgumentNullException.ThrowIfNull(_serverScreenShareEndPoint);

        _udpAudioClient.Connect(_serverAudioEndPoint);
        _udpScreenShareClient.Connect(_serverScreenShareEndPoint);
    }

    public async Task SendAudioPacketAsync(byte[] packet, CancellationToken token)
    {
        try
        {
            await _udpAudioClient.SendAsync(packet, token);
        }
        catch { /* ignore */ }
    }

    public async Task<byte[]> ReceiveAudioPacketAsync(CancellationToken token)
    {
        var result = await _udpAudioClient.ReceiveAsync(token);

        return result.Buffer;
    }

    public async Task SendScreenSharePacketAsync(byte[] packet, CancellationToken token)
    {
        try
        {
            await _udpScreenShareClient.SendAsync(packet, token);
        }
        catch { /* ignore */ }
    }

    public async Task<byte[]> ReceiveScreenSharePacketAsync(CancellationToken token)
    {
        var result = await _udpScreenShareClient.ReceiveAsync(token);

        return result.Buffer;
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
}