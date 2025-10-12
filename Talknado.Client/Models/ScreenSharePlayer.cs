using CommunityToolkit.Mvvm.ComponentModel;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Talknado.Client.Models.Helpers.ScreenShare;

namespace Talknado.Client.Models
{
    public interface IScreenSharePlayer
    {
        void UpdateTile(byte packetId, byte[] jpegData, byte tileX, byte tileY);
        ImageSource DisplayImage { get; }
        int FlushCallsPerSecond { get; }
        bool IsWindowVisible { get; set; }
        string ScreenShareUsername { get; set; }
    }
    public partial class ScreenSharePlayer : ObservableObject, IScreenSharePlayer, IDisposable
    {
        private const int MAX_DIMENSION = 1280;
        private const int TILE_GRID_CELL_COUNT = 16;
        private const int TILE_SIZE = MAX_DIMENSION / TILE_GRID_CELL_COUNT;

        private volatile WriteableBitmap _displayBitmap = null!;
        public ImageSource DisplayImage => _displayBitmap;

        private readonly ReaderWriterLockSlim _flushLock = new();
        private readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
        private readonly ConcurrentDictionary<(byte x, byte y), byte[]> _pendingTiles = new();
        private readonly Timer _flushStatsTimer = null!;
        private volatile byte _currentPacketId = 0;
        private Int32Rect[,] _tileRects = null!;
        private int _actualTilesX;
        private int _actualTilesY;
        private double _lastAspectRatio;

        public double AspectRatio { get; set; } = 16.0 / 9.0;

        [ObservableProperty]
        private bool _isWindowVisible = false;

        [ObservableProperty]
        private string _screenShareUsername = string.Empty;

        [ObservableProperty]
        private volatile int _flushCallsPerSecond = 0;
        private int _flushCallCount;

