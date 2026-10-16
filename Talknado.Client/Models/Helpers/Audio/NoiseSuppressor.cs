using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Talknado.Client.Models.Helpers.Audio;

public enum NoiseSuppressionModel { None, RNNoise, DPDFNet2, DPDFNet8 }

public static class NoiseSuppressor
{
    private const string LibraryName = "rnnoise";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rnnoise_create(IntPtr model);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rnnoise_destroy(IntPtr state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rnnoise_get_frame_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe float rnnoise_process_frame(IntPtr state, float* output, float* input);

    private static readonly object _lock = new();

    private static IntPtr _state;
    private static int _frameLength;
    private static float[] _input = [];
    private static float[] _output = [];

    private static byte[] _result = [];

    private sealed record LoadedDpdfNet(NoiseSuppressionModel Model, DpdfNet Net);

    private static volatile NoiseSuppressionModel _model = NoiseSuppressionModel.RNNoise;
    private static volatile LoadedDpdfNet? _dpdfNet;
    private static int _dpdfNetLoadGeneration;
    private static DpdfNet? _activeDpdfNet;

    public static NoiseSuppressionModel Model
    {
        get => _model;
        set
        {
            _model = value;

            // Поколение отменяет загрузку, начатую до этого выбора: иначе модель,
            // догрузившаяся позже, подменила бы выбранную.
            int generation = Interlocked.Increment(ref _dpdfNetLoadGeneration);

            if (value is NoiseSuppressionModel.DPDFNet2 or NoiseSuppressionModel.DPDFNet8)
            {
                if (_dpdfNet?.Model != value)
                    Task.Run(() => LoadDpdfNet(value, generation));
            }
            else if (_dpdfNet != null)
            {
                Task.Run(() => SwapDpdfNet(null, generation));
            }
        }
    }

    // Загрузка модели занимает около трети секунды, в потоке захвата это пропуск
    // звука. До её конца работает RNNoise.
    private static void LoadDpdfNet(NoiseSuppressionModel model, int generation)
    {
        try
        {
            string file = model == NoiseSuppressionModel.DPDFNet8 ? "dpdfnet8_48khz_hr.onnx" : "dpdfnet2_48khz_hr.onnx";
            SwapDpdfNet(new LoadedDpdfNet(model, new DpdfNet(Path.Combine(AppContext.BaseDirectory, file))), generation);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DPDFNet unavailable: {ex.Message}");
        }
    }

    private static void SwapDpdfNet(LoadedDpdfNet? next, int generation)
    {
        DpdfNet? stale;

        // Под той же блокировкой, что и обработка кадра: модель нельзя
        // освободить, пока поток захвата прогоняет через неё кадр.
        lock (_lock)
        {
            if (generation != Volatile.Read(ref _dpdfNetLoadGeneration))
            {
                stale = next?.Net;
            }
            else
            {
                stale = _dpdfNet?.Net;
                _dpdfNet = next;
                _activeDpdfNet = null;
            }
        }

        stale?.Dispose();
    }

    /// <summary>На сколько кадров выход отстаёт от входа (RNNoise — по замеру, 960 отсчётов).</summary>
    public static int DelayFrames
    {
        get
        {
            var model = _model;
            if (model == NoiseSuppressionModel.None)
                return 0;
            var loaded = _dpdfNet;
            return loaded != null && loaded.Model == model ? DpdfNet.DelaySamples / DpdfNet.FrameSamples : 2;
        }
    }

    /// <summary>Новое включение микрофона: звук прошлого включения не должен выйти в начале.</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            if (_state != IntPtr.Zero)
                rnnoise_destroy(_state);
            _state = IntPtr.Zero;
            EnsureCreated();
            _activeDpdfNet = null;
        }
    }

    /// <summary>Возвращает буфер, переиспользуемый между вызовами.</summary>
    public static unsafe byte[] Denoise(byte[] microphoneFrame)
    {
        var model = _model;
        if (model == NoiseSuppressionModel.None)
            return microphoneFrame;

        lock (_lock)
        {
            EnsureCreated();

            int sampleCount = Math.Min(microphoneFrame.Length / 2, _frameLength);

            for (int i = 0; i < sampleCount; i++)
                _input[i] = BinaryPrimitives.ReadInt16LittleEndian(microphoneFrame.AsSpan(i * 2));

            for (int i = sampleCount; i < _frameLength; i++)
                _input[i] = 0f;

            var loaded = _dpdfNet;
            var dpdfNet = loaded != null && loaded.Model == model ? loaded.Net : null;
            if (dpdfNet != null)
            {
                if (_activeDpdfNet != dpdfNet)
                {
                    dpdfNet.Reset();
                    _activeDpdfNet = dpdfNet;
                }

                // DPDFNet работает в шкале ±1, RNNoise — в шкале int16.
                for (int i = 0; i < _frameLength; i++)
                    _input[i] *= 1f / 32768f;

                dpdfNet.Process(_input.AsSpan(0, _frameLength), _output.AsSpan(0, _frameLength));

                for (int i = 0; i < _frameLength; i++)
                    _output[i] *= 32768f;
            }
            else
            {
                _activeDpdfNet = null;

                fixed (float* inPtr = _input)
                fixed (float* outPtr = _output)
                    rnnoise_process_frame(_state, outPtr, inPtr);
            }

            if (_result.Length != microphoneFrame.Length)
                _result = new byte[microphoneFrame.Length];

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = (short)Math.Clamp(_output[i], short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(_result.AsSpan(i * 2), sample);
            }

            return _result;
        }
    }

    private static void EnsureCreated()
    {
        if (_state != IntPtr.Zero)
            return;

        _state = rnnoise_create(IntPtr.Zero);
        _frameLength = rnnoise_get_frame_size();
        _input = new float[_frameLength];
        _output = new float[_frameLength];
    }
}
