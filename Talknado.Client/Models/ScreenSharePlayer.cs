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
    void UpdateFrame(byte[] h264Data, CancellationToken token);
    ImageSource DisplayImage { get; }
    int FramesPerSecond { get; }
    bool IsWindowVisible { get; set; }
    string ScreenShareUsername { get; set; }
}

public partial class ScreenSharePlayer : ObservableObject, IScreenSharePlayer, IDisposable
{
    private WriteableBitmap _displayBitmap = null!;
    public ImageSource DisplayImage => _displayBitmap;

    private readonly Timer _statsTimer = null!;
    private int _frameCount;

    private int _currentWidth;
    private int _currentHeight;
    private bool _isInitialized = false;

    [ObservableProperty]
    private bool _isWindowVisible = false;
    [ObservableProperty]
    private int _framesPerSecond = 0;
    [ObservableProperty]
    private string _screenShareUsername = string.Empty;

    public ScreenSharePlayer()
    {
        H264Decoder.Initialize();

        _statsTimer = new Timer(_ =>
        {
            FramesPerSecond = _frameCount;
            _frameCount = 0;
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void UpdateFrame(byte[] h264Data, CancellationToken token)
    {
        try
        {
            byte[] bgraPixels = H264Decoder.Decode(h264Data);

            if (bgraPixels == null)
                return;

            int width = H264Decoder.Width;
            int height = H264Decoder.Height;

            if (!_isInitialized || _currentWidth != width || _currentHeight != height)
            {
                InitializeBitmap(width, height);
            }

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

        Application.Current.Dispatcher.Invoke(() =>
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

        Application.Current.Dispatcher.Invoke(() =>
        {
            var blackPixels = new byte[_displayBitmap.PixelWidth * _displayBitmap.PixelHeight * 4];
            _displayBitmap.WritePixels(
                new Int32Rect(0, 0, _displayBitmap.PixelWidth, _displayBitmap.PixelHeight),
                blackPixels,
                _displayBitmap.PixelWidth * 4,
                0);
        });
    }

    public void Dispose()
    {
        _statsTimer?.Dispose();
        H264Decoder.Cleanup();

        GC.SuppressFinalize(this);
    }
}