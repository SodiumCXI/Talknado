using Concentus.Enums;
using Concentus.Structs;
using System.Runtime.InteropServices;

namespace Talknado.Client.Models.Helpers.Audio;

public class OpusCodecEncoder : IDisposable
{
    /// <summary>Наибольший пакет Opus.</summary>
    public const int MaxPacketSize = 1275;

    private readonly OpusEncoder _encoder;
    private readonly short[] _shortBuffer;
    private const int FRAME_SIZE = 480;

    public OpusCodecEncoder(bool isSystemAudio)
    {
        // Микрофон тоже в AUDIO: режим VOIP кодирует шипящие как шум.
#pragma warning disable CS0618
        _encoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = isSystemAudio ? 48000 : 32000;
#pragma warning restore CS0618

        _shortBuffer = new short[FRAME_SIZE];
    }

    /// <summary>Кадр int16 в пакет Opus; возвращает длину пакета.</summary>
    public int Encode(ReadOnlySpan<byte> pcm, Span<byte> destination)
    {
        lock (_encoder)
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcm);
            if (samples.Length < FRAME_SIZE)
            {
                samples.CopyTo(_shortBuffer);
                _shortBuffer.AsSpan(samples.Length).Clear();
                samples = _shortBuffer;
            }

            return _encoder.Encode(samples[..FRAME_SIZE], FRAME_SIZE, destination, destination.Length);
        }
    }

    public void Dispose()
    {
        _encoder?.Dispose();

        GC.SuppressFinalize(this);
    }
}

public class OpusCodecDecoder : IDisposable
{
    /// <summary>Байт PCM в одном кадре: 480 отсчётов int16.</summary>
    public const int FrameBytes = FRAME_SIZE * 2;

    private readonly OpusDecoder _decoder;
    private readonly short[] _shortBuffer;
    private const int FRAME_SIZE = 480;

    public OpusCodecDecoder()
    {
#pragma warning disable CS0618
        _decoder = new(48000, 1);
#pragma warning restore CS0618
        _shortBuffer = new short[FRAME_SIZE];
    }

    public void Decode(ReadOnlySpan<byte> opus, Span<byte> pcm)
    {
        lock (_decoder)
        {
            _decoder.Decode(opus, MemoryMarshal.Cast<byte, short>(pcm), FRAME_SIZE, false);
        }
    }

    /// <summary>Кадр на месте потерянного пакета.</summary>
    public void DecodePLC(Span<byte> pcm)
    {
        lock (_decoder)
        {
#pragma warning disable CS0618
            _decoder.Decode(null, 0, 0, _shortBuffer, 0, FRAME_SIZE, false);
#pragma warning restore CS0618
            MemoryMarshal.AsBytes(_shortBuffer.AsSpan()).CopyTo(pcm);
        }
    }

    public void Dispose()
    {
        _decoder?.Dispose();

        GC.SuppressFinalize(this);
    }
}
