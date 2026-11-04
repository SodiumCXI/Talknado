using System.Runtime.InteropServices;

namespace Talknado.Client.Models.Helpers.Audio;

/// <summary>
/// libopus 1.5.2 (lib/opus.dll) собран с ENABLE_DEEP_PLC: на провалах в 60–200 мс
/// нейросетевой PLC заметно ближе к настоящей речи, чем обычный.
/// </summary>
internal static partial class OpusNative
{
    private const string LibraryName = "opus";

    public const int ApplicationAudio = 2049;
    public const int SetBitrateRequest = 4002;
    public const int SetComplexityRequest = 4010;
    public const int ResetState = 4028;

    [LibraryImport(LibraryName, EntryPoint = "opus_encoder_create")]
    public static partial nint EncoderCreate(int sampleRate, int channels, int application, out int error);

    [LibraryImport(LibraryName, EntryPoint = "opus_encode")]
    public static unsafe partial int Encode(nint encoder, short* pcm, int frameSize, byte* data, int maxDataBytes);

    // Функции ctl variadic; на x64 Windows целые аргументы передаются так же, как у обычных.
    [LibraryImport(LibraryName, EntryPoint = "opus_encoder_ctl")]
    public static partial int EncoderCtl(nint encoder, int request, int value);

    [LibraryImport(LibraryName, EntryPoint = "opus_encoder_ctl")]
    public static partial int EncoderCtl(nint encoder, int request);

    [LibraryImport(LibraryName, EntryPoint = "opus_encoder_destroy")]
    public static partial void EncoderDestroy(nint encoder);

    [LibraryImport(LibraryName, EntryPoint = "opus_decoder_create")]
    public static partial nint DecoderCreate(int sampleRate, int channels, out int error);

    [LibraryImport(LibraryName, EntryPoint = "opus_decode")]
    public static unsafe partial int Decode(nint decoder, byte* data, int length, short* pcm, int frameSize, int decodeFec);

    [LibraryImport(LibraryName, EntryPoint = "opus_decoder_ctl")]
    public static partial int DecoderCtl(nint decoder, int request, int value);

    [LibraryImport(LibraryName, EntryPoint = "opus_decoder_destroy")]
    public static partial void DecoderDestroy(nint decoder);

    public static nint Check(nint handle, int error, string what) =>
        handle != 0 && error == 0 ? handle : throw new InvalidOperationException($"{what}: Opus error {error}");
}

public class OpusCodecEncoder : IDisposable
{
    /// <summary>Наибольший пакет Opus.</summary>
    public const int MaxPacketSize = 1275;

    private const int FRAME_SIZE = 480;
    private const int Complexity = 9;

    private readonly object _lock = new();
    private readonly short[] _shortBuffer = new short[FRAME_SIZE];
    private nint _encoder;

    public OpusCodecEncoder(bool isSystemAudio)
    {
        // Микрофон тоже в AUDIO: режим VOIP кодирует шипящие как шум.
        _encoder = OpusNative.Check(OpusNative.EncoderCreate(48000, 1, OpusNative.ApplicationAudio, out int error), error, "opus_encoder_create");
        OpusNative.EncoderCtl(_encoder, OpusNative.SetBitrateRequest, isSystemAudio ? 48000 : 32000);
        OpusNative.EncoderCtl(_encoder, OpusNative.SetComplexityRequest, Complexity);
    }

    /// <summary>Кадр int16 в пакет Opus; возвращает длину пакета.</summary>
    public unsafe int Encode(ReadOnlySpan<byte> pcm, Span<byte> destination)
    {
        lock (_lock)
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcm);
            if (samples.Length < FRAME_SIZE)
            {
                samples.CopyTo(_shortBuffer);
                _shortBuffer.AsSpan(samples.Length).Clear();
                samples = _shortBuffer;
            }

            fixed (short* input = samples)
            fixed (byte* output = destination)
            {
                int length = OpusNative.Encode(_encoder, input, FRAME_SIZE, output, destination.Length);
                return length < 0 ? throw new InvalidOperationException($"opus_encode: Opus error {length}") : length;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
            OpusNative.EncoderCtl(_encoder, OpusNative.ResetState);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_encoder != 0)
                OpusNative.EncoderDestroy(_encoder);
            _encoder = 0;
        }

        GC.SuppressFinalize(this);
    }
}

public class OpusCodecDecoder : IDisposable
{
    /// <summary>Байт PCM в одном кадре: 480 отсчётов int16.</summary>
    public const int FrameBytes = FRAME_SIZE * 2;

    private const int FRAME_SIZE = 480;
    // С 5 декодер включает нейросетевой PLC.
    private const int DeepPlcComplexity = 5;

    private readonly object _lock = new();
    private nint _decoder;

    public OpusCodecDecoder()
    {
        _decoder = OpusNative.Check(OpusNative.DecoderCreate(48000, 1, out int error), error, "opus_decoder_create");
        OpusNative.DecoderCtl(_decoder, OpusNative.SetComplexityRequest, DeepPlcComplexity);
    }

    /// <summary>Повреждённый пакет заменяется кадром PLC, а не оставляет в буфере прошлый кадр.</summary>
    public unsafe void Decode(ReadOnlySpan<byte> opus, Span<byte> pcm)
    {
        lock (_lock)
        {
            fixed (byte* input = opus)
            fixed (byte* output = pcm)
            {
                if (OpusNative.Decode(_decoder, input, opus.Length, (short*)output, FRAME_SIZE, 0) < 0)
                    OpusNative.Decode(_decoder, null, 0, (short*)output, FRAME_SIZE, 0);
            }
        }
    }

    /// <summary>Кадр на месте потерянного пакета.</summary>
    public unsafe void DecodePLC(Span<byte> pcm)
    {
        lock (_lock)
        {
            fixed (byte* output = pcm)
                OpusNative.Decode(_decoder, null, 0, (short*)output, FRAME_SIZE, 0);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_decoder != 0)
                OpusNative.DecoderDestroy(_decoder);
            _decoder = 0;
        }

        GC.SuppressFinalize(this);
    }
}
