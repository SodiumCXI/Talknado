using FFmpeg.AutoGen;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Talknado.Client.Models.Helpers.Media;

public sealed record ProbeResult(AttachmentKind Kind, int Width, int Height, TimeSpan Duration,
    byte[]? PreviewJpeg, BitmapSource? Thumbnail);

/// <summary>Определяет вид вложения и готовит превью. Вызывается не из потока UI.</summary>
public static unsafe class MediaProbe
{
    public const int ThumbnailWidth = 720;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".3gp" };

    public static ProbeResult Probe(string path)
    {
        var extension = Path.GetExtension(path);

        try
        {
            if (ImageExtensions.Contains(extension))
            {
                var (width, height) = ReadImageSize(path);
                return new(AttachmentKind.Image, width, height, TimeSpan.Zero, null, LoadImage(path, ThumbnailWidth));
            }

            if (VideoExtensions.Contains(extension))
                return ProbeVideo(path);
        }
        catch { /* не открылось как картинка или видео — уходит обычным файлом */ }

        return new(AttachmentKind.File, 0, 0, TimeSpan.Zero, null, null);
    }

    public static BitmapSource LoadImage(string path, int decodePixelWidth = 0)
    {
        using var stream = File.OpenRead(path);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (decodePixelWidth > 0)
        {
            var (width, _) = ReadImageSize(path);
            if (width > decodePixelWidth)
                image.DecodePixelWidth = decodePixelWidth;
        }
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Размер картинки, вписанной в область, в пикселях; не крупнее своего.</summary>
    public static (int Width, int Height) FitSize(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        int width = sourceWidth, height = sourceHeight;
        if (sourceWidth > maxWidth || sourceHeight > maxHeight)
        {
            if ((long)maxWidth * sourceHeight <= (long)maxHeight * sourceWidth)
                (width, height) = (maxWidth, (int)((long)sourceHeight * maxWidth / sourceWidth));
            else
                (width, height) = ((int)((long)sourceWidth * maxHeight / sourceHeight), maxHeight);
        }
        return (Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// Уровни для приближения: вся картинка и её уменьшения вдвое, вдвое… — пока
    /// больше <paramref name="minWidth"/>; первым идёт самый крупный. Видеокарта
    /// уменьшает простой выборкой, а она больше чем вдвое даёт ступеньки и рябь,
    /// поэтому окно выводит ближайший уровень не меньше нужного размера.
    /// Длинная сторона не больше <paramref name="maxSide"/>: крупнее видеокарта может
    /// не принять текстурой, и WPF рисовал бы её процессором.
    /// </summary>
    public static List<BitmapSource> LoadZoomLevels(string path, int maxSide, int minWidth)
    {
        var (sourceWidth, sourceHeight) = ReadImageSize(path);
        int longest = Math.Max(sourceWidth, sourceHeight);
        var levels = new List<BitmapSource> { LoadImage(path, longest > maxSide ? (int)((long)sourceWidth * maxSide / longest) : 0) };

        int width = levels[0].PixelWidth, height = levels[0].PixelHeight;
        if (width / 2 <= minWidth)
            return levels;

        var pixels = CopyPixels(levels[0]);
        while (width / 2 > minWidth)
        {
            int halfWidth = width / 2, halfHeight = Math.Max(1, height / 2);
            pixels = Resize(pixels, width, height, halfWidth, halfHeight);
            (width, height) = (halfWidth, halfHeight);
            levels.Add(CreateBitmap(pixels, width, height));
        }
        return levels;
    }

    /// <summary>
    /// Картинка ровно заданного размера в пикселях экрана. Уменьшение WPF при
    /// выводе мягкое, поэтому окну отдаётся готовый размер (Lanczos), и оно
    /// рисует картинку один к одному.
    /// </summary>
    public static BitmapSource LoadImageScaled(string path, int width, int height)
    {
        // Большой снимок декодируется сразу вдвое крупнее нужного: целиком 50 Мп — это 200 МБ.
        var decoded = LoadImage(path, width * 2);
        var pixels = CopyPixels(decoded);
        if (decoded.PixelWidth != width || decoded.PixelHeight != height)
            pixels = Resize(pixels, decoded.PixelWidth, decoded.PixelHeight, width, height);
        return CreateBitmap(pixels, width, height);
    }

    private static byte[] CopyPixels(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

    private static BitmapSource CreateBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Lanczos: BGRA <paramref name="width"/>×<paramref name="height"/> в новый размер.</summary>
    private static byte[] Resize(byte[] pixels, int width, int height, int newWidth, int newHeight)
    {
        var result = new byte[newWidth * newHeight * 4];
        var context = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            newWidth, newHeight, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_LANCZOS, null, null, null);
        if (context == null)
            throw new InvalidOperationException();
        try
        {
            fixed (byte* from = pixels)
            fixed (byte* to = result)
            {
                var sourceData = new byte_ptrArray4 { [0] = from };
                var sourceLines = new int_array4 { [0] = width * 4 };
                var targetData = new byte_ptrArray4 { [0] = to };
                var targetLines = new int_array4 { [0] = newWidth * 4 };
                ffmpeg.sws_scale(context, sourceData, sourceLines, 0, height, targetData, targetLines);
            }
        }
        finally
        {
            ffmpeg.sws_freeContext(context);
        }
        return result;
    }

    public static BitmapSource LoadImage(byte[] data)
    {
        using var stream = new MemoryStream(data);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static (int Width, int Height) ReadImageSize(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static ProbeResult ProbeVideo(string path)
    {
        AVFormatContext* format = null;
        AVCodecContext* codecContext = null;
        AVPacket* packet = null;
        AVFrame* frame = null;

        try
        {
            if (ffmpeg.avformat_open_input(&format, path, null, null) < 0)
                throw new InvalidDataException();

            if (ffmpeg.avformat_find_stream_info(format, null) < 0)
                throw new InvalidDataException();

            AVCodec* codec = null;
            int streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (streamIndex < 0 || codec == null)
                throw new InvalidDataException();

            var stream = format->streams[streamIndex];
            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(codecContext, stream->codecpar);
            if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
                throw new InvalidDataException();

            var duration = format->duration > 0
                ? TimeSpan.FromMilliseconds(format->duration / 1000.0)
                : TimeSpan.Zero;

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();

            byte[]? preview = null;
            BitmapSource? thumbnail = null;

            for (int i = 0; i < 1000 && thumbnail == null && ffmpeg.av_read_frame(format, packet) >= 0; i++)
            {
                if (packet->stream_index == streamIndex && ffmpeg.avcodec_send_packet(codecContext, packet) >= 0
                    && ffmpeg.avcodec_receive_frame(codecContext, frame) >= 0)
                {
                    thumbnail = ToBitmap(frame, ThumbnailWidth);
                    preview = EncodeJpeg(thumbnail);
                }

                ffmpeg.av_packet_unref(packet);
            }

            return new(AttachmentKind.Video, stream->codecpar->width, stream->codecpar->height, duration, preview, thumbnail);
        }
        finally
        {
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (codecContext != null) ffmpeg.avcodec_free_context(&codecContext);
            if (format != null) ffmpeg.avformat_close_input(&format);
        }
    }

    private static BitmapSource ToBitmap(AVFrame* frame, int maxWidth)
    {
        int width = Math.Min(frame->width, maxWidth);
        int height = (int)Math.Round((double)frame->height * width / frame->width);

        var sws = ffmpeg.sws_getContext(frame->width, frame->height, (AVPixelFormat)frame->format,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);

        try
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];

            fixed (byte* destination = pixels)
            {
                byte*[] data = [destination];
                int[] lines = [stride];
                ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, frame->height, data, lines);
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            ffmpeg.sws_freeContext(sws);
        }
    }

    private static byte[] EncodeJpeg(BitmapSource bitmap)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
