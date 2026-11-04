using System.Diagnostics;

namespace Talknado.Client.Models.Helpers.Audio;

/// <summary>
/// Разброс задержки пакетов за последнюю минуту. Задержка пакета — время прихода
/// минус его место в потоке (номер × 10 мс); часы отправителя не нужны, важна
/// только разница между самым ранним и самым поздним пакетом.
/// </summary>
internal sealed class DelaySpread
{
    private const int FrameMilliseconds = 10;
    private const int BucketMilliseconds = 2000;
    private const int BucketCount = 30;
    // Дольше — молчал отправитель (рация, выключенный микрофон), а не сеть: номера
    // после паузы идут подряд, и без поправки задержка выросла бы на всю паузу.
    private const long PauseMilliseconds = 1000;

    private readonly long[] _bucketIds = new long[BucketCount];
    private readonly long[] _min = new long[BucketCount];
    private readonly long[] _max = new long[BucketCount];
    private long _lastArrival = long.MinValue;
    private ushort _lastSequence;
    private long _position;

    public DelaySpread() => Array.Fill(_bucketIds, -1);

    public static long NowMilliseconds() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    public void Add(ushort sequence, long now)
    {
        long delay;
        if (_lastArrival == long.MinValue || now - _lastArrival > PauseMilliseconds)
        {
            // После паузы пакет считается пришедшим вовремя: память о замираниях остаётся.
            delay = MinDelay(now) ?? now;
            _position = now - delay;
            _lastSequence = sequence;
        }
        else
        {
            int ahead = (short)(sequence - _lastSequence);
            long position = _position + (long)ahead * FrameMilliseconds;
            delay = now - position;
            if (ahead > 0)
            {
                _position = position;
                _lastSequence = sequence;
            }
        }
        _lastArrival = now;

        long id = now / BucketMilliseconds;
        int i = (int)(id % BucketCount);
        if (_bucketIds[i] != id)
        {
            _bucketIds[i] = id;
            _min[i] = delay;
            _max[i] = delay;
        }
        else
        {
            _min[i] = Math.Min(_min[i], delay);
            _max[i] = Math.Max(_max[i], delay);
        }
    }

    /// <summary>Разброс за минуту, мс; 0 — пакетов ещё не было.</summary>
    public int Spread(long now)
    {
        long oldest = now / BucketMilliseconds - BucketCount + 1;
        long min = long.MaxValue, max = long.MinValue;
        for (int i = 0; i < BucketCount; i++)
        {
            if (_bucketIds[i] < oldest)
                continue;
            min = Math.Min(min, _min[i]);
            max = Math.Max(max, _max[i]);
        }
        return min == long.MaxValue ? 0 : (int)(max - min);
    }

    private long? MinDelay(long now)
    {
        long oldest = now / BucketMilliseconds - BucketCount + 1;
        long min = long.MaxValue;
        for (int i = 0; i < BucketCount; i++)
        {
            if (_bucketIds[i] >= oldest)
                min = Math.Min(min, _min[i]);
        }
        return min == long.MaxValue ? null : min;
    }
}

/// <summary>
/// Сжатие кадра без пропуска слов: вырезается один период основного тона с плавным
/// переходом. Так уходит лишний запас. Тихий кадр сжимается всегда, звучащий —
/// только периодичный (гласные): сжатие шумного звука на слух — рывок.
/// </summary>
internal static class FrameCompressor
{
    // 2,5–5 мс: тон 200–400 Гц; два периода должны уместиться в кадр 10 мс.
    private const int MinLag = 120;
    private const int MaxLag = 240;
    private const double MinCorrelation = 0.8;
    private const double QuietRms = 150;

    /// <summary>Сжимает кадр на месте; возвращает новое число отсчётов.</summary>
    public static int Compress(Span<short> samples)
    {
        int length = samples.Length;
        if (length < 2 * MaxLag)
            return length;

        int lag = Rms(samples) < QuietRms ? MaxLag : BestLag(samples);
        if (lag == 0)
            return length;

        for (int i = 0; i < lag; i++)
        {
            float weight = (i + 1f) / (lag + 1);
            samples[i] = (short)(samples[i] * (1 - weight) + samples[i + lag] * weight);
        }
        samples[(2 * lag)..].CopyTo(samples[lag..]);
        return length - lag;
    }

    private static int BestLag(ReadOnlySpan<short> s)
    {
        int bestLag = 0;
        double best = MinCorrelation;
        for (int lag = MinLag; lag <= MaxLag; lag++)
        {
            double cross = 0, a = 0, b = 0;
            for (int i = 0; i < lag; i++)
            {
                double x = s[i], y = s[i + lag];
                cross += x * y;
                a += x * x;
                b += y * y;
            }
            if (a == 0 || b == 0)
                continue;
            double correlation = cross / Math.Sqrt(a * b);
            if (correlation > best)
            {
                best = correlation;
                bestLag = lag;
            }
        }
        return bestLag;
    }

    private static double Rms(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (short sample in samples)
            sum += sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }
}
