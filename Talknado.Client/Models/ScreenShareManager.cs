using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Models.Helpers.Network;
using Talknado.Client.Models.Helpers.ScreenShare;

namespace Talknado.Client.Models;

public interface IScreenShareManager
{
    void StartSharing(int adapterIndex, int outputIndex, bool withAudio);
    void StopSharing();
    bool IsSharing { get; }
    Exception? ThreadException { get; set; }
}

public partial class ScreenShareManager : ObservableObject, IScreenShareManager, IDisposable
{
    private const int TARGET_FPS = 30;

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

    public void StartSharing(int adapterIndex, int outputIndex, bool withAudio)
    {
        if (IsSharing) return;

        IsSharing = true;

        _sendCancellationTokenSource = new CancellationTokenSource();

        ScreenGrabber.SelectMonitor(adapterIndex, outputIndex);

        if (withAudio)
        {
            LoopbackAudioCapture.InitializeAudio();
        }

        _screenShareSendThread = new(() => ShareScreenLoop(outputIndex, _sendCancellationTokenSource.Token))
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

    private void ShareScreenLoop(int outputIndex, CancellationToken token)
    {
        const int INTERVAL = 1000 / TARGET_FPS;
        try
        {

            _ = ScreenGrabber.CaptureFrame(out int w, out int h);
            H264Encoder.Initialize(w, h);

            long lastCaptureTimestamp = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                long now = Stopwatch.GetTimestamp();
                int deltaMs = (int)((now - lastCaptureTimestamp) * 1000 / Stopwatch.Frequency);
                lastCaptureTimestamp = now;

                var screenFrame = ScreenGrabber.CaptureFrame(out _, out _);
                var encodedFrame = H264Encoder.Encode(screenFrame, deltaMs);

                if (encodedFrame != null)
                {
                    SendFramePacket(encodedFrame);

                    if (_screenSharePlayer.IsWindowVisible)
                    {
                        ProcessFrame(encodedFrame, token);
                    }
                    _screenSharePlayer.SaveLastKeyFrame(encodedFrame);

                }

                int delay = INTERVAL - (int)sw.ElapsedMilliseconds;
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

    private void SendFramePacket(byte[] frameData)
    {
        var encryptedData = _cryptoSessionManager.EncryptMessage(frameData);

        _networkUtils.SendScreenSharePacketAsync(encryptedData).GetAwaiter().GetResult();
    }

    private void HandleReceiveScreenShare(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var encryptedData = _networkUtils.ReceiveScreenSharePacketAsync(token).GetAwaiter().GetResult();
                var h264Data = _cryptoSessionManager.DecryptMessage(encryptedData);

                if (_screenSharePlayer.IsWindowVisible)
                {
                    ProcessFrame(h264Data, token);
                }
                _screenSharePlayer.SaveLastKeyFrame(h264Data);
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                return;
            }
            catch { /* ignore */ }
        }
    }

    private void ProcessFrame(byte[] h264Data, CancellationToken token)
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