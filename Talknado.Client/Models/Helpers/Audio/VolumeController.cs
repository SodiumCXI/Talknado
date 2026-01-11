namespace Talknado.Client.Models.Helpers.Audio;

public static class VolumeController
{
    public static void AdjustVolume(byte[] audioData, float volumeMultiplier)
    {
        if (volumeMultiplier == 0f)
        {
            Array.Clear(audioData, 0, audioData.Length);
            return;
        }

        short[] samples = new short[audioData.Length / 2];
        Buffer.BlockCopy(audioData, 0, samples, 0, audioData.Length);

        for (int i = 0; i < samples.Length; i++)
        {
            int adjustedSample = (int)(samples[i] * volumeMultiplier);

            samples[i] = (short)Math.Clamp(adjustedSample, short.MinValue, short.MaxValue);
        }

        Buffer.BlockCopy(samples, 0, audioData, 0, audioData.Length);
    }
}
