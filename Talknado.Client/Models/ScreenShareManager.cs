using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using Talknado.Client.Models.Client.Helpers;
using Talknado.Client.Models.Helpers;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Models.Helpers.ScreenShare;

namespace Talknado.Client.Models;

public interface IScreenShareManager
{
    void StartSharing(bool withAudio);
    void StopSharing();
    bool IsSharing { get; }
}

public partial class ScreenShareManager : ObservableObject, IScreenShareManager, IDisposable
{
    private const int TARGET_FPS = 30;

    private readonly INetworkUtils _networkUtils;
    private readonly ICryptoSessionManager _cryptoSessionManager;
    private readonly IUsersAudioPlayer _usersAudioPlayer;
    private readonly IScreenSharePlayer _screenSharePlayer;

    private readonly CancellationTokenSource _receiveCancellationTokenSource;
    private CancellationTokenSource? _sendCancellationTokenSource;

    private readonly Thread _screenShareReceiveThread;
    private Thread? _screenShareSendThread;

    [ObservableProperty]
    private bool _isSharing = false;

    public ScreenShareManager(
        INetworkUtils networkUtils,
        ICryptoSessionManager cryptoSessionManager,
        IUsersAudioPlayer usersAudioPlayer,
        IScreenSharePlayer screenSharePlayer)
    {
        _networkUtils = networkUtils;
        _cryptoSessionManager = cryptoSessionManager;
        _usersAudioPlayer = usersAudioPlayer;
        _screenSharePlayer = screenSharePlayer;

        _receiveCancellationTokenSource = new CancellationTokenSource();
        _screenShareReceiveThread = new(() => HandleReceiveScreenShare(_receiveCancellationTokenSource.Token))
        {
            IsBackground = true
        };
        _screenShareReceiveThread.Start();
    }

    public void StartSharing(bool withAudio)
    {
        if (IsSharing) return;

        IsSharing = true;

        _sendCancellationTokenSource = new CancellationTokenSource();

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
        const int INTERVAL = 1000 / TARGET_FPS;

        _ = ScreenGrabber.CaptureFrame(out int w, out int h);
        H264Encoder.Initialize(w, h);

        while (!token.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            var screenFrame = ScreenGrabber.CaptureFrame(out _, out _);
            CursorRenderer.OverlayCursorOnByteBuffer(screenFrame, w, h);
            var encodedFrame = H264Encoder.Encode(screenFrame);

            if (encodedFrame != null)
            {
                SendFramePacket(encodedFrame);

                if (_screenSharePlayer.IsWindowVisible)
                    ProcessFrame(encodedFrame, token);
            }

            int delay = INTERVAL - (int)sw.ElapsedMilliseconds;
            if (delay > 0)
            {
                Thread.Sleep(delay);
            }
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
                if (_screenSharePlayer.IsWindowVisible)
                {
                    var encryptedData = _networkUtils.ReceiveScreenSharePacketAsync(token).GetAwaiter().GetResult();
                    var data = _cryptoSessionManager.DecryptMessage(encryptedData);

                    ProcessFrame(data, token);
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                return;
            }
            catch { /* ignore */ }
        }
    }

    private void ProcessFrame(byte[] imageData, CancellationToken token)
    {
        _screenSharePlayer.UpdateFrame(imageData, token);
    }

    public void Dispose()
    {
        _receiveCancellationTokenSource.Cancel();
        _screenShareReceiveThread.Join();
        _receiveCancellationTokenSource.Dispose();

        StopSharing();

        LoopbackAudioCapture.Dispose();
        H264Encoder.Cleanup();

        GC.SuppressFinalize(this);
    }
}