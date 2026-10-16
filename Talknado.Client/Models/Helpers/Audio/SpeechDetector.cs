using System.Runtime.InteropServices;

namespace Talknado.Client.Models.Helpers.Audio;

/// <summary>
/// Детектор речи для индикатора «говорит»: вероятность речи по RNNoise, а не порог
/// громкости. Громкий шум, стук клавиш и музыка индикатор не зажигают, тихая речь — зажигает.
/// Состояние сети своё на каждого участника: она учитывает предыдущие кадры.
/// </summary>
public sealed class SpeechDetector : IDisposable
{
    private const string LibraryName = "rnnoise";
    private const int FrameSamples = 480;

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rnnoise_create(IntPtr model);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rnnoise_destroy(IntPtr state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe float rnnoise_process_frame(IntPtr state, float* output, float* input);

    private const float SpeechProbability = 0.5f;
    // Паузы между словами не гасят индикатор
    private const int HangoverFrames = 30;

    private readonly float[] _input = new float[FrameSamples];
    private readonly float[] _output = new float[FrameSamples];
    private IntPtr _state = rnnoise_create(IntPtr.Zero);
    private int _hangover;
    // Поток участника освобождается из другого потока, пока тик ещё может обрабатывать кадр.
    private readonly object _lock = new();

    /// <summary>Кадр 10 мс, 48 кГц, моно, 16 бит.</summary>
    public unsafe bool IsSpeech(ReadOnlySpan<byte> pcm)
    {
        lock (_lock)
        {
            if (_state == IntPtr.Zero)
                return false;

            var samples = MemoryMarshal.Cast<byte, short>(pcm);
            int count = Math.Min(samples.Length, FrameSamples);
            for (int i = 0; i < count; i++)
                _input[i] = samples[i];
            for (int i = count; i < FrameSamples; i++)
                _input[i] = 0;

            float probability;
            fixed (float* input = _input)
            fixed (float* output = _output)
                probability = rnnoise_process_frame(_state, output, input);

            if (probability >= SpeechProbability)
                _hangover = HangoverFrames;
            else if (_hangover > 0)
                _hangover--;

            return _hangover > 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_state == IntPtr.Zero)
                return;
            rnnoise_destroy(_state);
            _state = IntPtr.Zero;
        }
    }
}
