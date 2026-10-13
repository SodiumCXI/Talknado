using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.Models;

public enum AttachmentKind : byte { File, Image, Video }

public enum AttachmentState { Remote, Downloading, Ready, Failed }

public partial class Attachment : ObservableObject
{
    private const double MaxPreviewWidth = 320;
    private const double MaxPreviewHeight = 240;

    public required ushort OwnerId { get; init; }
    public required uint FileId { get; init; }
    public required AttachmentKind Kind { get; init; }
    public required string FileName { get; init; }
    public required long Size { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsOwn { get; init; }

    [ObservableProperty]
    private AttachmentState _state;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _localPath;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    /// <summary>Куда файл сохранён в «Загрузки»; null — ещё не сохранён.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaved))]
    private string? _savedPath;

    public bool IsSaved => SavedPath != null;

    public bool IsImage => Kind == AttachmentKind.Image;
    public bool IsVideo => Kind == AttachmentKind.Video;
    public bool IsFile => Kind == AttachmentKind.File;

    public string SizeText => FormatSize(Size);

    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : Duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    // Место под превью резервируется по размеру из объявления, до загрузки
    // картинки: иначе карточка вырастала бы и лента прыгала.
    public double PreviewWidth => Fit().Width;
    public double PreviewHeight => Fit().Height;

    private (double Width, double Height) Fit()
    {
        if (PixelWidth <= 0 || PixelHeight <= 0)
            return (MaxPreviewWidth, MaxPreviewHeight);

        double scale = Math.Min(1, Math.Min(MaxPreviewWidth / PixelWidth, MaxPreviewHeight / PixelHeight));
        return (Math.Round(PixelWidth * scale), Math.Round(PixelHeight * scale));
    }

    public static string FormatSize(long bytes)
    {
        var culture = CultureInfo.CurrentUICulture;
        return bytes switch
        {
            < 1024 => string.Format(culture, Strings.SizeBytesFormat, bytes),
            < 1024 * 1024 => string.Format(culture, Strings.SizeKilobytesFormat, bytes / 1024.0),
            < 1024L * 1024 * 1024 => string.Format(culture, Strings.SizeMegabytesFormat, bytes / (1024.0 * 1024)),
            _ => string.Format(culture, Strings.SizeGigabytesFormat, bytes / (1024.0 * 1024 * 1024)),
        };
    }
}