        public ScreenSharePlayer()
        {
            _lastAspectRatio = AspectRatio;

            CreateBuffer();

            _flushStatsTimer = new Timer(_ =>
            {
                FlushCallsPerSecond = _flushCallCount;
                _flushCallCount = 0;
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        private void CreateBuffer()
        {
            if (AspectRatio > 1)
            {
                _actualTilesX = TILE_GRID_CELL_COUNT;
                _actualTilesY = (int)(TILE_GRID_CELL_COUNT / AspectRatio);
            }
            else
            {
                _actualTilesY = TILE_GRID_CELL_COUNT;
                _actualTilesX = (int)(TILE_GRID_CELL_COUNT * AspectRatio);
            }

            _actualTilesX = Math.Max(1, Math.Min(_actualTilesX, TILE_GRID_CELL_COUNT));
            _actualTilesY = Math.Max(1, Math.Min(_actualTilesY, TILE_GRID_CELL_COUNT));

            int width = TILE_SIZE * _actualTilesX;
            int height = TILE_SIZE * _actualTilesY;

            _displayBitmap = new WriteableBitmap(
                width, height,
                96, 96,
                PixelFormats.Bgra32,
                null);

            _tileRects = new Int32Rect[_actualTilesX, _actualTilesY];
            for (int y = 0; y < _actualTilesY; y++)
            {
                for (int x = 0; x < _actualTilesX; x++)
                {
                    _tileRects[x, y] = new Int32Rect(
                        x * TILE_SIZE,
                        y * TILE_SIZE,
                        TILE_SIZE,
                        TILE_SIZE);
                }
            }

            Clear();
        }

        private void RecreateBuffer()
        {
            _lastAspectRatio = AspectRatio;
            CreateBuffer();
        }

        public void UpdateTile(byte packetId, byte[] jpegData, byte tileX, byte tileY)
        {
            if (tileX >= _actualTilesX || tileY >= _actualTilesY)
                return;

            bool shouldFlush = false;

            _flushLock.EnterReadLock();
            try
            {
                if (!_lastAspectRatio.Equals(AspectRatio))
                {
                    _flushLock.ExitReadLock();
                    _flushLock.EnterWriteLock();
                    try
                    {
                        if (!_lastAspectRatio.Equals(AspectRatio))
                        {
                            RecreateBuffer();
                        }
                    }
                    finally
                    {
                        _flushLock.ExitWriteLock();
                    }
                    _flushLock.EnterReadLock();
                }

                if (packetId != _currentPacketId)
                {
                    _currentPacketId = packetId;
                    shouldFlush = true;
                }
                else
                {
                    _pendingTiles[(tileX, tileY)] = jpegData;
                }
            }
            finally
            {
                _flushLock.ExitReadLock();
            }

            if (shouldFlush)
            {
                FlushPendingTiles();

                _flushLock.EnterReadLock();
                try
                {
                    _pendingTiles[(tileX, tileY)] = jpegData;
                }
                finally
                {
                    _flushLock.ExitReadLock();
                }
            }
        }

        public void FlushPendingTiles()
        {
            KeyValuePair<(byte, byte), byte[]>[] items;

            _flushLock.EnterWriteLock();
            try
            {
                items = new KeyValuePair<(byte, byte), byte[]>[_pendingTiles.Count];
                int i = 0;
                foreach (var kvp in _pendingTiles)
                {
                    items[i++] = kvp;
                }
            }
            finally
            {
                _flushLock.ExitWriteLock();
            }

            if (items.Length == 0)
                return;

            _flushCallCount++;

            var tileRects = _tileRects;
            int actualTilesX = _actualTilesX;
            int actualTilesY = _actualTilesY;

            var decodedTiles = new List<(Int32Rect rect, byte[] pixels, int stride)>(items.Length);
            var lockObj = new object();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            Parallel.ForEach(items, parallelOptions,
                () => new List<(Int32Rect, byte[], int)>(items.Length / Environment.ProcessorCount + 1),
                (kvp, _, localList) =>
                {
                    var ((tileX, tileY), webpData) = kvp;
                    if (tileX >= actualTilesX || tileY >= actualTilesY || webpData is null)
                        return localList;

                    try
                    {
                        var raw = WebPCodec.Decode(webpData, out int w, out int h);
                        if (w != TILE_SIZE || h != TILE_SIZE)
                            return localList;

                        int stride = w * 4;
                        int bufSize = stride * h;

                        var rent = _arrayPool.Rent(bufSize);

                        unsafe
                        {
                            fixed (byte* src = raw)
                            fixed (byte* dst = rent)
                            {
                                Buffer.MemoryCopy(src, dst, bufSize, bufSize);
                            }
                        }

                        localList.Add((tileRects[tileX, tileY], rent, stride));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Tile decode failed: {ex.Message}");
                    }

                    return localList;
                },
                localList =>
                {
                    if (localList.Count > 0)
                    {
                        lock (lockObj)
                            decodedTiles.AddRange(localList);
                    }
                });

            if (decodedTiles.Count == 0)
                return;

            Application.Current.Dispatcher.BeginInvoke(
                () =>
                {
                    try
                    {
                        RenderTiles(decodedTiles);
                    }
                    finally
                    {
                        foreach (var (_, buf, _) in decodedTiles)
                            _arrayPool.Return(buf, clearArray: false);
                    }
                },
                DispatcherPriority.Send);
        }

        private unsafe void RenderTiles(List<(Int32Rect rect, byte[] pixels, int stride)> tiles)
        {
            _displayBitmap.Lock();
            try
            {
                IntPtr back = _displayBitmap.BackBuffer;
                int bufStride = _displayBitmap.BackBufferStride;
                byte* backPtr = (byte*)back;

                var dirtyRegions = new List<Int32Rect>(tiles.Count);

                foreach (var (rect, pixels, srcStride) in tiles)
                {
                    byte* dst = backPtr + rect.Y * bufStride + rect.X * 4;

                    fixed (byte* src = pixels)
                    {
                        if (srcStride == bufStride && rect.Width * 4 == srcStride)
                        {
                            Buffer.MemoryCopy(src, dst, rect.Height * srcStride, rect.Height * srcStride);
                        }
                        else
                        {
                            for (int y = 0; y < rect.Height; y++)
                            {
                                Buffer.MemoryCopy(
                                    src + y * srcStride,
                                    dst + y * bufStride,
                                    rect.Width * 4,
                                    rect.Width * 4);
                            }
                        }
                    }

                    dirtyRegions.Add(rect);
                }

                var mergedRegions = MergeDirtyRegions(dirtyRegions);
                foreach (var region in mergedRegions)
                {
                    _displayBitmap.AddDirtyRect(region);
                }
            }
            finally
            {
                _displayBitmap.Unlock();
            }
        }

        private static List<Int32Rect> MergeDirtyRegions(List<Int32Rect> regions)
        {
            if (regions.Count <= 1)
                return regions;

            var merged = new List<Int32Rect>();
            var sorted = regions.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();

            var current = sorted[0];

            for (int i = 1; i < sorted.Count; i++)
            {
                var next = sorted[i];

                if (current.Y == next.Y && current.X + current.Width >= next.X)
                {
                    current = new Int32Rect(
                        current.X,
                        current.Y,
                        Math.Max(current.X + current.Width, next.X + next.Width) - current.X,
                        current.Height);
                }
                else if (current.X == next.X && current.Width == next.Width &&
                         current.Y + current.Height >= next.Y)
                {
                    current = new Int32Rect(
                        current.X,
                        current.Y,
                        current.Width,
                        Math.Max(current.Y + current.Height, next.Y + next.Height) - current.Y);
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
            return merged;
        }

        public void Clear()
        {
            var blackPixels = new byte[_displayBitmap.PixelWidth * _displayBitmap.PixelHeight * 4];
            _displayBitmap.WritePixels(
                new Int32Rect(0, 0, _displayBitmap.PixelWidth, _displayBitmap.PixelHeight),
                blackPixels,
                _displayBitmap.PixelWidth * 4,
                0);
        }
        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
