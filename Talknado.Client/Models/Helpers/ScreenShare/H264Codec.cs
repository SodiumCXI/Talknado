using FFmpeg.AutoGen;
using System.Management;
using System.Runtime.InteropServices;

namespace Talknado.Client.Models.Helpers.ScreenShare;

public static unsafe class H264Encoder
{
    private const int MAX_PIXELS = 921600;

    private static AVCodec* _codec;
    private static AVCodecContext* _codecContext;
    private static AVFrame* _frame;
    private static AVPacket* _packet;
    private static SwsContext* _swsContext;
    private static int _width;
    private static int _height;
    private static int _frameCount = 0;
    private static bool _initialized = false;
    private static readonly bool _hasDiscreteGPU;

    static H264Encoder()
    {
        _hasDiscreteGPU = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController")
            .Get()
            .Cast<ManagementObject>()
            .Any(obj =>
            {
                string name = obj["Name"]?.ToString()?.ToLower() ?? "";
                if (name.Contains("microsoft basic") || name.Contains("vga compatible"))
                    return false;

                if (obj["AdapterRAM"] != null)
                {
                    ulong vramMB = Convert.ToUInt64(obj["AdapterRAM"]) / (1024 * 1024);
                    return vramMB >= 2000;
                }
                return false;
            });
    }


    public static void Initialize(int inputWidth, int inputHeight)
    {
        if (_initialized)
            Cleanup();

        _width = inputWidth;
        _height = inputHeight;

        CalculateScaledDimensions(inputWidth, inputHeight, out var width, out var height);

        _frameCount = 0;

        string[] codecNames = ["h264_nvenc", "h264_amf", "libx264"];
        bool codecOpened = false;

        foreach (var codecName in codecNames)
        {
            if (!_hasDiscreteGPU && (codecName == "h264_nvenс" || codecName == "h264_amf"))
                continue;

            _codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
            if (_codec == null)
                continue;

            _codecContext = ffmpeg.avcodec_alloc_context3(_codec);
            _codecContext->width = width;
            _codecContext->height = height;
            _codecContext->time_base = new AVRational { num = 1, den = 30 };
            _codecContext->framerate = new AVRational { num = 30, den = 1 };
            _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            _codecContext->gop_size = 30;
            _codecContext->max_b_frames = 0;

            if (codecName.Contains("nvenc"))
            {
                ffmpeg.av_opt_set(_codecContext->priv_data, "preset", "p4", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "tune", "hq", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "rc", "cqp", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "cq", "30", 0);
            }
            else if (codecName.Contains("amf"))
            {
                ffmpeg.av_opt_set(_codecContext->priv_data, "usage", "lowlatency", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "rc", "cqp", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "qp_i", "30", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "qp_p", "32", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "profile", "high", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "bf", "0", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "lowlatency", "true", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "quality", "speed", 0);
            }
            else if (codecName.Contains("libx"))
            {
                ffmpeg.av_opt_set(_codecContext->priv_data, "preset", "ultrafast", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "tune", "zerolatency", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "crf", "32", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "x264-params", "nal-hrd=cbr:force-cfr=1:aq-mode=0:ref=1", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "intra-refresh", "1", 0);
            }

            var ret = ffmpeg.avcodec_open2(_codecContext, _codec, null);
            if (ret >= 0)
            {
                codecOpened = true;
                break;
            }
            else
            {
                fixed (AVCodecContext** ptr = &_codecContext)
                    ffmpeg.avcodec_free_context(ptr);
                _codecContext = null;
            }
        }

        if (!codecOpened)
            throw new Exception("Failed to open any H.264 codec");


        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        _frame->width = width;
        _frame->height = height;
        ffmpeg.av_frame_get_buffer(_frame, 32);

        _packet = ffmpeg.av_packet_alloc();

        _swsContext = ffmpeg.sws_getContext(
            inputWidth, inputHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);

        _initialized = true;
    }

    private static void CalculateScaledDimensions(int inputWidth, int inputHeight, out int width, out int height)
    {
        width = inputWidth;
        height = inputHeight;

        if (width * height > MAX_PIXELS)
        {
            double scale = Math.Sqrt((double)MAX_PIXELS / (width * height));
            width = (int)(width * scale);
            height = (int)(height * scale);

            width = width / 2 * 2;
            height = height / 2 * 2;
        }
    }

