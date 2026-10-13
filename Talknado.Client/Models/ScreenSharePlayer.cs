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

    private const int MIN_BUFFER_FRAMES = 1;
    private const int MAX_BUFFER_FRAMES = 30;
    private readonly object _jitterLock = new();
    private long _lastArrivalTimestamp = 0;
    private double _jitterEma = 0;
    private int _lastDeltaMs = 33;
    private volatile int _targetBufferFrames = MIN_BUFFER_FRAMES;
    private long _lastShownTicks;

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
                IsKeyFrameInitialized = true;
            }
        }

        var frame = new EncodedFrame(
            BinaryPrimitives.ReadInt32LittleEndian(h264Data[1..]),
            h264Data.Length > 5 ? PooledBuffer.CopyOf(h264Data[5..]) : default);

        lock (_queueLock)
        {
            _frameQueue.Enqueue(frame);
        }

        lock (_jitterLock)
        {
            long now = Stopwatch.GetTimestamp();

            if (_lastArrivalTimestamp != 0)
            {
                double arrivalIntervalMs = (now - _lastArrivalTimestamp) * 1000.0 / Stopwatch.Frequency;
                double jitter = Math.Abs(arrivalIntervalMs - frame.DeltaMs);
                if (jitter > frame.DeltaMs * 5) jitter = _jitterEma;

                double alpha = jitter > _jitterEma ? 0.5 : 0.005;
                _jitterEma = alpha * jitter + (1 - alpha) * _jitterEma;
                if (frame.DeltaMs > 0) _lastDeltaMs = frame.DeltaMs;
                _targetBufferFrames = Math.Clamp((int)Math.Ceiling(_jitterEma / _lastDeltaMs), MIN_BUFFER_FRAMES, MAX_BUFFER_FRAMES);
            }

            _lastArrivalTimestamp = now;
        }

        _frameSignal.Release();
    }

    private async Task PlaybackLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            EncodedFrame? item = null;
            int queueSize;

            lock (_queueLock)
            {
                queueSize = _frameQueue.Count;
                if (queueSize > 0)
                    item = _frameQueue.Dequeue();
            }

            if (item is null)
            {
                await _frameSignal.WaitAsync(token);
                continue;
            }

            if (item.Value.Data.Length == 0)
                continue;

            float catchUpFactor = queueSize <= _targetBufferFrames
                ? 1.0f
                : queueSize >= (MAX_BUFFER_FRAMES + _targetBufferFrames)
                    ? 0.0f
                    : 1.0f - ((float)(queueSize - _targetBufferFrames) / MAX_BUFFER_FRAMES);

            // Пауза отсчитывается от показа прошлого кадра, а не от конца его
            // декодирования: иначе время декодирования прибавлялось к интервалу,
            // и очередь стояла на двадцати кадрах — столько же задержки картинки.
            long now = Stopwatch.GetTimestamp();
            long due = _lastShownTicks + (long)(item.Value.DeltaMs * catchUpFactor * Stopwatch.Frequency / 1000);
            _lastShownTicks = Math.Max(due, now);
            int delay = (int)((due - now) * 1000 / Stopwatch.Frequency);
            if (delay > 0)
                await Task.Delay(delay, token);

            try
            {
                DecodeAndRender(item.Value, token);
            }
            finally
            {
                item.Value.Data.Return();
            }
        }
    }

    private void DecodeAndRender(EncodedFrame frame, CancellationToken token)
    {
        if (!IsWindowVisible)
        {
            lock (_queueLock)
            {
                while (_frameQueue.TryDequeue(out var stale))
                    stale.Data.Return();
            }

            return;
        }

        try
        {
            if (!_decoder.Decode(frame.Data.Span))
                return;

            int width = _decoder.Width;
            int height = _decoder.Height;

            if (!_isInitialized || _currentWidth != width || _currentHeight != height)
                InitializeBitmap(width, height);

            if (_backPixels.Length != width * height * 4)
                _backPixels = new byte[width * height * 4];

            _decoder.ConvertTo(_backPixels);

            lock (_frontLock)
            {
                (_backPixels, _frontPixels) = (_frontPixels, _backPixels);
                _frontWidth = width;
                _frontHeight = height;
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
        catch (Exception ex)
        {
            Debug.WriteLine($"Frame update failed: {ex.Message}");
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
    public readonly record struct EncodedFrame(int DeltaMs, PooledBuffer Data);
}