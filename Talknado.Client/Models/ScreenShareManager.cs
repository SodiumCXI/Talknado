using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Wave;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
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
        private const int TARGET_FPS = 15;
        private const int AUDIO_FRAME_SIZE = (int)(48000 * 2 * (1.0 / TARGET_FPS));
        private const int MAX_DIMENSION = 1280;
        private const int TILE_GRID_CELL_COUNT = 16;
        private const int TILE_SIZE = MAX_DIMENSION / TILE_GRID_CELL_COUNT;

        private readonly INetworkUtils _networkUtils;
        private readonly ICryptoSessionManager _cryptoSessionManager;
        private readonly IUsersAudioPlayer _usersAudioPlayer;
        private readonly IScreenSharePlayer _screenSharePlayer;

        private readonly CancellationTokenSource _receiveCancellationTokenSource;
        private CancellationTokenSource? _sendCancellationTokenSource;

        private readonly Thread _screenShareReceiveThread;
        private Thread? _screenShareSendThread;

        private WasapiLoopbackCapture? _audioCapture;

        private uint[,] _previousTiles = null!;

        [ObservableProperty]
        private bool _isSharing = false;

        private volatile bool _withAudio;

        private byte _currentPacketId = 0;
        private int _consecutiveHighLoss;
        private DateTime _lastQualityDecrease = DateTime.MinValue;
        private DateTime _lastGoodStats = DateTime.UtcNow;

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
            _currentPacketId = 0;

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
            _networkUtils.SendScreenSharePacketAsync(encryptedPacket, token).GetAwaiter().GetResult();

            _currentPacketId = (byte)((_currentPacketId + 1) % 3);
        }

        private void ShareScreenLoop(CancellationToken token)
        {
            const int INTERVAL = 1000 / TARGET_FPS;
            const int WIDTH_TO_RESIZE = 1280;
            const int HEIGHT_TO_RESIZE = 720;
            const int TILES_X = 16;
            const int TILES_Y = 9;

            // Pre-calculated values
            var tileBounds = new (int x, int y, int w, int h)[TILES_X, TILES_Y];
            for (int yy = 0; yy < TILES_Y; yy++)
            {
                for (int xx = 0; xx < TILES_X; xx++)
                {
                    int tx = xx * TILE_SIZE;
                    int ty = yy * TILE_SIZE;
                    tileBounds[xx, yy] = (
                        tx, ty,
                        Math.Min(TILE_SIZE, WIDTH_TO_RESIZE - tx),
                        Math.Min(TILE_SIZE, HEIGHT_TO_RESIZE - ty)
                    );
                }
            }

            _previousTiles = new uint[TILE_GRID_CELL_COUNT, TILE_GRID_CELL_COUNT];

            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();

                byte[] screenFrame = null!;
                byte[] resizedFrame = null!;

                try
                {
                    screenFrame = ScreenGrabber.CaptureFrame(out int w, out int h, out int screenStride);
                    CursorRenderer.OverlayCursorOnByteBuffer(screenFrame, w, h);
                    resizedFrame = FrameResizer.ResizeFrame(screenFrame, w, h, WIDTH_TO_RESIZE, HEIGHT_TO_RESIZE, out int resizedStride);

                    Parallel.For(0, TILES_X * TILES_Y, linearIndex =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        int yy = linearIndex / TILES_X;
                        int xx = linearIndex % TILES_X;

                        var (tx, ty, tw, th) = tileBounds[xx, yy];
                        ProcessTile(resizedFrame, resizedStride, tx, ty, tw, th, xx, yy, token);
                    });
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(screenFrame);
                    ArrayPool<byte>.Shared.Return(resizedFrame);
                }

                _currentPacketId = (byte)((_currentPacketId + 1) % 3);

                int delay = INTERVAL - (int)sw.ElapsedMilliseconds;
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }
        }

        private unsafe void ProcessTile(
            byte[] screenBuf, int screenStride,
            int tileX, int tileY, int w, int h,
            int gridX, int gridY, CancellationToken token)
        {
            fixed (byte* basePtr = screenBuf)
            {
                byte* tilePtr = basePtr + tileY * screenStride + tileX * 4;

                var crc32 = new Crc32();
                for (int row = 0; row < h; row++)
                {
                    byte* srcRow = tilePtr + row * screenStride;
                    crc32.Append(new ReadOnlySpan<byte>(srcRow, w * 4));
                }

                uint crc = BinaryPrimitives.ReadUInt32BigEndian(crc32.GetCurrentHash());

                uint slot = _previousTiles[gridX, gridY];
                if (Volatile.Read(ref slot) != crc)
                {
                    byte[] webp = WebPCodec.Encode(tilePtr, w, h, screenStride, 90f);
                    SendTilePacket(webp, gridX, gridY, token);

                    Volatile.Write(ref slot, crc);
                    _previousTiles[gridX, gridY] = slot;
                }
            }
        }

        private void SendTilePacket(byte[] webpData, int x, int y, CancellationToken token)
        {
            var header = new PacketHeader
            {
                IsAudio = 0,
                TileX = (byte)x,
                TileY = (byte)y,
                PacketId = _currentPacketId
            };

            var headerBytes = header.ToBytes();
            var packet = new byte[PacketHeader.SIZE + webpData.Length];
            Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
            Buffer.BlockCopy(webpData, 0, packet, headerBytes.Length, webpData.Length);

            var encryptedPacket = _cryptoSessionManager.EncryptMessage(packet);
            _networkUtils.SendScreenSharePacketAsync(encryptedPacket, token).GetAwaiter().GetResult();

            ProcessImage(webpData, header, token);
        }

        private void HandleReceiveScreenShare(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    //if (_screenSharePlayer.IsWindowVisible)
                    //{
                        var packet = _networkUtils.ReceiveScreenSharePacketAsync(token).GetAwaiter().GetResult();
                        var decryptedPacket = _cryptoSessionManager.DecryptMessage(packet);

                        ProcessPacket(decryptedPacket, token);
                    //}
                    //else
                    //{
                    //    Thread.Sleep(50);
                    //}
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
                ProcessImage(data, header, token);
            }
        }

        private void ProcessImage(byte[] imageData, PacketHeader header, CancellationToken token)
        {
            Task.Run(() => _screenSharePlayer.UpdateTile(header.PacketId, imageData, header.TileX, header.TileY), token);
        }

        public void Dispose()
        {
            StopSharing();
            _audioCapture?.Dispose();
            GC.SuppressFinalize(this);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PacketHeader
        {
            public byte IsAudio;
            public byte TileX;
            public byte TileY;
            public byte PacketId;

            public const int SIZE = 4;

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
