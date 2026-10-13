using Microsoft.ML.OnnxRuntime;

namespace Talknado.Client.Models.Helpers.Audio;

/// <summary>
/// Потоковый DPDFNet 48 кГц, как StreamEnhancer пакета dpdfnet 0.6.0: окно Vorbis 960,
/// шаг 480. Кадр 480 отсчётов в шкале ±1; выход отстаёт на <see cref="DelaySamples"/>.
/// </summary>
public sealed class DpdfNet : IDisposable
{
    public const int FrameSamples = 480;
    public const int DelaySamples = 2400;

    private const int WindowLength = 960;
    private const int Bins = WindowLength / 2 + 1;

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();

    private readonly float[] _spec = new float[Bins * 2];
    private readonly float[] _specOut = new float[Bins * 2];
    private readonly float[] _stateA;
    private readonly float[] _stateB;
    private readonly float[] _initialState;
    private readonly OrtValue[] _values;
    private readonly OrtIoBinding _bindingA, _bindingB;
    private bool _stateInA = true;

    private readonly float[] _window = new float[WindowLength];
    private readonly float[] _in = new float[WindowLength];
    private readonly float[] _ola = new float[WindowLength];
    private readonly float[] _frame = new float[WindowLength];
    private int _filled;

    private readonly Bluestein _fft = new(WindowLength);

    public DpdfNet(string modelPath)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        _session = new InferenceSession(modelPath, options);

        // Начальное состояние лежит в метаданных модели: нормировки по полосам,
        // остальное нули. С нулевыми нормировками сеть первые секунды глохнет.
        var meta = _session.ModelMetadata.CustomMetadataMap;
        int stateSize = int.Parse(meta["state_size"]);
        int erbSize = int.Parse(meta["erb_norm_state_size"]);
        _initialState = new float[stateSize];
        var erb = meta["erb_norm_init"].Split(',');
        var spec = meta["spec_norm_init"].Split(',');
        for (int i = 0; i < erbSize; i++) _initialState[i] = float.Parse(erb[i], System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < spec.Length; i++) _initialState[erbSize + i] = float.Parse(spec[i], System.Globalization.CultureInfo.InvariantCulture);

        _stateA = (float[])_initialState.Clone();
        _stateB = new float[stateSize];

        long[] specShape = [1, 1, Bins, 2];
        long[] stateShape = [stateSize];
        var specIn = OrtValue.CreateTensorValueFromMemory(_spec, specShape);
        var specOut = OrtValue.CreateTensorValueFromMemory(_specOut, specShape);
        var stateA = OrtValue.CreateTensorValueFromMemory(_stateA, stateShape);
        var stateB = OrtValue.CreateTensorValueFromMemory(_stateB, stateShape);
        _values = [specIn, specOut, stateA, stateB];

        // Состояние переходит между двумя буферами по заранее созданным привязкам:
        // вызов сети не выделяет памяти.
        string specInName = _session.InputNames[0], stateInName = _session.InputNames[1];
        string specOutName = _session.OutputNames[0], stateOutName = _session.OutputNames[1];
        _bindingA = _session.CreateIoBinding();
        _bindingA.BindInput(specInName, specIn);
        _bindingA.BindInput(stateInName, stateA);
        _bindingA.BindOutput(specOutName, specOut);
        _bindingA.BindOutput(stateOutName, stateB);
        _bindingB = _session.CreateIoBinding();
        _bindingB.BindInput(specInName, specIn);
        _bindingB.BindInput(stateInName, stateB);
        _bindingB.BindOutput(specOutName, specOut);
        _bindingB.BindOutput(stateOutName, stateA);

