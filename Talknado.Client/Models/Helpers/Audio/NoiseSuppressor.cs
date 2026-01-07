using RNNoise.NET;

namespace Talknado.Client.Models.Helpers.Audio;

public static class NoiseSuppressor
{
    private static readonly Denoiser _denoiser = new();

    public static byte[] Denoise(byte[] microphoneBytes)
    {
        var floatInput = BytesToFloats(microphoneBytes, microphoneBytes.Length);
        _denoiser.Denoise(floatInput);
        var processedBytes = FloatsToBytes(floatInput);

        return processedBytes;
    }

    private static float[] BytesToFloats(byte[] buffer, int length)
    {
        int samples = length / 2;
        var floats = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            short sample = BitConverter.ToInt16(buffer, i * 2);
            floats[i] = sample / 32768f;
        }
        return floats;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var buffer = new byte[floats.Length * 2];
        for (int i = 0; i < floats.Length; i++)
        {
            short sample = (short)Math.Clamp(floats[i] * 32767f, short.MinValue, short.MaxValue);
            BitConverter.GetBytes(sample).CopyTo(buffer, i * 2);
        }
        return buffer;
    }

    public static void Dispose()
    {
        _denoiser?.Dispose();
    }
}