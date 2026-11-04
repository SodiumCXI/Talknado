using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Talknado.Client.Models.Helpers.Audio;

/// <summary>
/// Приоритет звука от Windows (MMCSS, «Pro Audio»). С обычным приоритетом поток
/// уступает игре или браузеру, и буфер устройства в 20 мс успевает опустеть.
/// Потоки NAudio создаются внутри библиотеки, поэтому зовётся из их обратных вызовов.
/// </summary>
internal static partial class ProAudioThread
{
    [ThreadStatic]
    private static bool _entered;

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    public static void Enter()
    {
        if (_entered)
            return;
        _entered = true;

        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        uint taskIndex = 0;
        AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
    }
}

/// <summary>Поднимает приоритет потока вывода NAudio при первом обращении за звуком.</summary>
internal sealed class ProAudioWaveProvider(IWaveProvider source) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        ProAudioThread.Enter();
        return source.Read(buffer, offset, count);
    }
}
