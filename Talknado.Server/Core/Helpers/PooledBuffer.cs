using System.Buffers;

namespace Talknado.Server.Core.Helpers;

/// <summary>
/// Массив из общего пула и длина полезных данных в нём: массив пула бывает
/// длиннее запрошенного. Владелец возвращает его ровно один раз.
/// </summary>
public readonly record struct PooledBuffer(byte[] Array, int Length)
{
    public static PooledBuffer Rent(int length) => new(ArrayPool<byte>.Shared.Rent(length), length);

    public static PooledBuffer CopyOf(ReadOnlySpan<byte> data)
    {
        var buffer = Rent(data.Length);
        data.CopyTo(buffer.Array);
        return buffer;
    }

    public Span<byte> Span => Array.AsSpan(0, Length);
    public Memory<byte> Memory => Array.AsMemory(0, Length);

    public PooledBuffer WithLength(int length) => new(Array, length);

    public void Return()
    {
        if (Array != null)
            ArrayPool<byte>.Shared.Return(Array);
    }
}
