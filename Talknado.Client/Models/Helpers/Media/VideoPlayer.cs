using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Talknado.Client.Models.Helpers.Media;

/// <summary>
/// Проигрыватель видео на FFmpeg. Данные FFmpeg берёт не с диска, а через
/// <see cref="IMediaReader"/>: так видео играет, пока файл ещё догружается.
/// Разбор и декодирование идут в своём потоке, декодирует видеокарта, если
/// умеет этот кодек. Кадры складываются в короткую очередь уже в BGRA размера
/// окна, а поток UI забирает их по часам.
/// Часы — позиция звука в устройстве, без звука — Stopwatch.
/// </summary>
public sealed unsafe class VideoPlayer : IDisposable
{
    private const int QueueCapacity = 4;
    private const int IoBufferSize = 64 * 1024;
    private static readonly TimeSpan StallBelow = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan ResumeAbove = TimeSpan.FromMilliseconds(300);
    private const int SampleRate = 48000;
    private static readonly TimeSpan AudioAhead = TimeSpan.FromMilliseconds(400);

    /// <summary>На сколько отрезков делится дорожка для отметки загруженного.</summary>
    public const int BufferedBins = 200;

    private readonly IMediaReader _reader;
    private readonly CancellationTokenSource _cts;
    private readonly avio_alloc_context_read_packet _readPacket;
    private readonly avio_alloc_context_seek _seekPacket;
    private readonly AVIOContext* _io;
    private long _readPosition;
    private bool _stalled;

    private readonly AVBufferRef* _hwDevice;
    private readonly AVFormatContext* _format;
    private readonly AVCodecContext* _videoCodec;
    private readonly AVCodecContext* _audioCodec;
    private readonly int _videoStream;
    private readonly int _audioStream = -1;
    private readonly double _videoTimeBase;
    private readonly double _audioTimeBase;
    private readonly long _startPts;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private long _outputSize;
    private readonly long[] _binStarts = new long[BufferedBins + 1];

    // Неявное приведение byte_ptrArray8 к byte*[] создаёт массив на каждый
    // кадр, поэтому плоскости для sws_scale лежат в постоянных массивах.
    private readonly byte*[] _srcData = new byte*[4];
    private readonly int[] _srcLines = new int[4];
    private readonly byte*[] _dstData = new byte*[4];
    private readonly int[] _dstLines = new int[4];

    private SwsContext* _sws;
    private SwrContext* _swr;

    private readonly BufferedWaveProvider? _audioBuffer;
    private readonly VolumeSampleProvider? _volume;
    private readonly WasapiOut? _output;
    private int _bytesPerSecond = SampleRate * 2 * 4;
    private byte[] _audioScratch = [];

    private readonly ConcurrentQueue<VideoFrame> _frames = new();
    private readonly ConcurrentBag<byte[]> _pool = [];
    private readonly Thread _decodeThread;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _disposed;
    private volatile bool _endOfStream;

    private readonly object _seekLock = new();
    private double? _pendingSeek;
    private double _seekTarget;
    private double _clockBase;
    private long _positionAtBase;
    private readonly Stopwatch _wallClock = new();
    private bool _isPaused = true;
    private double _pausedClock;

    // Поколение перемотки: кадр или звук, декодированные до перемотки,
    // в очередь уже не попадают.
    private volatile int _generation;
    private volatile bool _showNextFrame = true;

    public TimeSpan Duration { get; }
    public bool IsPaused => _isPaused;
    public bool IsEnded => _endOfStream && _frames.IsEmpty && (_audioBuffer == null || _audioBuffer.BufferedDuration == TimeSpan.Zero);
    public bool IsBuffering => _stalled || _reader.IsWaiting && _frames.IsEmpty;

    /// <summary>Буфер может быть длиннее кадра: он переживает смену размера окна.</summary>
    public readonly record struct VideoFrame(double Time, byte[] Pixels, int Width, int Height);

