using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Talknado.Server.Core.Helpers;

public static class LocalIPsPacker
{
    public static List<IPAddress> GetAllLocalIPs()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => new
            {
                Interface = n,
                IPProps = n.GetIPProperties(),
                Priority = GetInterfacePriority(n)
            })
            .Where(x => x.IPProps.UnicastAddresses.Any(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.Interface.Speed)
            .SelectMany(x => x.IPProps.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address))
            .Distinct()
            .Take(4)
            .ToList();
    }

    private static int GetInterfacePriority(NetworkInterface networkInterface)
    {
        var hasGateway = networkInterface.GetIPProperties().GatewayAddresses
            .Any(g => !g.Address.ToString().StartsWith("0.0.0.0") &&
                      g.Address.AddressFamily == AddressFamily.InterNetwork);
        return networkInterface.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet when hasGateway => 5,
            NetworkInterfaceType.Wireless80211 when hasGateway => 4,
            NetworkInterfaceType.Ethernet => 3,
            NetworkInterfaceType.Wireless80211 => 2,
            _ => 1
        };
    }

    public static void Pack(BitWriter w)
    {
        var ips = GetAllLocalIPs();
        if (ips.Count == 0)
            throw new IOException("No network interfaces found");

        w.Write(ips.Count - 1, 2);
        foreach (var ip in ips)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 192 && b[1] == 168)
            {
                w.Write(0b00, 2);
                if (b[2] == 1) { w.Write(0, 1); w.Write(b[3], 8); }
                else { w.Write(1, 1); w.Write(b[2], 8); w.Write(b[3], 8); }
            }
            else if (b[0] == 172 && b[1] is >= 16 and <= 31)
            {
                w.Write(0b01, 2);
                w.Write(b[1] - 16, 4);
                w.Write(b[2], 8);
                w.Write(b[3], 8);
            }
            else if (b[0] == 10)
            {
                w.Write(0b10, 2);
                w.Write(b[1], 8);
                w.Write(b[2], 8);
                w.Write(b[3], 8);
            }
            else
            {
                w.Write(0b11, 2);
                w.Write(b[0], 8);
                w.Write(b[1], 8);
                w.Write(b[2], 8);
                w.Write(b[3], 8);
            }
        }
    }

    public sealed class BitWriter
    {
        private readonly List<byte> _buf = [];
        private int _cur;
        private int _bits;
        public void Write(int value, int bitCount)
        {
            for (int i = bitCount - 1; i >= 0; i--)
            {
                _cur = (_cur << 1) | ((value >> i) & 1);
                if (++_bits == 8) { _buf.Add((byte)_cur); _cur = 0; _bits = 0; }
            }
        }
        public byte[] ToArray()
        {
            if (_bits > 0) _buf.Add((byte)(_cur << (8 - _bits)));
            return [.. _buf];
        }
    }
}