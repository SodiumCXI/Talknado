using CommunityToolkit.Mvvm.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Talknado.Client.Models.Helpers.Network;
using Talknado.Client.Models.Helpers.ScreenShare;

namespace Talknado.Client.Models;

public interface IScreenSharePlayer
{
    void SaveLastKeyFrame(ReadOnlySpan<byte> h264Data);
    void ClearLastKeyFrame();
    void UpdateSavedKeyFrame();
    void UpdateFrame(ReadOnlySpan<byte> h264Data, CancellationToken token);
    void Clear();

    /// <summary>После пропуска кадров разброс задержки считается заново.</summary>
    void ResetTimeline();

    ImageSource DisplayImage { get; }
    int FramesPerSecond { get; }
    bool IsWindowVisible { get; set; }
    bool IsKeyFrameInitialized { get; set; }
    bool HasLastKeyFrame { get; }
    string ScreenShareUsername { get; set; }
}

public partial class ScreenSharePlayer : ObservableObject, IScreenSharePlayer, IDisposable
{
    private WriteableBitmap _displayBitmap = null!;
    public ImageSource DisplayImage => _displayBitmap;
    public bool IsKeyFrameInitialized { get; set; } = false;
    public bool HasLastKeyFrame => _lastKeyFrameLength > 0;

    // Опорный кадр приходит раз в секунду, поэтому копируется в один и тот же
    // массив, а не в новый.
    private byte[] _lastKeyFrame = [];
    private int _lastKeyFrameLength;

    // Кадр BGRA пишется декодером в _backPixels и меняется местами с
    // _frontPixels; поток UI рисует последний готовый. Если он не успевает,
    // промежуточные кадры пропускаются, а не копятся в очереди диспетчера.
    private byte[] _backPixels = [];
    private byte[] _frontPixels = [];
    private int _frontWidth;
    private int _frontHeight;
    private int _backWidth;
    private int _backHeight;
    private readonly object _frontLock = new();
    private int _renderPending;
    private readonly Action _renderAction;

    private readonly Timer _statsTimer = null!;
    private int _frameCount;

    private int _currentWidth;
    private int _currentHeight;
    private bool _isInitialized = false;

    private readonly H264Decoder _decoder = new();

    private readonly object _saveLock = new();
    private readonly object _queueLock = new();
    private readonly Queue<EncodedFrame> _frameQueue = new();
    private readonly CancellationTokenSource _playbackCts = new();
    private readonly Task _playbackTask;
    private readonly SemaphoreSlim _frameSignal = new(0);

    // Кадр показывается в «время отправителя + задержка показа». Задержка —
    // 99-й процентиль запаздывания последних кадров, но не меньше запаздывания
    // последних опорных с их декодированием: они редкие и в процентиль не попадают.
    // Меняется не быстрее 5 % скорости показа; кадр декодируется заранее.
    private const int TimelineWindow = 512;
    private const double TimelinePercentile = 0.99;
    private const double MaxRateChange = 0.05;
    private const double LateToleranceMs = 2;
    private const double MaxDelayMs = 1000;
    private const double DecodeLeadMs = 12;
    private const double DecodeMarginMs = 4;
    private const int KeyFrameWindow = 3;
    private readonly double[] _keyOffsets = new double[KeyFrameWindow];
    private int _keyOffsetCount;
    private int _keyOffsetNext;
    private double _keyDecodeMs = DecodeLeadMs;
    // Первое декодирование после сброса готовит декодер и в оценку не идёт.
    private volatile bool _decoderCold = true;
    private readonly double[] _offsets = new double[TimelineWindow];
    private readonly double[] _sortedOffsets = new double[TimelineWindow];
    private int _offsetCount;
    private int _offsetNext;
    private int _sinceEstimate;
    private long _senderMs;
    private double _targetOffsetMs = double.NaN;
    private double _playOffsetMs;
    private volatile bool _reanchor = true;

    [ObservableProperty]
    private bool _isWindowVisible = false;
    [ObservableProperty]
    private int _framesPerSecond = 0;
    [ObservableProperty]
    private string _screenShareUsername = string.Empty;

