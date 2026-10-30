namespace Talknado.Client.Models.Helpers.Audio;

public static class VolumeController
{
    /// <summary>
    /// Множитель идёт от from к to по отсчётам кадра: скачок громкости на границе
    /// кадра, сто раз в секунду, пока двигают ползунок, слышен треском.
    /// </summary>
    public static void AdjustVolume(Span<byte> audioData, float from, float to)
    {
        if (from == 0f && to == 0f)
        {
            audioData.Clear();
            return;
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audioData);
        float step = (to - from) / samples.Length;

        for (int i = 0; i < samples.Length; i++)
        {
            int adjustedSample = (int)(samples[i] * (from + step * (i + 1)));

            samples[i] = (short)Math.Clamp(adjustedSample, short.MinValue, short.MaxValue);
        }
    }
}
