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
        var encrypted = PooledBuffer.Rent(CryptoSessionManager.EncryptedLength(frameData.Length));
        try
        {
            int length = _cryptoSessionManager.Encrypt(frameData, encrypted.Array);
            _networkUtils.SendScreenSharePacket(encrypted.Array.AsSpan(0, length));
        }
        finally
        {
            encrypted.Return();
        }
    }

    private void HandleReceiveScreenShare(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var encrypted = _networkUtils.ReceiveScreenSharePacket(token);
                var plain = PooledBuffer.Rent(encrypted.Length);
                try
                {
                    int length = _cryptoSessionManager.Decrypt(encrypted.Span, plain.Array);
                    var h264Data = plain.Array.AsSpan(0, length);

                    if (_screenSharePlayer.IsWindowVisible)
                    {
                        ProcessFrame(h264Data, token);
                    }
                    _screenSharePlayer.SaveLastKeyFrame(h264Data);
                }
                finally
                {
                    plain.Return();
                    encrypted.Return();
                }
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                return;
            }
            catch { /* ignore */ }
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