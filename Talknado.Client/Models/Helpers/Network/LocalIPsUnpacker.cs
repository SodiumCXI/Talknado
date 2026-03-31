using System.Net;

namespace Talknado.Client.Models.Helpers.Network;

public class LocalIPsUnpacker
{
    public static List<string> Unpack(BitReader r)
    {
        int count = r.Read(2) + 1;
        var result = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int type = r.Read(2);
            byte[] ip = type switch
            {
                0b00 => r.Read(1) == 0
                    ? [192, 168, 1, (byte)r.Read(8)]
                    : [192, 168, (byte)r.Read(8), (byte)r.Read(8)],
                0b01 => [172, (byte)(r.Read(4) + 16), (byte)r.Read(8), (byte)r.Read(8)],
                0b10 => [10, (byte)r.Read(8), (byte)r.Read(8), (byte)r.Read(8)],
                _ => [(byte)r.Read(8), (byte)r.Read(8), (byte)r.Read(8), (byte)r.Read(8)],
            };
            result.Add(new IPAddress(ip).ToString());
        }
        return result;
    }

    public sealed class BitReader(byte[] data)
    {
        private int _pos;
        public int Read(int bitCount)
        {
            int result = 0;
            for (int i = 0; i < bitCount; i++)
            {
                int byteIdx = _pos / 8;
                int bitIdx = 7 - (_pos % 8);
                result = (result << 1) | ((data[byteIdx] >> bitIdx) & 1);
                _pos++;
            }
            return result;
        }
    }
}
