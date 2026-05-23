namespace Talknado.Client.Models.Helpers.Audio;

public static class EnergyVad
{
    private static readonly double _threshold = 200.0;
    private static readonly int _hangoverFrames = 40;
    private static int _hangoverCount;

    public static bool IsSpeech(byte[] pcm)
    {
        double rms = ComputeRms(pcm);

        if (rms > _threshold)
        {
            _hangoverCount = _hangoverFrames;
            return true;
        }

        if (_hangoverCount > 0)
        {
            _hangoverCount--;
            return true;
        }

        return false;
    }

    public static double ComputeRms(byte[] pcm)
    {
        int sampleCount = pcm.Length / 2;
        if (sampleCount == 0) return 0;

        double sum = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(pcm, i * 2);
            sum += (double)sample * sample;
        }
        return Math.Sqrt(sum / sampleCount);
    }
}
