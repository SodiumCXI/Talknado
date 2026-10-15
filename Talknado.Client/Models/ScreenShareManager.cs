using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Models.Helpers.Network;
using Talknado.Client.Models.Helpers.ScreenShare;

namespace Talknado.Client.Models;

public interface IScreenShareManager
{
    void StartSharing(int adapterIndex, int outputIndex, bool withAudio, byte qualityCeiling);
    void StopSharing();
    bool IsSharing { get; }
    Exception? ThreadException { get; set; }
}

public partial class ScreenShareManager : ObservableObject, IScreenShareManager, IDisposable
{
    private ShareQualityProfile _profile = ShareQuality.Resolve(0);

    // Пустой кадр: экран не менялся, но зритель должен видеть, что поток жив.
    private static readonly byte[] KeepAlive = new byte[5];

    private readonly INetworkUtils _networkUtils;
    private readonly ICryptoSessionManager _cryptoSessionManager;
    private readonly IScreenSharePlayer _screenSharePlayer;

    private readonly CancellationTokenSource _receiveCancellationTokenSource;
    private CancellationTokenSource? _sendCancellationTokenSource;

    private readonly Thread _screenShareReceiveThread;
    private Thread? _screenShareSendThread;

    // Очередь к серверу, после которой кадры перестают уходить до следующего
    // опорного: на медленном канале она иначе растёт без предела, и картинка
    // у зрителей отстаёт на десятки секунд. 256 пакетов — около 350 КБ.
    private const int MaxBacklog = 256;

    // Номер не сбрасывается между показами: начало показа помечено Resync,
    // по нему зритель и отличает новый показ от старого.
    private uint _frameNumber;
    private bool _startOfShare;
    private bool _skipping;

    // Кадры идут по нескольким каналам и приходят не по порядку. Недостающий
    // ждём не дольше HoleTimeout: надёжный канал досылает потерю за несколько
    // пингов, а дольше ждать можно лишь кадр, которого уже не будет.
    private static readonly TimeSpan HoleTimeout = TimeSpan.FromSeconds(1);
    private const int MaxPendingFrames = 240;
    private readonly SortedDictionary<uint, PooledBuffer> _pending = [];
    private uint _nextFrame;
    private bool _hasNextFrame;
    private long _waitingSince;

    [ObservableProperty]
    private bool _isSharing = false;
    public Exception? ThreadException { get; set; }

    public ScreenShareManager(
        INetworkUtils networkUtils,
        ICryptoSessionManager cryptoSessionManager,
        IScreenSharePlayer screenSharePlayer)
    {
        _networkUtils = networkUtils;
        _cryptoSessionManager = cryptoSessionManager;
        _screenSharePlayer = screenSharePlayer;

        _receiveCancellationTokenSource = new CancellationTokenSource();
        _screenShareReceiveThread = new(() => HandleReceiveScreenShare(_receiveCancellationTokenSource.Token))
        {
            IsBackground = true
        };
        _screenShareReceiveThread.Start();
    }

    public void StartSharing(int adapterIndex, int outputIndex, bool withAudio, byte qualityCeiling)
    {
        if (IsSharing) return;

        _profile = ShareQuality.Resolve(qualityCeiling);
        _startOfShare = true;
        _skipping = false;

        IsSharing = true;

        _sendCancellationTokenSource = new CancellationTokenSource();

        ScreenGrabber.SelectMonitor(adapterIndex, outputIndex);

        if (withAudio)
        {
            LoopbackAudioCapture.InitializeAudio();
        }

        _screenShareSendThread = new(() => ShareScreenLoop(_sendCancellationTokenSource.Token))
        {
            IsBackground = true
        };
        _screenShareSendThread.Start();
    }

    public void StopSharing()
    {
        IsSharing = false;
        _sendCancellationTokenSource?.Cancel();
        _screenShareSendThread?.Join();
        _sendCancellationTokenSource?.Dispose();
        _sendCancellationTokenSource = null;

        LoopbackAudioCapture.Stop();
    }

