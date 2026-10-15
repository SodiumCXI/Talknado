using System.Buffers.Binary;

namespace Talknado.Client.Models.Helpers.Network;

/// <summary>
/// Открытый заголовок кадра демонстрации перед зашифрованным кадром: номер (4 байта)
/// и признаки (1 байт). Открыт, чтобы сервер мог выбрать канал и пропускать кадры
/// до опорного, не расшифровывая их; содержимое кадра остаётся зашифрованным.
/// </summary>
public static class ScreenFrame
{
    public const int HeaderSize = 5;

    /// <summary>Опорный кадр: с него можно начать показ.</summary>
    public const byte Key = 1;

    /// <summary>Кадры до этого могли не дойти: получатель не ждёт их и начинает отсюда.</summary>
    public const byte Resync = 2;

    public static bool TryRead(ReadOnlySpan<byte> packet, out uint number, out byte flags)
    {
        number = 0;
        flags = 0;
        if (packet.Length <= HeaderSize)
            return false;

        number = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        flags = packet[4];
        return true;
    }

    public static void Write(Span<byte> packet, uint number, byte flags)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, number);
        packet[4] = flags;
    }
}