    public ScreenSharePlayer()
    {
        _statsTimer = new Timer(_ =>
        {
            FramesPerSecond = _frameCount;
            _frameCount = 0;
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        _renderAction = RenderFrame;
        _playbackTask = Task.Run(() => PlaybackLoop(_playbackCts.Token));
    }

    private static bool IsKeyFrame(ReadOnlySpan<byte> h264Data) => h264Data.Length >= 5 && h264Data[0] == 1;

    public void SaveLastKeyFrame(ReadOnlySpan<byte> h264Data)
    {
        if (!IsKeyFrame(h264Data))
            return;

        lock (_saveLock)
        {
            StoreKeyFrame(h264Data);
        }
    }

    private void StoreKeyFrame(ReadOnlySpan<byte> h264Data)
    {
        if (_lastKeyFrame.Length < h264Data.Length)
            _lastKeyFrame = new byte[h264Data.Length * 2];

        h264Data.CopyTo(_lastKeyFrame);
        _lastKeyFrameLength = h264Data.Length;
    }

    public void ClearLastKeyFrame()
    {
        lock (_saveLock)
        {
            _lastKeyFrameLength = 0;
        }
    }

    public void UpdateSavedKeyFrame()
    {
        lock (_saveLock)
        {
            if (_lastKeyFrameLength == 0)
                return;

            UpdateFrame(_lastKeyFrame.AsSpan(0, _lastKeyFrameLength), CancellationToken.None);
        }
    }

    public void UpdateFrame(ReadOnlySpan<byte> h264Data, CancellationToken token)
    {
        if (h264Data.Length < 5)
            throw new ArgumentException("Data must contain at least 5 bytes");

        bool isKeyFrame = IsKeyFrame(h264Data);

        if (!IsKeyFrameInitialized)
        {
            if (!isKeyFrame)
                return;

            lock (_saveLock)
            {
                StoreKeyFrame(h264Data);
                _decoder.FlushBuffers();
                _decoderCold = true;
                IsKeyFrameInitialized = true;
            }
        }

        int deltaMs = BinaryPrimitives.ReadInt32LittleEndian(h264Data[1..]);
        var data = h264Data.Length > 5 ? PooledBuffer.CopyOf(h264Data[5..]) : default;
        double arrivalMs = NowMs;

        lock (_queueLock)
        {
            _senderMs += deltaMs;
            _frameQueue.Enqueue(new EncodedFrame(deltaMs, data, _senderMs, arrivalMs, isKeyFrame));
            AddOffset(arrivalMs - _senderMs, isKeyFrame);
        }

        _frameSignal.Release();
    }

    private static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Под _queueLock.</summary>
    private void AddOffset(double offsetMs, bool isKeyFrame)
    {
        _offsets[_offsetNext] = offsetMs;
        _offsetNext = (_offsetNext + 1) % TimelineWindow;
        _offsetCount = Math.Min(_offsetCount + 1, TimelineWindow);

        if (isKeyFrame)
        {
            _keyOffsets[_keyOffsetNext] = offsetMs;
            _keyOffsetNext = (_keyOffsetNext + 1) % KeyFrameWindow;
            _keyOffsetCount = Math.Min(_keyOffsetCount + 1, KeyFrameWindow);
        }
        else if (++_sinceEstimate < 8 && !double.IsNaN(_targetOffsetMs))
        {
            return;
        }

        _sinceEstimate = 0;
        Array.Copy(_offsets, _sortedOffsets, _offsetCount);
        Array.Sort(_sortedOffsets, 0, _offsetCount);
        double fastest = _sortedOffsets[0];
        double target = _sortedOffsets[(int)((_offsetCount - 1) * TimelinePercentile)];
        for (int i = 0; i < _keyOffsetCount; i++)
            target = Math.Max(target, _keyOffsets[i] + _keyDecodeMs + DecodeMarginMs);
        _targetOffsetMs = Math.Min(target, fastest + MaxDelayMs);
    }

    public void ResetTimeline()
    {
        lock (_queueLock)
        {
            _offsetCount = 0;
            _offsetNext = 0;
            _keyOffsetCount = 0;
            _keyOffsetNext = 0;
            _targetOffsetMs = double.NaN;
        }
    }

    private async Task PlaybackLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            EncodedFrame frame;
            double targetOffsetMs;
            bool hasFrame;

            lock (_queueLock)
            {
                hasFrame = _frameQueue.TryPeek(out frame);
                targetOffsetMs = _targetOffsetMs;
            }

            if (!hasFrame)
            {
                await _frameSignal.WaitAsync(token);
                continue;
            }

            double arrivalOffsetMs = frame.ArrivalMs - frame.SenderMs;
            if (_reanchor || _playOffsetMs - arrivalOffsetMs > MaxDelayMs)
            {
                _playOffsetMs = arrivalOffsetMs;
                _reanchor = false;
            }

            // Опоздавший кадр показывается сразу, и дальше отсчёт идёт от него:
            // пришедшие следом кадры не проматываются, а идут своим шагом.
            if (arrivalOffsetMs > _playOffsetMs + LateToleranceMs)
                _playOffsetMs = arrivalOffsetMs;

            double dueMs = frame.SenderMs + _playOffsetMs;
            double leadMs = frame.IsKeyFrame ? Math.Max(DecodeLeadMs, _keyDecodeMs + DecodeMarginMs) : DecodeLeadMs;
            double waitMs = dueMs - leadMs - NowMs;
            if (waitMs >= 1)
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), token);