    /// <summary>
    /// Открытие читает начало файла и может ждать данных из сети, поэтому
    /// вызывается не из потока UI. <paramref name="openToken"/> прерывает ожидание.
    /// Проигрыватель становится владельцем <paramref name="reader"/>.
    /// Кадр вписывается в <paramref name="maxWidth"/> × <paramref name="maxHeight"/>,
    /// дальше размер меняет <see cref="SetOutputSize"/>.
    /// </summary>
    public VideoPlayer(IMediaReader reader, int maxWidth, int maxHeight, string? outputDeviceId, CancellationToken openToken)
    {
        _reader = reader;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(openToken);
        _readPacket = ReadPacket;
        _seekPacket = SeekPacket;

        try
        {
            var buffer = (byte*)ffmpeg.av_malloc(IoBufferSize);
            _io = ffmpeg.avio_alloc_context(buffer, IoBufferSize, 0, null, _readPacket, null, _seekPacket);

            var format = ffmpeg.avformat_alloc_context();
            format->pb = _io;
            format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            if (ffmpeg.avformat_open_input(&format, null, null, null) < 0)
                throw new InvalidDataException();
            _format = format;

            if (ffmpeg.avformat_find_stream_info(_format, null) < 0)
                throw new InvalidDataException();

            AVCodec* videoCodec = null;
            _videoStream = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &videoCodec, 0);
            if (_videoStream < 0)
                throw new InvalidDataException();

            AVBufferRef* hwDevice = null;
            if (ffmpeg.av_hwdevice_ctx_create(&hwDevice, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) >= 0)
                _hwDevice = hwDevice;

            _videoCodec = OpenCodec(videoCodec, _format->streams[_videoStream], _hwDevice);
            _videoTimeBase = ffmpeg.av_q2d(_format->streams[_videoStream]->time_base);

            var start = _format->streams[_videoStream]->start_time;
            _startPts = start == ffmpeg.AV_NOPTS_VALUE ? 0 : start;

            AVCodec* audioCodec = null;
            int audioStream = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, _videoStream, &audioCodec, 0);
            if (audioStream >= 0 && audioCodec != null)
            {
                try
                {
                    _audioCodec = OpenCodec(audioCodec, _format->streams[audioStream], null);
                    _audioStream = audioStream;
                    _audioTimeBase = ffmpeg.av_q2d(_format->streams[audioStream]->time_base);

                    _audioBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2))
                    {
                        BufferDuration = TimeSpan.FromSeconds(2),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };
                    _volume = new VolumeSampleProvider(_audioBuffer.ToSampleProvider());