    public static byte[] Encode(byte[] bgraData, int deltaMs)
    {
        if (!_initialized)
            throw new InvalidOperationException("Encoder is not initialized. Call Initialize()");

        if (bgraData.Length != _width * _height * 4)
            throw new ArgumentException($"Invalid data size. Expected {_width * _height * 4} bytes");

        fixed (byte* pBgra = bgraData)
        {
            byte*[] srcData = [pBgra];
            int[] srcLinesize = [_width * 4];
            ffmpeg.sws_scale(_swsContext, srcData, srcLinesize, 0, _height, _frame->data, _frame->linesize);
        }

        _frame->pts = _frameCount++;

        ffmpeg.avcodec_send_frame(_codecContext, _frame);

        int ret = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            return null!;

        if (ret < 0)
            throw new Exception($"Encoding error: {ret}");

        bool isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
        byte[] encodedData = new byte[5 + _packet->size];
        encodedData[0] = isKeyFrame ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(encodedData.AsSpan(1, 4), deltaMs);
        Marshal.Copy((IntPtr)_packet->data, encodedData, 5, _packet->size);

        ffmpeg.av_packet_unref(_packet);
        return encodedData;
    }

    public static void Resize(int width, int height)
    {
        Initialize(width, height);
    }

    public static void Cleanup()
    {
        if (!_initialized) return;

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_packet != null)
        {
            fixed (AVPacket** ptr = &_packet)
                ffmpeg.av_packet_free(ptr);
            _packet = null;
        }

        if (_frame != null)
        {
            fixed (AVFrame** ptr = &_frame)
                ffmpeg.av_frame_free(ptr);
            _frame = null;
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** ptr = &_codecContext)
                ffmpeg.avcodec_free_context(ptr);
            _codecContext = null;
        }

        _initialized = false;
    }
}

public unsafe class H264Decoder
{
    private AVCodec* _codec;
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private int _width;
    private int _height;
    private bool _initialized = false;

    public H264Decoder()
    {
        Initialize();
    }

    public void Initialize()
    {
        if (_initialized)
            Cleanup();

        _codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (_codec == null)
            throw new Exception("H.264 decoder not found");

        _codecContext = ffmpeg.avcodec_alloc_context3(_codec);
        if (_codecContext == null)
            throw new Exception("Failed to create decoder context");

        int ret = ffmpeg.avcodec_open2(_codecContext, _codec, null);
        if (ret < 0)
            throw new Exception($"Failed to open decoder: {ret}");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        _initialized = true;
    }

    public void FlushBuffers()
    {
        if (!_initialized)
            throw new InvalidOperationException("Decoder is not initialized. Call Initialize()");

        ffmpeg.avcodec_flush_buffers(_codecContext);
    }

    public byte[] Decode(byte[] h264Data)
    {
        if (!_initialized)
            throw new InvalidOperationException("Decoder is not initialized. Call Initialize()");

        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null)
            throw new Exception("av_packet_alloc failed");

        int ret = ffmpeg.av_new_packet(pkt, h264Data.Length);
        if (ret < 0)
            throw new Exception("av_new_packet failed");

        fixed (byte* src = h264Data)
        {
            Buffer.MemoryCopy(
                src,
                pkt->data,
                h264Data.Length,
                h264Data.Length);
        }

        ret = ffmpeg.avcodec_send_packet(_codecContext, pkt);
        ffmpeg.av_packet_free(&pkt);

        if (ret < 0)
            throw new Exception($"avcodec_send_packet error {ret}");


        ret = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            return null!;

        if (ret < 0)
            throw new Exception($"Decoding error: {ret}");

        if (_width != _frame->width || _height != _frame->height)
        {
            _width = _frame->width;
            _height = _frame->height;

            _swsContext = ffmpeg.sws_getCachedContext(_swsContext,
            _width, _height, (AVPixelFormat)_frame->format,
            _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);
        }

        byte[] bgraData = new byte[_width * _height * 4];
        fixed (byte* pBgra = bgraData)
        {
            byte*[] dstData = [pBgra];
            int[] dstLinesize = [_width * 4];
            ffmpeg.sws_scale(_swsContext, _frame->data, _frame->linesize, 0, _height, dstData, dstLinesize);
        }

        return bgraData;
    }

    public int Width => _width;

    public int Height => _height;

    public void Cleanup()
    {
        if (!_initialized) return;

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_packet != null)
        {
            fixed (AVPacket** ptr = &_packet)
                ffmpeg.av_packet_free(ptr);
            _packet = null;
        }

        if (_frame != null)
        {
            fixed (AVFrame** ptr = &_frame)
                ffmpeg.av_frame_free(ptr);
            _frame = null;
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** ptr = &_codecContext)
                ffmpeg.avcodec_free_context(ptr);
            _codecContext = null;
        }

        _initialized = false;
    }
}