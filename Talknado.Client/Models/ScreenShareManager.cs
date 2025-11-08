using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Talknado.Client.Models.Client.Helpers;
using Talknado.Client.Models.Helpers;
using Talknado.Client.Models.Helpers.ScreenShare;

namespace Talknado.Client.Models
{
    public interface IScreenShareManager
    {
        void StartSharing(bool withAudio = false);
        void StopSharing();
        bool IsSharing { get; }
    }

    public partial class ScreenShareManager : ObservableObject, IScreenShareManager, IDisposable
    {
        private const int TARGET_FPS = 30;
        private const int AUDIO_FRAME_SIZE = (int)(48000 * 2 * (1.0 / TARGET_FPS));

        private readonly INetworkUtils _networkUtils;
        private readonly ICryptoSessionManager _cryptoSessionManager;
        private readonly IUsersAudioPlayer _usersAudioPlayer;
        private readonly IScreenSharePlayer _screenSharePlayer;

        private readonly CancellationTokenSource _receiveCancellationTokenSource;
        private CancellationTokenSource? _sendCancellationTokenSource;

        private readonly Thread _screenShareReceiveThread;
        private Thread? _screenShareSendThread;

        private WasapiLoopbackCapture? _audioCapture;

        [ObservableProperty]
        private bool _isSharing = false;

        private bool _withAudio;

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

        public void StartSharing(bool withAudio = false)
        {
            if (IsSharing) return;

            IsSharing = true;
            _withAudio = withAudio;

            _sendCancellationTokenSource = new CancellationTokenSource();

            if (withAudio)
            {
                InitializeAudio(_sendCancellationTokenSource.Token);
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

            if (_audioCapture != null)
            {
                _audioCapture.StopRecording();
                _audioCapture.Dispose();
                _audioCapture = null;
            }
        }

        private void InitializeAudio(CancellationToken token)
        {
            _audioCapture = new WasapiLoopbackCapture
            {
                WaveFormat = new WaveFormat(48000, 16, 1)
            };

            _audioCapture.DataAvailable += (_, e) =>
            {
                if (!IsSharing || !_withAudio) return;

                try
                {
                    if (e.BytesRecorded >= AUDIO_FRAME_SIZE)
                    {
                        var buffer = new byte[AUDIO_FRAME_SIZE];
                        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, AUDIO_FRAME_SIZE);
                        SendAudioPacket(buffer, token);
                    }
                }
                catch { /* ignore */ }
            };

            _audioCapture.RecordingStopped += (_, _) =>
            {
                if (IsSharing && _withAudio)
                {
                    try { _audioCapture?.StartRecording(); }
                    catch { /* ignore */ }
                }
            };

            _audioCapture.StartRecording();
        }

        private void SendAudioPacket(byte[] audioData, CancellationToken token)
        {
            var header = new PacketHeader
            {
                IsAudio = 1
            };

            var headerBytes = header.ToBytes();
            var packet = new byte[PacketHeader.SIZE + audioData.Length];
            Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
            Buffer.BlockCopy(audioData, 0, packet, headerBytes.Length, audioData.Length);

            var encryptedPacket = _cryptoSessionManager.EncryptMessage(packet);
            _networkUtils.SendScreenSharePacketAsync(encryptedPacket).GetAwaiter().GetResult();
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
                    SendFramePacket(encodedFrame, token);

                    ProcessFrame(encodedFrame, token);
                }

                int delay = INTERVAL - (int)sw.ElapsedMilliseconds;
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }
        }

        private void SendFramePacket(byte[] frameData, CancellationToken token)
        {
            var header = new PacketHeader
            {
                IsAudio = 0
            };

            var headerBytes = header.ToBytes();
            var packet = new byte[PacketHeader.SIZE + frameData.Length];
            Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
            Buffer.BlockCopy(frameData, 0, packet, headerBytes.Length, frameData.Length);

            var encryptedPacket = _cryptoSessionManager.EncryptMessage(packet);

            _networkUtils.SendScreenSharePacketAsync(encryptedPacket).GetAwaiter().GetResult();
        }

        private void HandleReceiveScreenShare(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_screenSharePlayer.IsWindowVisible)
                    {
                        var packet = _networkUtils.ReceiveScreenSharePacketAsync(token).GetAwaiter().GetResult();

                        ProcessPacket(packet, token);
                    }
                    else
                    {
                        Thread.Sleep(50);
                    }
                }
                catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
                {
                    return;
                }
                catch { /* ignore */ }
            }
        }

        private void ProcessPacket(byte[] packet, CancellationToken token)
        {
            var decryptedPacket = _cryptoSessionManager.DecryptMessage(packet);

            var header = PacketHeader.FromBytes(decryptedPacket.AsSpan()[..PacketHeader.SIZE]);
            var data = decryptedPacket[PacketHeader.SIZE..];

            if (header.IsAudio == 1)
            {
                _usersAudioPlayer.Play(0, data);
            }
            else
            {
                ProcessFrame(data, token);
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
            H264Encoder.Cleanup();

            GC.SuppressFinalize(this);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PacketHeader
        {
            public byte IsAudio;

            public const int SIZE = 1;

            public readonly byte[] ToBytes()
            {
                var buffer = new byte[SIZE];
                MemoryMarshal.Write(buffer.AsSpan(), this);
                return buffer;
            }

            public static PacketHeader FromBytes(ReadOnlySpan<byte> data)
            {
                return MemoryMarshal.Read<PacketHeader>(data);
            }
        }
    }
}
