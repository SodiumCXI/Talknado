namespace Talknado.Client.Models.Helpers.Audio;

public static class VolumeController
{
    public static void AdjustVolume(Span<byte> audioData, float volumeMultiplier)
    {
        if (volumeMultiplier == 0f)
        {
            audioData.Clear();
            return;
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audioData);

        for (int i = 0; i < samples.Length; i++)
        {
            int adjustedSample = (int)(samples[i] * volumeMultiplier);

            samples[i] = (short)Math.Clamp(adjustedSample, short.MinValue, short.MaxValue);
        }
    }
}
