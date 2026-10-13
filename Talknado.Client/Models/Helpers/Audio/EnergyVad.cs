namespace Talknado.Client.Models.Helpers.Audio;

public class EnergyVad
{
    private static readonly double _threshold = 200.0;
    private static readonly int _hangoverFrames = 40;
    private int _hangoverCount;

    public bool IsSpeech(ReadOnlySpan<byte> pcm)
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

    private static double ComputeRms(ReadOnlySpan<byte> pcm)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        if (samples.Length == 0) return 0;

        double sum = 0;
        foreach (short sample in samples)
            sum += (double)sample * sample;

        return Math.Sqrt(sum / samples.Length);
    }
}
