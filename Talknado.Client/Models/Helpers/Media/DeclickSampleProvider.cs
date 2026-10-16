using NAudio.Wave;

namespace Talknado.Client.Models.Helpers.Media;

/// <summary>
/// Убирает щелчки на разрывах звука. При перемотке и паузе звук не обрывается, а затухает
/// за 10 мс; после перемотки, паузы и опустошения буфера нарастает за 10 мс.
/// Устройство на паузе останавливается, когда затухание доиграно: остановка раньше
/// оборвала бы уже отданный ему звук.
/// Громкость меняется плавно, по сэмплу: скачок громкости на границе блока слышен треском.
/// </summary>
public sealed class DeclickSampleProvider(BufferedWaveProvider buffer) : ISampleProvider
{
    private const int FadeFrames = 480;
    private const int Channels = 2;
    // Доля разницы до нужной громкости за сэмпл: около 5 мс на переход
    private const float VolumeStep = 0.004f;

    private readonly BufferedWaveProvider _buffer = buffer;
    private readonly ISampleProvider _source = buffer.ToSampleProvider();
    private readonly float[] _tail = new float[FadeFrames * Channels];
    private readonly object _lock = new();
    private int _tailLength;
    private int _tailPosition;
    private int _fadeIn = FadeFrames;
    private volatile float _targetVolume = 1;
    private float _volume = 1;
    private bool _holding;
    private long _writtenFrames;
    private long _silentFrom = -1;
    private long _silentFrames;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Устройство, которое останавливается после затухания на паузе.</summary>
    public WasapiOut? Output { get; set; }

    public float Volume
    {
        get => _targetVolume;
        set => _targetVolume = value;
    }

    /// <summary>Тишина, отданная устройству на паузе: оно сыграет её после продолжения, а часы видео стоят.</summary>
    public long SilentFrames
    {
        get { lock (_lock) return _silentFrames; }
    }

    /// <summary>Перемотка: ещё не сыгранный звук затухает, буфер очищается.</summary>
    public void Cut()
    {
        lock (_lock)
        {
            if (!_holding)
                TakeTail();
            _buffer.ClearBuffer();
            _fadeIn = 0;
        }
    }

    /// <summary>Пауза: звук затухает, затем устройство останавливается.</summary>
    public void Hold()
    {
        lock (_lock)
        {
            if (_holding)
                return;
            TakeTail();
            _holding = true;
            _silentFrom = -1;
        }
    }

    /// <summary>Вызывается до запуска устройства, иначе оно может остановиться уже после него.</summary>
    public void Release()
    {
        lock (_lock)
        {
            if (!_holding)
                return;
            _holding = false;
            _fadeIn = 0;
        }
    }

    private void TakeTail()
    {
        if (_tailPosition < _tailLength)
            return;

        int length = Math.Min(_tail.Length, _buffer.BufferedBytes / sizeof(float) / Channels * Channels);
        length = length > 0 ? _source.Read(_tail, 0, length) : 0;
        float gain = (float)_fadeIn / FadeFrames;
        int frames = length / Channels;
        for (int i = 0; i < length; i++)
            _tail[i] *= gain * (frames - i / Channels) / frames;

        _tailLength = length;
        _tailPosition = 0;
    }

    // buffer от NAudio — byte[], выданный за float[]: Array.Copy и Array.Clear на нём
    // падают или чистят не то, поэтому запись только поэлементная.
    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int written = 0;
            while (written < count && _tailPosition < _tailLength)
                buffer[offset + written++] = _tail[_tailPosition++];

            if (!_holding)
            {
                int wanted = (count - written) / Channels * Channels;
                int available = Math.Min(wanted, _buffer.BufferedBytes / sizeof(float) / Channels * Channels);
                // Пустое чтение NAudio не переносит, пока не прочитал ни разу
                int read = available > 0 ? _source.Read(buffer, offset + written, available) : 0;

                for (int i = 0; i < read && _fadeIn < FadeFrames; i += Channels)
                {
                    float gain = (float)_fadeIn++ / FadeFrames;
                    for (int c = 0; c < Channels; c++)
                        buffer[offset + written + i + c] *= gain;
                }

                written += read;
                if (read < wanted)
                    _fadeIn = 0;
            }
            else
            {
                if (_silentFrom < 0 && _tailPosition >= _tailLength)
                    _silentFrom = _writtenFrames + written / Channels;
                _silentFrames += (count - written) / Channels;
            }

            while (written < count)
                buffer[offset + written++] = 0;

            float target = _targetVolume;
            for (int i = 0; i < count; i += Channels)
            {
                _volume = MathF.Abs(target - _volume) < 1e-4f ? target : _volume + (target - _volume) * VolumeStep;
                for (int c = 0; c < Channels; c++)
                    buffer[offset + i + c] *= _volume;
            }

            _writtenFrames += count / Channels;

            if (_holding && _silentFrom >= 0 && Output is { } output
                && output.GetPosition() * WaveFormat.SampleRate / output.OutputWaveFormat.AverageBytesPerSecond >= _silentFrom)
                output.Pause();

            return count;
        }
    }
}
