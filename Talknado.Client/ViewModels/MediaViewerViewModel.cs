using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Talknado.Client.Models;
using Talknado.Client.Models.Helpers.Media;

namespace Talknado.Client.ViewModels;

public partial class MediaViewerViewModel(IMediaViewerManager mediaViewerManager, ISettingsManager settingsManager,
    IFileTransferManager fileTransferManager) : ObservableObject
{
    private readonly IMediaViewerManager _mediaViewerManager = mediaViewerManager;
    private readonly ISettingsManager _settingsManager = settingsManager;
    private readonly IFileTransferManager _fileTransferManager = fileTransferManager;

    // Позиция, время, загруженная часть и размер кадра обновляются четыре раза
    // в секунду, а не на каждый кадр и не на каждый шаг изменения размера окна.
    private readonly DispatcherTimer _positionTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };

    private readonly bool[] _bufferedBins = new bool[VideoPlayer.BufferedBins];
    private readonly bool[] _shownBins = new bool[VideoPlayer.BufferedBins];

    private VideoPlayer? _player;
    private CancellationTokenSource? _openCts;
    private Attachment? _attachment;
    private int _maxWidth;
    private int _maxHeight;
    private int _viewportWidth;
    private int _viewportHeight;
    private double _dpiScale = 1;
    // Наибольшая сторона картинки для приближения: предел текстуры с запасом на видеокарты попроще
    private const int ZoomImageMaxSide = 8192;

    // Вписанная картинка пересчитывается под новый размер окна, когда его перестали менять
    private DispatcherTimer? _imageResizeTimer;
    private int _imageLoads;
    // Вписанный размер картинки в пикселях экрана
    private (int Width, int Height) _fit;
    private bool _zoomImageLoading;
    private bool _rendering;
    private bool _isSeeking;

    public IMediaViewerManager Manager => _mediaViewerManager;

    /// <summary>Превью из сообщения, затем вписанная — для масштаба 1, один к одному.</summary>
    [ObservableProperty]
    private ImageSource? _image;

    /// <summary>
    /// Уровни для приближения, крупный первым; масштабирует их видеокарта. Готовятся
    /// сразу после вписанной и лежат в окне под ней: первое приближение не ждёт ни
    /// декодирования, ни передачи в видеокарту.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZoomImageShown))]
    private IReadOnlyList<BitmapSource>? _zoomLayers;

    /// <summary>Номер выведенного уровня: самый мелкий, который не меньше картинки на экране.</summary>
    [ObservableProperty]
    private int _shownZoomLayer;

    /// <summary>Показана сама картинка, а не превью из сообщения.</summary>
    [ObservableProperty]
    private bool _isImageFitted;

    /// <summary>
    /// Рамка картинки в единицах WPF — по исходнику, а не по загруженной картинке:
    /// превью, вписанная и приближенная растягиваются в одну и ту же рамку и не
    /// сдвигаются друг относительно друга. NaN — размер ещё неизвестен.
    /// </summary>
    [ObservableProperty]
    private double _imageWidth = double.NaN;

    [ObservableProperty]
    private double _imageHeight = double.NaN;

    /// <summary>Во сколько раз картинка крупнее вписанной в окно.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZoomImageShown))]
    private double _imageZoom = 1;

    /// <summary>Приближено, и вся картинка готова: вписанная поверх неё прячется.</summary>
    public bool IsZoomImageShown => ImageZoom > 1 && ZoomLayers != null;

    [ObservableProperty]
    private WriteableBitmap? _videoSurface;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private bool _openFailed;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private string _timeText = string.Empty;

    [ObservableProperty]
    private string _timeTextReserve = string.Empty;

    [ObservableProperty]
    private double _volume = 1;

    /// <summary>Загруженные отрезки дорожки: пары долей «начало, конец».</summary>
    [ObservableProperty]
    private double[]? _bufferedRanges;

    public void OnOpened(int maxWidth, int maxHeight)
    {
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _attachment = _mediaViewerManager.Current;
        OpenFailed = false;
        IsBuffering = false;

        if (_attachment == null)
            return;

        Image = _attachment.Thumbnail;
        IsImageFitted = false;
        UpdateImageFrame();
        _attachment.PropertyChanged += OnAttachmentChanged;

        if (_attachment.IsVideo)
            StartVideo(_attachment);
        else
            LoadImage();
    }

    public void OnClosed()
    {
        if (_attachment != null)
            _attachment.PropertyChanged -= OnAttachmentChanged;

        StopVideo();
        _imageResizeTimer?.Stop();
        _attachment = null;
        Image = null;
        ZoomLayers = null;
        _zoomImageLoading = false;
        IsImageFitted = false;
        ImageZoom = 1;
        ImageWidth = ImageHeight = double.NaN;
    }

    /// <summary>Размер области под картинку и видео в пикселях экрана.</summary>
    public void SetViewport(int width, int height, double dpiScale)
    {
        _viewportWidth = width;
        _viewportHeight = height;
        _dpiScale = dpiScale;

        UpdateImageFrame();
        ScheduleImageLoad();
    }

    private void UpdateImageFrame()
    {
        if (_attachment is not { IsImage: true, PixelWidth: > 0, PixelHeight: > 0 } attachment)
            return;

        _fit = MediaProbe.FitSize(attachment.PixelWidth, attachment.PixelHeight,
            _viewportWidth > 0 ? _viewportWidth : _maxWidth, _viewportHeight > 0 ? _viewportHeight : _maxHeight);
        ImageWidth = _fit.Width / _dpiScale;
        ImageHeight = _fit.Height / _dpiScale;
        UpdateZoomLayers();
    }

    partial void OnImageZoomChanged(double value) => UpdateZoomLayers();

    private void UpdateZoomLayers()
    {
        if (ZoomLayers is not { } layers)
            return;

        double shown = _fit.Width * ImageZoom;
        int chosen = 0;
        for (int i = 1; i < layers.Count && layers[i].PixelWidth >= shown; i++)
            chosen = i;
        ShownZoomLayer = chosen;
    }

    private void ScheduleImageLoad()
    {
        if (_attachment is not { IsImage: true })
            return;

        if (_imageResizeTimer == null)
        {
            _imageResizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            _imageResizeTimer.Tick += (_, _) =>
            {
                _imageResizeTimer.Stop();
                LoadImage();
            };
        }
        _imageResizeTimer.Stop();
        _imageResizeTimer.Start();
    }

    private void OnAttachmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Attachment.State) && _attachment is { IsImage: true })
            LoadImage();
    }

    private void LoadImage()
    {
        var attachment = _attachment;
        if (attachment?.State != AttachmentState.Ready || attachment.LocalPath is not { } path || _fit.Width == 0)
            return;

        var (width, height) = _fit;
        int load = ++_imageLoads;
        Task.Run(() =>
        {
            try { return MediaProbe.LoadImageScaled(path, width, height); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result != null && _attachment == attachment && load == _imageLoads)
            {
                Image = t.Result;
                IsImageFitted = true;
                LoadZoomLayers();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void LoadZoomLayers()
    {
        var attachment = _attachment;
        if (_zoomImageLoading || ZoomLayers != null || attachment?.State != AttachmentState.Ready
            || attachment.LocalPath is not { } path)
            return;

        _zoomImageLoading = true;
        int minWidth = _fit.Width;
        Task.Run(() =>
        {
            try { return MediaProbe.LoadZoomLevels(path, ZoomImageMaxSide, minWidth); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (_attachment != attachment)
                return;

            ZoomLayers = t.Result;
            UpdateZoomLayers();
            // Не загрузились — остаётся вписанная, без повторных попыток
            _zoomImageLoading = t.Result == null;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void StartVideo(Attachment attachment)
    {
        IMediaReader reader;
        try
        {
            reader = _fileTransferManager.OpenReader(attachment);
        }
        catch
        {
            OpenFailed = true;
            return;
        }

        _openCts = new CancellationTokenSource();
        var token = _openCts.Token;
        var deviceId = UsersAudioPlayer.ResolveOutputDeviceId(_settingsManager.SelectedOutputDevice);
        int width = _viewportWidth > 0 ? _viewportWidth : _maxWidth;
        int height = _viewportHeight > 0 ? _viewportHeight : _maxHeight;
        float volume = (float)Volume;

        IsBuffering = true;
        _positionTimer.Tick += OnPositionTick;
        _positionTimer.Start();

        Task.Run(() => new VideoPlayer(reader, width, height, deviceId, token) { Volume = volume })
            .ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    reader.Dispose();
                    if (_attachment == attachment && !token.IsCancellationRequested)
                    {
                        IsBuffering = false;
                        OpenFailed = true;
                    }
                    return;
                }

                if (_attachment != attachment || token.IsCancellationRequested)
                {
                    t.Result.Dispose();
                    return;
                }

                _player = t.Result;
                DurationSeconds = _player.Duration.TotalSeconds;
                PositionSeconds = 0;
                UpdateTimeText();
                TimeTextReserve = WidestDigits().Replace($"{FormatTime(DurationSeconds)} / {FormatTime(DurationSeconds)}", "4");

                _player.Play();
                IsPlaying = true;
                SetRendering(true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // Подписка на Rendering заставляет WPF перерисовывать окно на каждом кадре
    // монитора, поэтому она есть только пока видео идёт.
    private void SetRendering(bool enabled)
    {
        if (enabled == _rendering)
            return;

        if (enabled)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;

        _rendering = enabled;
    }

    private void StopVideo()
    {
        _openCts?.Cancel();
        _openCts = null;
        SetRendering(false);

        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTick;

        // Закрытие ждёт затухания звука — не в потоке UI
        if (_player is { } player)
            Task.Run(player.Dispose);
        _player = null;
        VideoSurface = null;
        IsPlaying = false;
        IsBuffering = false;
        DurationSeconds = 0;
        PositionSeconds = 0;
        TimeTextReserve = string.Empty;
        BufferedRanges = null;
        Array.Clear(_shownBins);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_player?.TakeDueFrame() is not { } frame)
            return;

        // Поверхность пересоздаётся только при смене размера кадра, то есть
        // после изменения размера окна, а не на каждый кадр.
        var surface = VideoSurface;
        if (surface == null || surface.PixelWidth != frame.Width || surface.PixelHeight != frame.Height)
            VideoSurface = surface = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);

        surface.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
        _player.ReturnFrame(frame);
    }

    private void OnPositionTick(object? sender, EventArgs e)
    {
        if (_player == null)
            return;

        _player.SetOutputSize(_viewportWidth, _viewportHeight);

        if (!_isSeeking)
            PositionSeconds = Math.Min(_player.Clock, DurationSeconds);

        IsBuffering = _player.IsBuffering;
        UpdateBuffered(_player);

        if (_player.IsEnded && IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            SetRendering(false);
        }

        // На паузе кадр после перемотки показывается здесь, без подписки на Rendering.
        if (!IsPlaying)
            OnRendering(null, EventArgs.Empty);

        UpdateTimeText();
    }

    private void UpdateBuffered(VideoPlayer player)
    {
        player.GetBuffered(_bufferedBins);
        if (_bufferedBins.AsSpan().SequenceEqual(_shownBins))
            return;

        _bufferedBins.CopyTo(_shownBins, 0);

        var ranges = new List<double>();
        for (int i = 0; i < _shownBins.Length; i++)
        {
            if (!_shownBins[i] || i > 0 && _shownBins[i - 1])
                continue;

            int end = i;
            while (end < _shownBins.Length && _shownBins[end])
                end++;

            ranges.Add((double)i / _shownBins.Length);
            ranges.Add((double)end / _shownBins.Length);
        }

        BufferedRanges = [.. ranges];
    }

    private void UpdateTimeText() =>
        TimeText = $"{FormatTime(PositionSeconds)} / {FormatTime(DurationSeconds)}";

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    // В шрифте темы четвёрка — самая широкая цифра.
    [GeneratedRegex(@"\d")]
    private static partial Regex WidestDigits();

    partial void OnVolumeChanged(double value)
    {
        if (_player != null)
            _player.Volume = (float)value;
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (_player == null)
            return;

        if (IsPlaying)
            _player.Pause();
        else
            _player.Play();

        IsPlaying = !IsPlaying;
        SetRendering(IsPlaying);
    }

    public void SeekBy(double seconds)
    {
        if (_player != null)
            SeekTo(PositionSeconds + seconds);
    }

    public void BeginSeek() => _isSeeking = true;

    public void EndSeek()
    {
        if (!_isSeeking)
            return;

        _isSeeking = false;
        SeekTo(PositionSeconds);
    }

    private void SeekTo(double seconds)
    {
        if (_player == null)
            return;

        seconds = Math.Clamp(seconds, 0, DurationSeconds);
        _player.Seek(TimeSpan.FromSeconds(seconds));
        PositionSeconds = seconds;
        UpdateTimeText();
    }

    [RelayCommand]
    private void Close()
    {
        _mediaViewerManager.IsWindowVisible = false;
    }
}
