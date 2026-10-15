using System.Buffers.Binary;

namespace Talknado.Server.Core.Helpers;

/// <summary>
/// Открытый заголовок части зашифрованного кадра демонстрации: по нему сервер
/// выбирает канал и пропускает кадры, не расшифровывая их.
/// </summary>
public static class ScreenFrame
{
    public const int HeaderSize = 11;

    public const byte Key = 1;

    /// <summary>Прежние кадры могли не дойти: получатель их не ждёт.</summary>
    public const byte Resync = 2;

    public static bool TryRead(ReadOnlySpan<byte> packet, out uint number, out byte flags, out byte index)
    {
        number = 0;
        flags = 0;
        index = 0;
        if (packet.Length <= HeaderSize)
            return false;

        number = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        flags = packet[4];
        index = packet[5];
        return true;
    }

    public static void SetFlags(Span<byte> packet, byte flags) => packet[4] = flags;
}