        for (int i = 0; i < WindowLength; i++)
        {
            double s = Math.Sin(0.5 * Math.PI * (i + 0.5) / (WindowLength / 2.0));
            _window[i] = (float)Math.Sin(0.5 * Math.PI * s * s);
        }
        // Первый прогон сети втрое дольше обычного: ONNX Runtime доинициализирует граф.
        Span<float> silence = stackalloc float[FrameSamples];
        Span<float> scratch = stackalloc float[FrameSamples];
        Process(silence, scratch);
        Process(silence, scratch);
        Reset();
    }

    public void Reset()
    {
        Array.Copy(_initialState, _stateA, _initialState.Length);
        _stateInA = true;
        Array.Clear(_in);
        Array.Clear(_ola);
        _filled = 0;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        input.CopyTo(_in.AsSpan(_filled));
        _filled += FrameSamples;

        if (_filled < WindowLength)
        {
            output.Clear();
            return;
        }

        for (int i = 0; i < WindowLength; i++) _frame[i] = _in[i] * _window[i];
        _fft.ForwardReal(_frame, _spec);

        _session.RunWithBinding(_runOptions, _stateInA ? _bindingA : _bindingB);
        _stateInA = !_stateInA;

        _fft.InverseReal(_specOut, _frame);
        for (int i = 0; i < WindowLength; i++) _ola[i] += _frame[i] * _window[i];

        _ola.AsSpan(0, FrameSamples).CopyTo(output);
        Array.Copy(_ola, FrameSamples, _ola, 0, WindowLength - FrameSamples);
        Array.Clear(_ola, WindowLength - FrameSamples, FrameSamples);

        Array.Copy(_in, FrameSamples, _in, 0, WindowLength - FrameSamples);
        _filled -= FrameSamples;
    }

    public void Dispose()
    {
        _bindingA.Dispose();
        _bindingB.Dispose();
        foreach (var v in _values) v.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
    }

    /// <summary>
    /// ДПФ длины 960 через алгоритм Блюстейна поверх БПФ длины 2048: длина
    /// окна модели не степень двойки. Все буферы выделены заранее.
    /// </summary>
    private sealed class Bluestein
    {
        private const int M = 2048;
        private readonly int _n;
        private readonly double[] _wRe, _wIm;
        private readonly double[] _bRe = new double[M], _bIm = new double[M];
        private readonly double[] _aRe = new double[M], _aIm = new double[M];
        private readonly double[] _cos = new double[M / 2], _sin = new double[M / 2];
        private readonly int[] _rev = new int[M];

        public Bluestein(int n)
        {
            _n = n;
            _wRe = new double[n];
            _wIm = new double[n];
            for (int k = 0; k < n; k++)
            {
                double phase = Math.PI * ((long)k * k % (2L * n)) / n;
                _wRe[k] = Math.Cos(phase);
                _wIm[k] = Math.Sin(phase);
            }
            for (int i = 0; i < M / 2; i++)
            {
                _cos[i] = Math.Cos(2 * Math.PI * i / M);
                _sin[i] = -Math.Sin(2 * Math.PI * i / M);
            }
            for (int i = 0, bits = 11; i < M; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                _rev[i] = r;
            }

            _bRe[0] = _wRe[0]; _bIm[0] = _wIm[0];
            for (int k = 1; k < n; k++)
            {
                _bRe[k] = _bRe[M - k] = _wRe[k];
                _bIm[k] = _bIm[M - k] = _wIm[k];
            }
            Fft(_bRe, _bIm, false);
        }

        public void ForwardReal(float[] x, float[] spectrum)
        {
            Array.Clear(_aRe); Array.Clear(_aIm);
            for (int k = 0; k < _n; k++)
            {
                _aRe[k] = x[k] * _wRe[k];
                _aIm[k] = -x[k] * _wIm[k];
            }
            Convolve();
            for (int k = 0; k <= _n / 2; k++)
            {
                double re = _aRe[k] / M, im = _aIm[k] / M;
                spectrum[2 * k] = (float)(re * _wRe[k] + im * _wIm[k]);
                spectrum[2 * k + 1] = (float)(im * _wRe[k] - re * _wIm[k]);
            }
        }

        public void InverseReal(float[] spectrum, float[] x)
        {
            // Обратное через прямое: x = conj(DFT(conj(X))) / N, спектр дополняется симметрично.
            Array.Clear(_aRe); Array.Clear(_aIm);
            for (int k = 0; k < _n; k++)
            {
                int j = k <= _n / 2 ? k : _n - k;
                double re = spectrum[2 * j];
                double im = k <= _n / 2 ? -spectrum[2 * j + 1] : spectrum[2 * j + 1];
                if (k == 0 || k == _n / 2) im = 0;
                _aRe[k] = re * _wRe[k] + im * _wIm[k];
                _aIm[k] = im * _wRe[k] - re * _wIm[k];
            }
            Convolve();
            for (int k = 0; k < _n; k++)
            {
                double re = _aRe[k] / M, im = _aIm[k] / M;
                x[k] = (float)((re * _wRe[k] + im * _wIm[k]) / _n);
            }
        }

        private void Convolve()
        {
            Fft(_aRe, _aIm, false);
            for (int i = 0; i < M; i++)
            {
                double re = _aRe[i] * _bRe[i] - _aIm[i] * _bIm[i];
                double im = _aRe[i] * _bIm[i] + _aIm[i] * _bRe[i];
                _aRe[i] = re; _aIm[i] = im;
            }
            Fft(_aRe, _aIm, true);
        }

        private void Fft(double[] re, double[] im, bool inverse)
        {
            for (int i = 0; i < M; i++)
            {
                int j = _rev[i];
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= M; len <<= 1)
            {
                int half = len >> 1, step = M / len;
                for (int i = 0; i < M; i += len)
                {
                    for (int j = 0; j < half; j++)
                    {
                        double wr = _cos[j * step], wi = inverse ? -_sin[j * step] : _sin[j * step];
                        int a = i + j, c = a + half;
                        double xr = re[c] * wr - im[c] * wi, xi = re[c] * wi + im[c] * wr;
                        re[c] = re[a] - xr; im[c] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                    }
                }
            }
        }
    }
}
