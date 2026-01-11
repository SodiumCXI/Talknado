using Concentus.Enums;
using Concentus.Structs;

namespace Talknado.Client.Models.Helpers.Audio;

public class OpusCodecEncoder : IDisposable
{
    private readonly OpusEncoder _encoder;
    private readonly short[] _shortBuffer;
    private readonly byte[] _opusBuffer;
    private const int FRAME_SIZE = 480;

    public OpusCodecEncoder(bool isSystemAudio)
    {
#pragma warning disable CS0618
        _encoder = isSystemAudio
            ? new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO)
            : new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = isSystemAudio ? 48000 : 24000;
#pragma warning restore CS0618

        _shortBuffer = new short[FRAME_SIZE];
        _opusBuffer = new byte[1275];
    }

    public byte[] Encode(byte[] pcm)
    {
        lock (_encoder)
        {
            Buffer.BlockCopy(pcm, 0, _shortBuffer, 0, pcm.Length);
#pragma warning disable CS0618
            int length = _encoder.Encode(_shortBuffer, 0, FRAME_SIZE, _opusBuffer, 0, _opusBuffer.Length);
#pragma warning restore CS0618

            byte[] result = new byte[length];
            Array.Copy(_opusBuffer, result, length);
            return result;
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

    public byte[] Decode(byte[] opus)
    {
        lock (_decoder)
        {
#pragma warning disable CS0618
            _decoder.Decode(opus, 0, opus.Length, _shortBuffer, 0, FRAME_SIZE, false);
#pragma warning restore CS0618
            byte[] pcm = new byte[FRAME_SIZE * 2];
            Buffer.BlockCopy(_shortBuffer, 0, pcm, 0, pcm.Length);
            return pcm;
        }
    }

    public byte[] DecodePLC()
    {
        lock (_decoder)
        {
#pragma warning disable CS0618
            _decoder.Decode(null, 0, 0, _shortBuffer, 0, FRAME_SIZE, false);
#pragma warning restore CS0618
            byte[] pcm = new byte[FRAME_SIZE * 2];
            Buffer.BlockCopy(_shortBuffer, 0, pcm, 0, pcm.Length);
            return pcm;
        }
    }

    public void Dispose()
    {
        _decoder?.Dispose();

        GC.SuppressFinalize(this);
    }
}