    private void ShareScreenLoop(CancellationToken token)
    {
        int interval = 1000 / _profile.Fps;
        try
        {

            var firstFrame = ScreenGrabber.CaptureFrame(out int w, out int h);
            if (firstFrame == null)
            {
                while (firstFrame == null && !token.IsCancellationRequested)
                {
                    Thread.Sleep(10);
                    firstFrame = ScreenGrabber.CaptureFrame(out w, out h);
                }
                if (firstFrame == null) return;
            }
            H264Encoder.Initialize(w, h, _profile);

            long lastCaptureTimestamp = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                long now = Stopwatch.GetTimestamp();
                int deltaMs = (int)((now - lastCaptureTimestamp) * 1000 / Stopwatch.Frequency);
                lastCaptureTimestamp = now;

                var screenFrame = ScreenGrabber.CaptureFrame(out _, out _);
                ReadOnlySpan<byte> encodedFrame = screenFrame != null
                    ? H264Encoder.Encode(screenFrame, deltaMs)
                    : KeepAlive;

                if (!encodedFrame.IsEmpty)
                {
                    SendFramePacket(encodedFrame);
                    if (_screenSharePlayer.IsWindowVisible)
                        ProcessFrame(encodedFrame, token);
                    _screenSharePlayer.SaveLastKeyFrame(encodedFrame);
                }

                int delay = interval - (int)sw.ElapsedMilliseconds;
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }
        }
        catch (Exception ex)
        {
            ThreadException = ex;
        }
    }

    private void SendFramePacket(ReadOnlySpan<byte> frameData)
    {
        bool key = frameData[0] == 1;

        int backlog = _networkUtils.ScreenShareBacklog;
        if (_skipping)
        {
            if (!key || backlog > MaxBacklog / 2)
                return;
            _skipping = false;
        }
        else if (backlog > MaxBacklog)
        {
            _skipping = true;
            return;
        }

        byte flags = key ? ScreenFrame.Key : (byte)0;
        if (_startOfShare)
            flags |= ScreenFrame.Resync;

        var packet = PooledBuffer.Rent(ScreenFrame.HeaderSize + CryptoSessionManager.EncryptedLength(frameData.Length));
        try
        {
            ScreenFrame.Write(packet.Array, _frameNumber++, flags);
            int length = _cryptoSessionManager.Encrypt(frameData, packet.Array.AsSpan(ScreenFrame.HeaderSize));
            _networkUtils.SendScreenSharePacket(packet.Array.AsSpan(0, ScreenFrame.HeaderSize + length));
            _startOfShare = false;
        }
        finally
        {
            packet.Return();
        }
    }

    private void HandleReceiveScreenShare(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Reorder(_networkUtils.ReceiveScreenSharePacket(token), token);
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                return;
            }
            catch { /* ignore */ }
        }
    }

    // Поток приёма один, очередь кадров — только его.
    private void Reorder(PooledBuffer packet, CancellationToken token)
    {
        if (!ScreenFrame.TryRead(packet.Span, out var number, out var flags))
        {
            packet.Return();
            return;
        }

        if ((flags & ScreenFrame.Resync) != 0 || !_hasNextFrame)
        {
            // Старое и кадры прошлого показа по порядку уже не придут.
            foreach (var stale in _pending.Keys.Where(k => k < number || k - number > MaxPendingFrames).ToList())
                Drop(stale);
            _nextFrame = number;
            _hasNextFrame = true;
        }
        else if ((int)(number - _nextFrame) < 0)
        {
            packet.Return();
            return;
        }

        Drop(number);
        _pending[number] = packet;

        Deliver(token);

        if (_pending.Count == 0)
            return;

        if (_waitingSince == 0)
            _waitingSince = Stopwatch.GetTimestamp();

        if (_pending.Count > MaxPendingFrames || Stopwatch.GetElapsedTime(_waitingSince) > HoleTimeout)
        {
            // Пропавший кадр не дождаться. С опорного картинка восстановится
            // чисто, поэтому продолжаем с него, если он уже здесь.
            uint resume = _pending.Keys.First();
            foreach (var (k, p) in _pending)
            {
                if (ScreenFrame.TryRead(p.Span, out _, out var f) && (f & ScreenFrame.Key) != 0)
                {
                    resume = k;
                    break;
                }
            }

            foreach (var skipped in _pending.Keys.Where(k => k < resume).ToList())
                Drop(skipped);
            _nextFrame = resume;
            Deliver(token);
            if (_pending.Count > 0)
                _waitingSince = Stopwatch.GetTimestamp();
        }
    }

    private void Drop(uint number)
    {
        if (_pending.Remove(number, out var packet))
            packet.Return();
    }

    private void Deliver(CancellationToken token)
    {
        while (_pending.Remove(_nextFrame, out var packet))
        {
            _nextFrame++;
            _waitingSince = 0;

            var plain = PooledBuffer.Rent(packet.Length);
            try
            {
                int length = _cryptoSessionManager.Decrypt(packet.Span[ScreenFrame.HeaderSize..], plain.Array);
                var h264Data = plain.Array.AsSpan(0, length);

                if (_screenSharePlayer.IsWindowVisible)
                {
                    ProcessFrame(h264Data, token);
                }
                _screenSharePlayer.SaveLastKeyFrame(h264Data);
            }
            catch (Exception ex) when (!NetworkExceptionHelper.IsNetworkException(ex) && ex is not OperationCanceledException) { /* повреждённый кадр пропускается */ }
            finally
            {
                plain.Return();
                packet.Return();
            }
        }
    }

    private void ProcessFrame(ReadOnlySpan<byte> h264Data, CancellationToken token)
    {
        _screenSharePlayer.UpdateFrame(h264Data, token);
    }

    public void Dispose()
    {
        _receiveCancellationTokenSource.Cancel();
        _screenShareReceiveThread.Join();
        _receiveCancellationTokenSource.Dispose();

        StopSharing();
        H264Encoder.Cleanup();

        GC.SuppressFinalize(this);
    }
}