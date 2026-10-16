using FFmpeg.AutoGen;
using System.Buffers.Binary;
using System.Management;

namespace Talknado.Client.Models.Helpers.ScreenShare;

public static unsafe class H264Encoder
{
    private static ShareQualityProfile _profile = ShareQuality.Resolve(0);

    private static AVCodec* _codec;
    private static AVCodecContext* _codecContext;
    private static AVFrame* _frame;
    private static AVPacket* _packet;
    private static int _width;
    private static int _height;
    private static int _frameCount = 0;
    private static bool _initialized = false;
    private static volatile bool _keyFrameRequested;
    private static readonly bool _hasDiscreteGPU;

    // Перевод BGRA в YUV считается полосами параллельно. При масштабировании
    // полоса одна: фильтр на стыке полос не видел бы соседних строк.
    private static ConvertBand[] _bands = [];
    private static byte* _bgra;
    private static readonly Action<int> _convertBand = ConvertBandAt;
    private static byte[] _output = [];

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

    public static bool HasHardwareEncoder => _hasDiscreteGPU;

    public static void Initialize(int inputWidth, int inputHeight, ShareQualityProfile profile)
    {
        _profile = profile;
        Initialize(inputWidth, inputHeight);
    }

    public static void Initialize(int inputWidth, int inputHeight)
    {
        if (_initialized)
            Cleanup();

        _width = inputWidth;
        _height = inputHeight;

        CalculateScaledDimensions(inputWidth, inputHeight, out var width, out var height);

        _frameCount = 0;

        var fps = _profile.Fps;

        string[] codecNames = ["h264_nvenc", "h264_amf", "libx264"];
        bool codecOpened = false;

        foreach (var codecName in codecNames)
        {
            if (!_hasDiscreteGPU && (codecName == "h264_nvenc" || codecName == "h264_amf"))
                continue;

            _codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
            if (_codec == null)
                continue;

            _codecContext = ffmpeg.avcodec_alloc_context3(_codec);
            _codecContext->width = width;
            _codecContext->height = height;
            _codecContext->time_base = new AVRational { num = 1, den = fps };
            _codecContext->framerate = new AVRational { num = fps, den = 1 };
            _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            // Остальные опорные кадры — по запросу зрителей.
            _codecContext->gop_size = fps * 5;
            _codecContext->max_b_frames = 0;

            if (codecName.Contains("nvenc"))
            {
                ffmpeg.av_opt_set(_codecContext->priv_data, "preset", "p4", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "tune", "hq", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "rc", "cqp", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "cq", "30", 0);
                ffmpeg.av_opt_set(_codecContext->priv_data, "forced-idr", "1", 0);
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
                ffmpeg.av_opt_set(_codecContext->priv_data, "forced-idr", "1", 0);
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

        _bands = width == inputWidth && height == inputHeight && height % 2 == 0
            ? CreateBands(width, height, Math.Clamp(Environment.ProcessorCount / 2, 1, 4))
            : [new ConvertBand(inputWidth, 0, inputHeight, width, height, _frame)];

        _initialized = true;
    }

    private static ConvertBand[] CreateBands(int width, int height, int count)
    {
        int rowPairs = height / 2;
        var bands = new ConvertBand[count];
        for (int i = 0; i < count; i++)
        {
            int top = rowPairs * i / count * 2;
            int bottom = rowPairs * (i + 1) / count * 2;
            bands[i] = new ConvertBand(width, top, bottom - top, width, bottom - top, _frame);
        }
        return bands;
    }

    private static void ConvertBandAt(int index) => _bands[index].Convert(_bgra, _width * 4);

    /// <summary>
    /// Полоса строк исходного кадра со своим контекстом sws: контекст хранит
    /// состояние между строками и не делится между потоками. Плоскости — в
    /// постоянных массивах: неявное приведение byte_ptrArray8 к byte*[] создаёт
    /// массив на каждый вызов.
    /// </summary>
    private sealed class ConvertBand
    {
        private readonly SwsContext* _context;
        private readonly int _top;
        private readonly int _srcHeight;
        private readonly byte*[] _src = new byte*[4];
        private readonly int[] _srcLinesize = new int[4];
        private readonly byte*[] _dst = new byte*[4];
        private readonly int[] _dstLinesize = new int[4];

        public ConvertBand(int srcWidth, int top, int srcHeight, int dstWidth, int dstHeight, AVFrame* frame)
        {
            _top = top;
            _srcHeight = srcHeight;
            _context = ffmpeg.sws_getContext(
                srcWidth, srcHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                dstWidth, dstHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
                ffmpeg.SWS_FAST_BILINEAR, null, null, null);

            for (uint i = 0; i < 4; i++)
            {
                int rows = i == 0 ? top : top / 2;
                _dst[i] = frame->data[i] == null ? null : frame->data[i] + rows * frame->linesize[i];
                _dstLinesize[i] = frame->linesize[i];
            }
        }

        public void Convert(byte* bgra, int stride)
        {
            _src[0] = bgra + (long)_top * stride;
            _srcLinesize[0] = stride;
            ffmpeg.sws_scale(_context, _src, _srcLinesize, 0, _srcHeight, _dst, _dstLinesize);
            _src[0] = null;
        }

        public void Free() => ffmpeg.sws_freeContext(_context);
    }

    private static void CalculateScaledDimensions(int inputWidth, int inputHeight, out int width, out int height)
    {
        width = inputWidth;
        height = inputHeight;

        if ((long)width * height > _profile.MaxPixels)
        {
            double scale = Math.Sqrt((double)_profile.MaxPixels / ((double)width * height));
            width = (int)(width * scale);
            height = (int)(height * scale);

            width = width / 2 * 2;
            height = height / 2 * 2;
        }
    }

    /// <summary>
    /// Пакет: признак опорного кадра, интервал в мс и данные H.264. Пустой —
    /// кодировщик кадр ещё не отдал. Массив общий и переписывается следующим вызовом.
    /// </summary>
    public static ReadOnlySpan<byte> Encode(byte[] bgraData, int deltaMs)
    {
        if (!_initialized)
            throw new InvalidOperationException("Encoder is not initialized. Call Initialize()");

        if (bgraData.Length != _width * _height * 4)
            throw new ArgumentException($"Invalid data size. Expected {_width * _height * 4} bytes");

        fixed (byte* pBgra = bgraData)
        {
            _bgra = pBgra;
            if (_bands.Length == 1)
                ConvertBandAt(0);
            else
                Parallel.For(0, _bands.Length, _convertBand);
            _bgra = null;
        }

        _frame->pts = _frameCount++;
        _frame->pict_type = _keyFrameRequested ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        _keyFrameRequested = false;

        ffmpeg.avcodec_send_frame(_codecContext, _frame);

        int ret = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            return default;

        if (ret < 0)
            throw new Exception($"Encoding error: {ret}");

        int length = 5 + _packet->size;
        if (_output.Length < length)
            _output = new byte[length * 2];

        bool isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
        _output[0] = isKeyFrame ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(_output.AsSpan(1, 4), deltaMs);
        new ReadOnlySpan<byte>(_packet->data, _packet->size).CopyTo(_output.AsSpan(5));

        ffmpeg.av_packet_unref(_packet);
        return _output.AsSpan(0, length);
    }

    public static void RequestKeyFrame() => _keyFrameRequested = true;

    public static void Resize(int width, int height)
    {
        Initialize(width, height);
    }

    public static void Cleanup()
    {
        if (!_initialized) return;

        foreach (var band in _bands)
            band.Free();
        _bands = [];

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
    private AVBufferRef* _hwDevice;
    private AVFrame* _cpuFrame;
    private AVFrame* _source;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private int _width;
    private int _height;
    private bool _initialized = false;

    private readonly byte*[] _srcData = new byte*[4];
    private readonly int[] _srcLinesize = new int[4];
    private readonly byte*[] _dstData = new byte*[4];
    private readonly int[] _dstLinesize = new int[4];

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

        AVBufferRef* hwDevice = null;
        if (ffmpeg.av_hwdevice_ctx_create(&hwDevice, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) >= 0)
        {
            _hwDevice = hwDevice;
            _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevice);
        }

        int ret = ffmpeg.avcodec_open2(_codecContext, _codec, null);
        if (ret < 0)
            throw new Exception($"Failed to open decoder: {ret}");

        _frame = ffmpeg.av_frame_alloc();
        _cpuFrame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        _initialized = true;
    }

    public void FlushBuffers()
    {
        if (!_initialized)
            throw new InvalidOperationException("Decoder is not initialized. Call Initialize()");

        ffmpeg.avcodec_flush_buffers(_codecContext);
    }

    /// <summary>
    /// true — кадр готов, его размер в <see cref="Width"/> и <see cref="Height"/>,
    /// забрать его — <see cref="ConvertTo"/>.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> h264Data)
    {
        if (!_initialized)
            throw new InvalidOperationException("Decoder is not initialized. Call Initialize()");

        int ret;
        // Пакет без буфера FFmpeg: send_packet сам копирует данные с нужным
        // запасом в конце, поэтому управляемый массив хватает закрепить на вызов.
        fixed (byte* src = h264Data)
        {
            _packet->data = src;
            _packet->size = h264Data.Length;
            ret = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            _packet->data = null;
            _packet->size = 0;
        }

        if (ret < 0)
            throw new Exception($"avcodec_send_packet error {ret}");

        ret = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            return false;

        if (ret < 0)
            throw new Exception($"Decoding error: {ret}");

        _source = _frame;
        if (_frame->hw_frames_ctx != null)
        {
            ffmpeg.av_frame_unref(_cpuFrame);
            if (ffmpeg.av_hwframe_transfer_data(_cpuFrame, _frame, 0) < 0)
                return false;
            _source = _cpuFrame;
        }

        _width = _source->width;
        _height = _source->height;
        _swsContext = ffmpeg.sws_getCachedContext(_swsContext,
            _width, _height, (AVPixelFormat)_source->format,
            _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);

        return true;
    }

    /// <summary>Последний декодированный кадр в BGRA, длина буфера — Width × Height × 4.</summary>
    public void ConvertTo(byte[] bgra)
    {
        for (uint i = 0; i < 4; i++)
        {
            _srcData[i] = _source->data[i];
            _srcLinesize[i] = _source->linesize[i];
        }

        fixed (byte* pBgra = bgra)
        {
            _dstData[0] = pBgra;
            _dstLinesize[0] = _width * 4;
            ffmpeg.sws_scale(_swsContext, _srcData, _srcLinesize, 0, _height, _dstData, _dstLinesize);
            _dstData[0] = null;
        }
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

        if (_cpuFrame != null)
        {
            fixed (AVFrame** ptr = &_cpuFrame)
                ffmpeg.av_frame_free(ptr);
            _cpuFrame = null;
        }
        _source = null;

        if (_codecContext != null)
        {
            fixed (AVCodecContext** ptr = &_codecContext)
                ffmpeg.avcodec_free_context(ptr);
            _codecContext = null;
        }

        if (_hwDevice != null)
        {
            fixed (AVBufferRef** ptr = &_hwDevice)
                ffmpeg.av_buffer_unref(ptr);
            _hwDevice = null;
        }

        _initialized = false;
    }
}