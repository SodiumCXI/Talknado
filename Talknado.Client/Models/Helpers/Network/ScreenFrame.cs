using System.Buffers.Binary;

namespace Talknado.Client.Models.Helpers.Network;

/// <summary>
/// Открытый заголовок части зашифрованного кадра демонстрации: по нему сервер
/// выбирает канал и пропускает кадры, не расшифровывая их. Части идут по разным
/// каналам, чтобы крупный кадр не упирался в окно одного.
/// </summary>
public static class ScreenFrame
{
    public const int HeaderSize = 11;
    public const int MaxPieces = 16;
    private const int PieceTarget = 16 * 1024;

    public const byte Key = 1;

    /// <summary>Прежние кадры могли не дойти: получатель их не ждёт.</summary>
    public const byte Resync = 2;

    public readonly record struct Header(uint Number, byte Flags, byte Index, byte Count, int Total)
    {
        public int PieceSize => (Total + Count - 1) / Count;
        public int Offset => Index * PieceSize;
        public int Length => Math.Min(PieceSize, Total - Offset);
    }

    public static byte PieceCount(int total) => (byte)Math.Clamp((total + PieceTarget - 1) / PieceTarget, 1, MaxPieces);

    public static bool TryRead(ReadOnlySpan<byte> packet, out Header header)
    {
        header = default;
        if (packet.Length <= HeaderSize)
            return false;

        header = new Header(
            BinaryPrimitives.ReadUInt32LittleEndian(packet),
            packet[4],
            packet[5],
            packet[6],
            BinaryPrimitives.ReadInt32LittleEndian(packet[7..]));

        return header.Count is > 0 and <= MaxPieces && header.Index < header.Count &&
            header.Total > 0 && header.Offset < header.Total && packet.Length - HeaderSize == header.Length;
    }

    public static void Write(Span<byte> packet, Header header)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, header.Number);
        packet[4] = header.Flags;
        packet[5] = header.Index;
        packet[6] = header.Count;
        BinaryPrimitives.WriteInt32LittleEndian(packet[7..], header.Total);
    }
}