            lock (_queueLock)
                _frameQueue.TryDequeue(out _);

            if (!double.IsNaN(targetOffsetMs))
            {
                double step = frame.DeltaMs * MaxRateChange;
                _playOffsetMs = _playOffsetMs > targetOffsetMs
                    ? Math.Max(targetOffsetMs, _playOffsetMs - step)
                    : Math.Min(targetOffsetMs, _playOffsetMs + step);
            }

            if (frame.Data.Length == 0)
                continue;

            bool ready;
            try
            {
                ready = Decode(frame);
            }
            finally
            {
                frame.Data.Return();
            }

            if (!ready)
                continue;

            waitMs = dueMs - NowMs;
            if (waitMs >= 1)
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), token);

            Publish(token);
        }
    }

    /// <summary>Кадр BGRA в _backPixels; false — показывать нечего.</summary>
    private bool Decode(EncodedFrame frame)
    {
        if (!IsWindowVisible)
        {
            lock (_queueLock)
            {
                while (_frameQueue.TryDequeue(out var stale))
                    stale.Data.Return();
            }
            _reanchor = true;

            return false;
        }

        try
        {
            long started = Stopwatch.GetTimestamp();
            if (!_decoder.Decode(frame.Data.Span))
                return false;

            int width = _decoder.Width;
            int height = _decoder.Height;

            if (!_isInitialized || _currentWidth != width || _currentHeight != height)
                InitializeBitmap(width, height);

            if (_backPixels.Length != width * height * 4)
                _backPixels = new byte[width * height * 4];

            _decoder.ConvertTo(_backPixels);
            _backWidth = width;
            _backHeight = height;

            if (_decoderCold)
            {
                _decoderCold = false;
            }
            else if (frame.IsKeyFrame)
            {
                double decodeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (_queueLock)
                    _keyDecodeMs = Math.Max(decodeMs, (_keyDecodeMs + decodeMs) / 2);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Frame update failed: {ex.Message}");
            return false;
        }
    }

    private void Publish(CancellationToken token)
    {
        lock (_frontLock)
        {
            (_backPixels, _frontPixels) = (_frontPixels, _backPixels);
            _frontWidth = _backWidth;
            _frontHeight = _backHeight;
        }

        _frameCount++;

        if (token.IsCancellationRequested)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted &&
            Interlocked.Exchange(ref _renderPending, 1) == 0)
        {
            dispatcher.BeginInvoke(_renderAction, DispatcherPriority.Render);
        }
    }

    private void InitializeBitmap(int width, int height)
    {
        _currentWidth = width;
        _currentHeight = height;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _displayBitmap = new WriteableBitmap(
                width, height,
                96, 96,
                PixelFormats.Bgra32,
                null);

            OnPropertyChanged(nameof(DisplayImage));
        });

        _isInitialized = true;
    }

    private unsafe void RenderFrame()
    {
        Volatile.Write(ref _renderPending, 0);

        lock (_frontLock)
        {
            int width = _frontWidth;
            int height = _frontHeight;

            if (_displayBitmap == null || _displayBitmap.PixelWidth != width || _displayBitmap.PixelHeight != height)
                return;

            CopyToBitmap(_frontPixels, width, height);
        }
    }

    private unsafe void CopyToBitmap(byte[] bgraPixels, int width, int height)
    {
        _displayBitmap.Lock();
        try
        {
            int stride = width * 4;

            fixed (byte* src = bgraPixels)
            {
                byte* dst = (byte*)_displayBitmap.BackBuffer;
                int bufferStride = _displayBitmap.BackBufferStride;

                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * stride,
                        dst + y * bufferStride,
                        stride,
                        stride);
                }
            }

            _displayBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _displayBitmap.Unlock();
        }
    }

    public void Clear()
    {
        if (!_isInitialized || _displayBitmap == null)
            return;

        _displayBitmap = new WriteableBitmap(
            _currentWidth, _currentHeight,
            96, 96,
            PixelFormats.Bgra32,
            null);

        OnPropertyChanged(nameof(DisplayImage));
    }

    public void Dispose()
    {
        _playbackCts.Cancel();
        _playbackCts.Dispose();

        lock (_queueLock)
        {
            while (_frameQueue.TryDequeue(out var frame))
                frame.Data.Return();
        }
        _statsTimer?.Dispose();
        _decoder.Cleanup();

        GC.SuppressFinalize(this);
    }

    /// <summary>Данные H.264 из пула; возвращает тот, кто вынул кадр из очереди.</summary>
    public readonly record struct EncodedFrame(int DeltaMs, PooledBuffer Data, long SenderMs, double ArrivalMs, bool IsKeyFrame);
}