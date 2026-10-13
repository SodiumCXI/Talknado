namespace Talknado.Client.Models.Helpers.ScreenShare;

public readonly record struct ShareQualityProfile(int MaxPixels, int Fps);

public static class ShareQuality
{
    public const byte Levels = 3;

    // Глубина сжатия в ступень не входит: она подобрана под канал.
    // Ступень меняет только разрешение и частоту.
    private static readonly ShareQualityProfile[] _profiles =
    [
        new(1280 * 720, 30),
        new(1920 * 1080, 30),
        new(1920 * 1080, 60)
    ];

    // Программный кодировщик 1080p60 не тянет, и обещать этот уровень с ним нельзя.
    public static byte HardwareCeiling =>
        H264Encoder.HasHardwareEncoder ? (byte)2
        : Environment.ProcessorCount >= 8 ? (byte)1
        : (byte)0;

    public static ShareQualityProfile Resolve(byte hostCeiling)
    {
        var level = Math.Min(Math.Min(hostCeiling, HardwareCeiling), (byte)(Levels - 1));
        return _profiles[level];
    }
}
