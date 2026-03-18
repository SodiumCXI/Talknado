using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Talknado.Client.Models.Helpers.ScreenShare;

namespace Talknado.Client.Models;

public interface IScreenSharePlayer
{
    void SaveLastKeyFrame(byte[] h264Data);
    void ClearLastKeyFrame();
    void UpdateSavedKeyFrame();
    void UpdateFrame(byte[] h264Data, CancellationToken token);
    void Clear();
    ImageSource DisplayImage { get; }
    int FramesPerSecond { get; }
    bool IsWindowVisible { get; set; }
    bool IsKeyFrameInitialized { get; set; }
    byte[]? LastKeyFrame { get; }
    string ScreenShareUsername { get; set; }
}

public partial class ScreenSharePlayer : ObservableObject, IScreenSharePlayer, IDisposable
{
    private WriteableBitmap _displayBitmap = null!;
    public ImageSource DisplayImage => _displayBitmap;
    public bool IsKeyFrameInitialized { get; set; } = false;
    public byte[]? LastKeyFrame { get; private set; } = null;

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

        _playbackTask = Task.Run(() => PlaybackLoop(_playbackCts.Token));
    }

    public void SaveLastKeyFrame(byte[] h264Data)
    {
        var frame = new EncodedFrame(h264Data);

        if (!frame.IsKeyFrame)
            return;

        lock (_saveLock)
        {
            LastKeyFrame = [.. h264Data];
        }
    }

    public void ClearLastKeyFrame()
    {
        LastKeyFrame = null;
    }

    public void UpdateSavedKeyFrame()
    {
        if (LastKeyFrame == null)
            return;

        UpdateFrame(LastKeyFrame, CancellationToken.None);
    }

    public void UpdateFrame(byte[] h264Data, CancellationToken token)
    {
        var frame = new EncodedFrame(h264Data);

        if (!IsKeyFrameInitialized)
        {
            if (!frame.IsKeyFrame)
                return;

            lock (_saveLock)
            {
                LastKeyFrame = [.. h264Data];
                _decoder.FlushBuffers();
                IsKeyFrameInitialized = true;
            }
        }

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

            int targetFrames = _targetBufferFrames;

            float catchUpFactor = queueSize <= targetFrames
                ? 1.0f
                : queueSize >= (MAX_BUFFER_FRAMES + targetFrames)
                    ? 0.0f
                    : 1.0f - ((float)(queueSize - targetFrames) / ((MAX_BUFFER_FRAMES + targetFrames) - targetFrames));

            int delay = (int)(item.Value.DeltaMs * catchUpFactor);
            if (delay > 0)
                await Task.Delay(delay, token);

            DecodeAndRender(item.Value, token);
        }
    }

    private void DecodeAndRender(EncodedFrame frame, CancellationToken token)
    {
        if (!IsWindowVisible)
            return;

        try
        {
            byte[] bgraPixels;
            int width, height;

                bgraPixels = _decoder.Decode(frame.Data);
                if (bgraPixels == null)
                    return;

                width = _decoder.Width;
                height = _decoder.Height;

                if (!_isInitialized || _currentWidth != width || _currentHeight != height)
                    InitializeBitmap(width, height);

            _frameCount++;

            if (token.IsCancellationRequested)
                return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(
                    () => RenderFrame(bgraPixels, width, height),
                    DispatcherPriority.Render);
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

    private unsafe void RenderFrame(byte[] bgraPixels, int width, int height)
    {
        if (_displayBitmap == null)
            return;

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
        _statsTimer?.Dispose();
        _decoder.Cleanup();

        GC.SuppressFinalize(this);
    }

    public readonly struct EncodedFrame
    {
        public bool IsKeyFrame { get; }
        public int DeltaMs { get; }
        public byte[] Data { get; }

        public EncodedFrame(byte[] encodedData)
        {
            if (encodedData == null || encodedData.Length < 5)
                throw new ArgumentException("Data must contain at least 5 bytes");

            IsKeyFrame = encodedData[0] == 1;
            DeltaMs = BitConverter.ToInt32(encodedData, 1);
            Data = new byte[encodedData.Length - 5];
            Array.Copy(encodedData, 5, Data, 0, Data.Length);
        }
    }
}