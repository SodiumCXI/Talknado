using Concentus.Enums;
using Concentus.Structs;

namespace Talknado.Client.Models.Helpers.Audio;

public static class OpusCodec
{
    private static readonly OpusEncoder _encoder;
    private static readonly OpusDecoder _decoder;
    private static readonly short[] _shortBuffer;
    private static readonly byte[] _opusBuffer;
    private const int FRAME_SIZE = 480;

    static OpusCodec()
    {
#pragma warning disable CS0618
        _encoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _decoder = new OpusDecoder(48000, 1);
#pragma warning restore CS0618
        _encoder.Bitrate = 24000;

        _shortBuffer = new short[FRAME_SIZE];
        _opusBuffer = new byte[FRAME_SIZE];
    }

    public static byte[] Encode(byte[] pcm)
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

    public static byte[] Decode(byte[] opus)
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

    public static void Dispose()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
    }
}