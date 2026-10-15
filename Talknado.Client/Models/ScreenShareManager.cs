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

    // Очередь выше MaxBacklog дольше Patience — кадры не уходят до следующего
    // опорного. Разовый всплеск от крупного опорного кадра не в счёт.
    private const int MaxBacklog = 256;
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(300);
    private long _overloadSince;

    // Не сбрасывается между показами: начало показа помечено Resync.
    private uint _frameNumber;
    private bool _startOfShare;
    private bool _skipping;

    // Кадры по разным каналам приходят не по порядку.
    private static readonly TimeSpan HoleTimeout = TimeSpan.FromSeconds(1);
    private const int MaxPendingFrames = 240;
    private readonly SortedDictionary<uint, (byte Flags, PooledBuffer Data)> _pending = [];
    private readonly Dictionary<uint, Assembly> _assemblies = [];
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
        if (backlog <= MaxBacklog)
            _overloadSince = 0;
        else if (_overloadSince == 0)
            _overloadSince = Stopwatch.GetTimestamp();

        if (_skipping)
        {
            if (!key || backlog > MaxBacklog / 2)
                return;
            _skipping = false;
        }
        else if (_overloadSince != 0 && Stopwatch.GetElapsedTime(_overloadSince) > Patience)
        {
            _skipping = true;
            return;
        }

        byte flags = key ? ScreenFrame.Key : (byte)0;
        if (_startOfShare)
            flags |= ScreenFrame.Resync;

        var encrypted = PooledBuffer.Rent(CryptoSessionManager.EncryptedLength(frameData.Length));
        PooledBuffer packet = default;
        try
        {
            int total = _cryptoSessionManager.Encrypt(frameData, encrypted.Array);
            byte count = ScreenFrame.PieceCount(total);
            uint number = _frameNumber++;

            packet = PooledBuffer.Rent(ScreenFrame.HeaderSize + (total + count - 1) / count);
            for (byte i = 0; i < count; i++)
            {
                var header = new ScreenFrame.Header(number, flags, i, count, total);
                ScreenFrame.Write(packet.Array, header);
                encrypted.Array.AsSpan(header.Offset, header.Length).CopyTo(packet.Array.AsSpan(ScreenFrame.HeaderSize));
                _networkUtils.SendScreenSharePacket(packet.Array.AsSpan(0, ScreenFrame.HeaderSize + header.Length));
            }
            _startOfShare = false;
        }
        finally
        {
            encrypted.Return();
            packet.Return();
        }
    }

    private void HandleReceiveScreenShare(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Assemble(_networkUtils.ReceiveScreenSharePacket(token), token);
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                return;
            }
            catch { /* ignore */ }
        }
    }

    private sealed class Assembly(PooledBuffer data, byte count)
    {
        public PooledBuffer Data { get; } = data;
        public byte Flags { get; set; }
        public int Missing { get; set; } = count;
        public ulong Received { get; set; }
    }

    // Поток приёма один: части, сборка и очередь кадров — только его.
    private void Assemble(PooledBuffer packet, CancellationToken token)
    {
        try
        {
            if (!ScreenFrame.TryRead(packet.Span, out var header) ||
                _hasNextFrame && (header.Flags & ScreenFrame.Resync) == 0 && (int)(header.Number - _nextFrame) < 0)
                return;

            if (!_assemblies.TryGetValue(header.Number, out var assembly))
            {
                assembly = new Assembly(PooledBuffer.Rent(header.Total), header.Count);
                _assemblies[header.Number] = assembly;
            }

            ulong bit = 1UL << header.Index;
            if (assembly.Data.Length != header.Total || (assembly.Received & bit) != 0)
                return;

            packet.Span[ScreenFrame.HeaderSize..].CopyTo(assembly.Data.Array.AsSpan(header.Offset));
            assembly.Received |= bit;
            assembly.Flags |= header.Flags;
            if (--assembly.Missing > 0)
                return;

            _assemblies.Remove(header.Number);
            Reorder(header.Number, assembly.Flags, assembly.Data, token);
        }
        finally
        {
            packet.Return();
        }
    }

    private void Reorder(uint number, byte flags, PooledBuffer data, CancellationToken token)
    {
        if ((flags & ScreenFrame.Resync) != 0 || !_hasNextFrame)
        {
            foreach (var stale in _pending.Keys.Where(k => k < number || k - number > MaxPendingFrames).ToList())
                Drop(stale);
            _nextFrame = number;
            _hasNextFrame = true;
        }
        else if ((int)(number - _nextFrame) < 0)
        {
            data.Return();
            return;
        }

        Drop(number);
        _pending[number] = (flags, data);

        Deliver(token);

        if (_pending.Count > 0)
        {
            if (_waitingSince == 0)
                _waitingSince = Stopwatch.GetTimestamp();

            if (_pending.Count > MaxPendingFrames || Stopwatch.GetElapsedTime(_waitingSince) > HoleTimeout)
            {
                // С опорного картинка восстанавливается чисто.
                uint resume = _pending.Keys.First();
                foreach (var (k, p) in _pending)
                {
                    if ((p.Flags & ScreenFrame.Key) != 0)
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

        foreach (var stale in _assemblies.Keys.Where(k => (int)(k - _nextFrame) < 0 || k - _nextFrame > MaxPendingFrames).ToList())
        {
            _assemblies.Remove(stale, out var assembly);
            assembly!.Data.Return();
        }
    }

    private void Drop(uint number)
    {
        if (_pending.Remove(number, out var frame))
            frame.Data.Return();
    }

    private void Deliver(CancellationToken token)
    {
        while (_pending.Remove(_nextFrame, out var frame))
        {
            _nextFrame++;
            _waitingSince = 0;

            var plain = PooledBuffer.Rent(frame.Data.Length);
            try
            {
                int length = _cryptoSessionManager.Decrypt(frame.Data.Span, plain.Array);
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
                frame.Data.Return();
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