                    using var enumerator = new MMDeviceEnumerator();
                    var device = string.IsNullOrEmpty(outputDeviceId)
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : enumerator.GetDevice(outputDeviceId);
                    _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
                    _output.Init(_volume);
                    _bytesPerSecond = _output.OutputWaveFormat.AverageBytesPerSecond;
                }
                catch
                {
                    _audioStream = -1;
                    _output?.Dispose();
                    _output = null;
                    _audioBuffer = null;
                }
            }

            _sourceWidth = _videoCodec->width;
            _sourceHeight = _videoCodec->height;
            SetOutputSize(maxWidth, maxHeight);

            Duration = _format->duration > 0 ? TimeSpan.FromMilliseconds(_format->duration / 1000.0) : TimeSpan.Zero;
            MapBins();
        }
        catch
        {
            Dispose();
            throw;
        }

        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "VideoPlayer" };
        _decodeThread.Start();
    }

    // Устройство видеокарты кодек берёт, только если умеет с ним работать;
    // иначе, как и при сбое аппаратного декодирования, кадры идут программно.
    private static AVCodecContext* OpenCodec(AVCodec* codec, AVStream* stream, AVBufferRef* hwDevice)
    {
        var context = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(context, stream->codecpar);
        context->thread_count = 0;
        if (hwDevice != null)
            context->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevice);
        if (ffmpeg.avcodec_open2(context, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new InvalidDataException();
        }
        return context;
    }

    /// <summary>
    /// Начало каждого отрезка дорожки в байтах файла — по оглавлению потока,
    /// если оно покрывает всё видео, иначе пропорционально размеру.
    /// </summary>
    private void MapBins()
    {
        var stream = _format->streams[_videoStream];
        int count = ffmpeg.avformat_index_get_entries_count(stream);
        double seconds = Duration.TotalSeconds;
        bool indexed = seconds > 0 && count > 0
            && (ffmpeg.avformat_index_get_entry(stream, count - 1)->timestamp - _startPts) * _videoTimeBase >= seconds * 0.9;

        for (int i = 0; i < BufferedBins; i++)
        {
            long position = _reader.Size * i / BufferedBins;
            if (indexed)
            {
                long ts = (long)(seconds * i / BufferedBins / _videoTimeBase) + _startPts;
                var entry = ffmpeg.avformat_index_get_entry_from_timestamp(stream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD | ffmpeg.AVSEEK_FLAG_ANY);
                if (entry != null)
                    position = entry->pos;
            }
            _binStarts[i] = i > 0 ? Math.Max(position, _binStarts[i - 1]) : position;
        }
        _binStarts[BufferedBins] = _reader.Size;
    }

    /// <summary>Какие отрезки дорожки уже загружены. Вызывается из потока UI.</summary>
    public void GetBuffered(bool[] bins)
    {
        for (int i = 0; i < BufferedBins; i++)
            bins[i] = _reader.Has(_binStarts[i], _binStarts[i + 1]);
    }

    /// <summary>Вписывает кадр в область, не крупнее исходного. Новые кадры идут уже такими.</summary>
    public void SetOutputSize(int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0)
            return;

        double scale = Math.Min(1, Math.Min((double)maxWidth / _sourceWidth, (double)maxHeight / _sourceHeight));
        int width = Math.Max(2, (int)(_sourceWidth * scale) & ~1);
        int height = Math.Max(2, (int)(_sourceHeight * scale) & ~1);
        Volatile.Write(ref _outputSize, (long)width << 32 | (uint)height);
    }

    public float Volume
    {
        get => _volume?.Volume ?? 0;
        set { if (_volume != null) _volume.Volume = value; }
    }

    private int ReadPacket(void* opaque, byte* buffer, int size)
    {
        try
        {
            int read = _reader.Read(_readPosition, new Span<byte>(buffer, size), _cts.Token);
            if (read <= 0)
                return ffmpeg.AVERROR_EOF;

            _readPosition += read;
            return read;
        }
        catch (OperationCanceledException)
        {
            return ffmpeg.AVERROR_EXIT;
        }
        catch
        {
            return ffmpeg.AVERROR(5);
        }
    }

    private long SeekPacket(void* opaque, long offset, int whence)
    {
        if ((whence & ffmpeg.AVSEEK_SIZE) != 0)
            return _reader.Size;

        switch (whence & ~ffmpeg.AVSEEK_FORCE)
        {
            case 0: _readPosition = offset; break;
            case 1: _readPosition += offset; break;
            case 2: _readPosition = _reader.Size + offset; break;
            default: return -1;
        }

        return _readPosition;
    }

    public void Play()
    {
        lock (_seekLock)
        {
            if (IsEnded)
                SeekLocked(0);

            _stalled = false;
            _isPaused = false;
            _output?.Play();
            _wallClock.Start();
        }
        _wake.Set();
    }

    // Часы встают в момент нажатия. Устройство ещё доигрывает то, что успело
    // взять, и по его позиции кадры ползли бы вперёд и после паузы.
    public void Pause()
    {
        lock (_seekLock)
        {
            _pausedClock = ClockLocked();
            _isPaused = true;
            _output?.Pause();
            _wallClock.Stop();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_seekLock)
            SeekLocked(Math.Clamp(position.TotalSeconds, 0, Math.Max(0, Duration.TotalSeconds)));
        _wake.Set();
    }

    private void SeekLocked(double seconds)
    {
        _generation++;
        _showNextFrame = true;
        _pendingSeek = seconds;
        _seekTarget = seconds;
        _pausedClock = seconds;
        _endOfStream = false;
        _audioBuffer?.ClearBuffer();
        _clockBase = seconds;
        _positionAtBase = _output != null ? _output.GetPosition() : 0;
        _wallClock.Reset();
        if (!_isPaused)
            _wallClock.Start();

        while (_frames.TryDequeue(out var frame))
            _pool.Add(frame.Pixels);
    }

    public double Clock
    {
        get
        {
            lock (_seekLock)
                return _isPaused ? _pausedClock : ClockLocked();
        }
    }

    private double ClockLocked()
    {
        if (_output != null)
        {
            var played = (_output.GetPosition() - _positionAtBase) / (double)_bytesPerSecond;
            return _clockBase + Math.Max(0, played);
        }

        return _clockBase + _wallClock.Elapsed.TotalSeconds;
    }

    /// <summary>Кадр, которому пора на экран, или null. Вызывается из потока UI.</summary>
    public VideoFrame? TakeDueFrame()
    {
        UpdateStall();
        double clock = Clock;
        VideoFrame? due = null;

        while (_frames.TryPeek(out var head) && (head.Time <= clock || due == null && (_showNextFrame || StarvedForAudio())))
        {
            _showNextFrame = false;
            _frames.TryDequeue(out var frame);
            if (due is { } previous)
                _pool.Add(previous.Pixels);
            due = frame;
        }

        _wake.Set();
        return due;
    }

    // Без этого очередь кадров, заполненная далеко вперёд, держала бы разбор,
    // а звук, лежащий в файле после этих кадров, так и не пришёл бы: часы встали бы.
    private bool StarvedForAudio() =>
        _audioBuffer != null && !_isPaused && _frames.Count >= QueueCapacity
        && _audioBuffer.BufferedDuration < TimeSpan.FromMilliseconds(50);

    public void ReturnFrame(VideoFrame frame) => _pool.Add(frame.Pixels);

    /// <summary>
    /// Пока данные идут из сети, звук может кончиться раньше, чем придут новые.
    /// Тогда часы останавливаются вместе с выводом и ждут запаса, иначе после
    /// ожидания кадры догоняли бы ушедшие вперёд часы рывком.
    /// </summary>
    private void UpdateStall()
    {
        lock (_seekLock)
        {
            if (_isPaused)
                return;

            if (_output != null && _audioBuffer != null)
            {
                var buffered = _audioBuffer.BufferedDuration;

                if (!_stalled && buffered < StallBelow && !_endOfStream && _reader.IsWaiting)
                {
                    _output.Pause();
                    _stalled = true;
                }
                else if (_stalled && (buffered > ResumeAbove || _endOfStream || _frames.Count >= QueueCapacity && !_reader.IsWaiting))
                {
                    _output.Play();
                    _stalled = false;
                }
            }
            else if (!_stalled && _frames.IsEmpty && !_endOfStream && _reader.IsWaiting)
            {
                _wallClock.Stop();
                _stalled = true;
            }
            else if (_stalled && (_frames.Count >= 2 || _endOfStream))
            {
                _wallClock.Start();
                _stalled = false;
            }
        }
    }

    private void DecodeLoop()
    {
        var packet = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        var cpuFrame = ffmpeg.av_frame_alloc();

        try
        {
            while (!_disposed)
            {
                double? seek;
                lock (_seekLock)
                {
                    seek = _pendingSeek;
                    _pendingSeek = null;
                }

                if (seek is { } seconds)
                {
                    long ts = (long)(seconds / _videoTimeBase) + _startPts;
                    ffmpeg.av_seek_frame(_format, _videoStream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    ffmpeg.avcodec_flush_buffers(_videoCodec);
                    if (_audioCodec != null)
                        ffmpeg.avcodec_flush_buffers(_audioCodec);
                }

                if (_endOfStream || _frames.Count >= QueueCapacity
                    || _audioBuffer != null && _audioBuffer.BufferedDuration > AudioAhead && _frames.Count > 0)
                {
                    _wake.Wait(20);
                    _wake.Reset();
                    continue;
                }

                if (ffmpeg.av_read_frame(_format, packet) < 0)
                {
                    _endOfStream = true;
                    continue;
                }

                if (packet->stream_index == _videoStream)
                    DecodeVideo(packet, frame, cpuFrame);
                else if (packet->stream_index == _audioStream)
                    DecodeAudio(packet, frame);

                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&cpuFrame);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
        }
    }

    private void DecodeVideo(AVPacket* packet, AVFrame* frame, AVFrame* cpuFrame)
    {
        if (ffmpeg.avcodec_send_packet(_videoCodec, packet) < 0)
            return;

        int generation = _generation;

        while (ffmpeg.avcodec_receive_frame(_videoCodec, frame) >= 0)
        {
            long pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
            double time = (pts - _startPts) * _videoTimeBase;
            if (time + 0.001 < _seekTarget)
                continue;

            var source = frame;
            if (frame->hw_frames_ctx != null)
            {
                if (ffmpeg.av_hwframe_transfer_data(cpuFrame, frame, 0) < 0)
                    continue;
                source = cpuFrame;
            }

            long size = Volatile.Read(ref _outputSize);
            int width = (int)(size >> 32), height = (int)size, stride = width * 4;
            var pixels = TakeBuffer(stride * height);

            _sws = ffmpeg.sws_getCachedContext(_sws, source->width, source->height, (AVPixelFormat)source->format,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);

            for (uint i = 0; i < 4; i++)
            {
                _srcData[i] = source->data[i];
                _srcLines[i] = source->linesize[i];
            }

            fixed (byte* destination = pixels)
            {
                _dstData[0] = destination;
                _dstLines[0] = stride;
                ffmpeg.sws_scale(_sws, _srcData, _srcLines, 0, source->height, _dstData, _dstLines);
                _dstData[0] = null;
            }

            if (source == cpuFrame)
                ffmpeg.av_frame_unref(cpuFrame);

            if (generation == _generation)
                _frames.Enqueue(new VideoFrame(time, pixels, width, height));
            else
                _pool.Add(pixels);
        }
    }

    // Буферы меньше нужного после увеличения окна выбрасываются, большие идут в дело.
    private byte[] TakeBuffer(int length)
    {
        while (_pool.TryTake(out var buffer))
        {
            if (buffer.Length >= length)
                return buffer;
        }
        return new byte[length];
    }

    private void DecodeAudio(AVPacket* packet, AVFrame* frame)
    {
        if (_audioBuffer == null || ffmpeg.avcodec_send_packet(_audioCodec, packet) < 0)
            return;

        int generation = _generation;

        while (ffmpeg.avcodec_receive_frame(_audioCodec, frame) >= 0)
        {
            double time = (frame->best_effort_timestamp - _startPts) * _audioTimeBase;
            if (frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE && time + 0.001 < _seekTarget)
                continue;

            if (_swr == null)
            {
                SwrContext* swr = null;
                AVChannelLayout stereo;
                ffmpeg.av_channel_layout_default(&stereo, 2);
                ffmpeg.swr_alloc_set_opts2(&swr, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate,
                    &_audioCodec->ch_layout, _audioCodec->sample_fmt, _audioCodec->sample_rate, 0, null);
                ffmpeg.swr_init(swr);
                _swr = swr;
            }

            int capacity = (int)ffmpeg.av_rescale_rnd(frame->nb_samples, SampleRate, frame->sample_rate, AVRounding.AV_ROUND_UP) + 256;
            if (_audioScratch.Length < capacity * 2 * 4)
                _audioScratch = new byte[capacity * 2 * 4];
            int converted;

            fixed (byte* destination = _audioScratch)
            {
                byte* outPtr = destination;
                converted = ffmpeg.swr_convert(_swr, &outPtr, capacity, frame->extended_data, frame->nb_samples);
            }

            if (converted > 0 && generation == _generation)
                _audioBuffer.AddSamples(_audioScratch, 0, converted * 2 * 4);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _wake.Set();

        if (_decodeThread != null && _decodeThread.IsAlive)
            _decodeThread.Join();

        _output?.Stop();
        _output?.Dispose();

        if (_sws != null) ffmpeg.sws_freeContext(_sws);
        if (_swr != null)
        {
            var swr = _swr;
            ffmpeg.swr_free(&swr);
        }

        var videoCodec = _videoCodec;
        if (videoCodec != null) ffmpeg.avcodec_free_context(&videoCodec);
        var audioCodec = _audioCodec;
        if (audioCodec != null) ffmpeg.avcodec_free_context(&audioCodec);
        var format = _format;
        if (format != null) ffmpeg.avformat_close_input(&format);
        var hwDevice = _hwDevice;
        if (hwDevice != null) ffmpeg.av_buffer_unref(&hwDevice);

        // Свой ввод FFmpeg при закрытии не освобождает.
        var io = _io;
        if (io != null)
        {
            ffmpeg.av_freep(&io->buffer);
            ffmpeg.avio_context_free(&io);
        }

        _reader.Dispose();
        _cts.Dispose();
        _wake.Dispose();
    }
}
