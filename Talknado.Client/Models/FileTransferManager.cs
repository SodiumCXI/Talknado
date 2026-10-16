using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Talknado.Client.Models.Helpers.Media;

namespace Talknado.Client.Models;

public interface IFileTransferManager
{
    void Start(string host, int port, Action<byte[]> broadcast);
    void Stop();
    Attachment Share(string path, ProbeResult probe, string caption);
    void HandleAnnouncement(ReadOnlySpan<byte> body);
    void HandleUserLeft(ushort userId);
    void Download(Attachment attachment);
    void SaveToDownloads(Attachment attachment);
    void CancelDownload(Attachment attachment);
    IMediaReader OpenReader(Attachment attachment);
}

/// <summary>
/// Файл остаётся у владельца, сервер только пересылает по каналу файлов.
/// Получатель запрашивает данные окнами по 512 КБ и держит в пути не больше
/// двух окон: скорость задаёт предел сервера, очередь на сервере не растёт.
/// <para>
/// Принятое ложится в файл на своё место, учёт ведётся кусками по 32 КБ.
/// Поэтому видео можно смотреть, не дожидаясь конца загрузки: плеер просит
/// нужное место, загрузка перескакивает туда и идёт от него вперёд.
/// </para>
/// </summary>
public sealed class FileTransferManager(IMessagesManager messagesManager, IConnectionInfo connectionInfo,
    IFileChannel fileChannel) : IFileTransferManager, IDisposable
{
    public const int ChunkSize = 32 * 1024;
    private const int WindowChunks = 16;
    // За два пинга приходит не больше запрошенного. При просмотре окно меньше:
    // после перемотки сначала доходит уже запрошенное.
    private const int DownloadOutstandingChunks = 8 * WindowChunks;
    private const int StreamingOutstandingChunks = 2 * WindowChunks;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);

    private static readonly string CacheRoot = Path.Combine(Path.GetTempPath(), "Talknado");

    /// <summary>Принятые картинки и видео, вставленные из буфера обмена картинки. Живёт до выхода.</summary>
    public static string CacheDirectory { get; } = Path.Combine(CacheRoot, Environment.ProcessId.ToString());

    private readonly IMessagesManager _messagesManager = messagesManager;
    private readonly IConnectionInfo _connectionInfo = connectionInfo;
    private readonly IFileChannel _fileChannel = fileChannel;

    private readonly ConcurrentDictionary<uint, string> _shared = new();
    private readonly ConcurrentDictionary<(ushort Owner, uint FileId), Attachment> _announced = new();
    private readonly ConcurrentDictionary<(ushort Owner, uint FileId), RemoteFile> _files = new();

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(50);
    private DispatcherTimer? _progressTimer;
    private int _progressTimerRunning;
    private readonly Channel<(ushort Requester, uint FileId, long Offset, int Length)> _requests =
        Channel.CreateUnbounded<(ushort, uint, long, int)>(new UnboundedChannelOptions { SingleReader = true });

    private Action<byte[]>? _broadcast;
    private CancellationTokenSource? _sessionCts;
    private DispatcherTimer? _watchdog;

    public void Start(string host, int port, Action<byte[]> broadcast)
    {
        _broadcast = broadcast;
        _sessionCts = new CancellationTokenSource();
        _fileChannel.PacketReceived = OnPacket;
        _fileChannel.Start(host, port);
        _ = Task.Run(() => ServeRequests(_sessionCts.Token));

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _watchdog.Tick += (_, _) => CheckStalled();
            _watchdog.Start();
        });
    }

    public void Stop()
    {
        _sessionCts?.Cancel();
        _fileChannel.Stop();
        _fileChannel.PacketReceived = null;
        _broadcast = null;
        _shared.Clear();

        Application.Current?.Dispatcher.BeginInvoke(() => _watchdog?.Stop());

        foreach (var key in _announced.Keys)
            MarkUnavailable(key);
    }

    public Attachment Share(string path, ProbeResult probe, string caption)
    {
        uint fileId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _shared[fileId] = path;

        var attachment = new Attachment
        {
            OwnerId = _connectionInfo.LocalUserId,
            FileId = fileId,
            Kind = probe.Kind,
            FileName = Path.GetFileName(path),
            Size = new FileInfo(path).Length,
            PixelWidth = probe.Width,
            PixelHeight = probe.Height,
            Duration = probe.Duration,
            IsOwn = true,
            State = AttachmentState.Ready,
            Progress = 1,
            LocalPath = path,
            Thumbnail = probe.Thumbnail
        };

        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            writer.Write("#FIL"u8);
            writer.Write(attachment.OwnerId);
            writer.Write(fileId);
            writer.Write((byte)attachment.Kind);
            writer.Write(attachment.Size);
            writer.Write(attachment.PixelWidth);
            writer.Write(attachment.PixelHeight);
            writer.Write((long)attachment.Duration.TotalMilliseconds);
            writer.Write(attachment.FileName);
            writer.Write(caption);
            writer.Write(probe.PreviewJpeg?.Length ?? 0);
            if (probe.PreviewJpeg != null)
                writer.Write(probe.PreviewJpeg);
        }

        _broadcast?.Invoke(ms.ToArray());
        return attachment;
    }

    public void HandleAnnouncement(ReadOnlySpan<byte> body)
    {
        using var reader = new BinaryReader(new MemoryStream(body.ToArray()), Encoding.UTF8);

        var ownerId = reader.ReadUInt16();
        var fileId = reader.ReadUInt32();
        var kind = (AttachmentKind)reader.ReadByte();
        var size = reader.ReadInt64();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var duration = TimeSpan.FromMilliseconds(reader.ReadInt64());
        var fileName = Path.GetFileName(reader.ReadString());
        var caption = reader.ReadString();
        var preview = reader.ReadBytes(reader.ReadInt32());

        var attachment = new Attachment
        {
            OwnerId = ownerId,
            FileId = fileId,
            Kind = kind,
            FileName = fileName,
            Size = size,
            PixelWidth = width,
            PixelHeight = height,
            Duration = duration,
            State = AttachmentState.Remote,
            Thumbnail = preview.Length > 0 ? TryLoad(() => MediaProbe.LoadImage(preview)) : null
        };

        _announced[(ownerId, fileId)] = attachment;
        _messagesManager.AddMessage(ownerId, caption, attachment);

        if (kind == AttachmentKind.Image)
            Download(attachment);
    }

    public void HandleUserLeft(ushort userId)
    {
        foreach (var key in _announced.Keys.Where(k => k.Owner == userId))
            MarkUnavailable(key);
    }

    public void Download(Attachment attachment)
    {
        var file = GetOrCreate(attachment, null);
        if (file == null)
            return;

        lock (file)
        {
            file.WantsWhole = true;
            RequestMore(file);
        }
    }

    public void SaveToDownloads(Attachment attachment)
    {
        if (attachment.SavedPath != null)
            return;

        if (attachment.State == AttachmentState.Ready && attachment.LocalPath is { } localPath)
        {
            if (attachment.Kind == AttachmentKind.File)
            {
                attachment.SavedPath = localPath;
                return;
            }

            var source = localPath;
            _ = Task.Run(() =>
            {
                try
                {
                    var destination = DownloadsFolder.CreateUniquePath(attachment.FileName);
                    File.Copy(source, destination);
                    if (!attachment.IsOwn)
                        DownloadsFolder.MarkFromInternet(destination);
                    Dispatch(() => attachment.SavedPath = destination);
                }
                catch { /* нет доступа к папке: кнопка останется, можно повторить */ }
            });
            return;
        }

        // Обычный файл качается сразу в «Загрузки»; картинка или видео — в кэш,
        // а в «Загрузки» копируется по окончании.
        var direct = attachment.Kind == AttachmentKind.File ? DownloadsFolder.CreateUniquePath(attachment.FileName) : null;
        var file = GetOrCreate(attachment, direct);
        if (file == null)
            return;

        lock (file)
        {
            file.WantsWhole = true;
            file.SaveToDownloads = true;
            RequestMore(file);
        }
    }

    public void CancelDownload(Attachment attachment)
    {
        if (!_files.TryRemove((attachment.OwnerId, attachment.FileId), out var file))
            return;

        lock (file)
        {
            file.Failed = true;
            file.Handle.Dispose();
            Monitor.PulseAll(file);
            try { File.Delete(file.Path); } catch { }
        }

        Dispatch(() =>
        {
            attachment.Progress = 0;
            attachment.State = AttachmentState.Remote;
        });
    }

    public IMediaReader OpenReader(Attachment attachment)
    {
        if (attachment.State == AttachmentState.Ready && attachment.LocalPath is { } path)
            return new LocalFileReader(path);

        var file = GetOrCreate(attachment, null) ?? throw new IOException("File is unavailable");
        return new RemoteFileReader(this, file);
    }

    private RemoteFile? GetOrCreate(Attachment attachment, string? destination)
    {
        if (attachment.IsOwn || attachment.State == AttachmentState.Failed)
            return null;

        var key = (attachment.OwnerId, attachment.FileId);
        if (_files.TryGetValue(key, out var existing))
            return existing;

        if (attachment.State == AttachmentState.Ready)
            return null;

        destination ??= Path.Combine(CacheDirectory, $"{attachment.OwnerId}_{attachment.FileId}{Path.GetExtension(attachment.FileName)}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var handle = File.OpenHandle(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.SetLength(handle, attachment.Size);

            var file = new RemoteFile(attachment, destination, handle);
            if (!_files.TryAdd(key, file))
            {
                handle.Dispose();
                return _files[key];
            }

            SetState(attachment, AttachmentState.Downloading);

            if (attachment.Size == 0)
                lock (file)
                    Complete(key, file);

            return file;
        }
        catch
        {
            SetState(attachment, AttachmentState.Failed);
            return null;
        }
    }

    private void OnPacket(ReadOnlySpan<byte> packet)
    {
        var command = packet[..4];
        var body = packet[4..];

        if (command.SequenceEqual("#FRQ"u8))
        {
            _requests.Writer.TryWrite((
                BinaryPrimitives.ReadUInt16LittleEndian(body),
                BinaryPrimitives.ReadUInt32LittleEndian(body[2..]),
                BinaryPrimitives.ReadInt64LittleEndian(body[6..]),
                BinaryPrimitives.ReadInt32LittleEndian(body[14..])));
        }
        else if (command.SequenceEqual("#FCH"u8))
        {
            HandleChunk(body);
        }
        else if (command.SequenceEqual("#FNA"u8))
        {
            MarkUnavailable((BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[2..])));
        }
    }

    private void HandleChunk(ReadOnlySpan<byte> body)
    {
        var key = (BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[2..]));
        var offset = BinaryPrimitives.ReadInt64LittleEndian(body[6..]);
        var data = body[14..];

        if (!_files.TryGetValue(key, out var file))
            return;

        lock (file)
        {
            if (file.Completed || file.Failed || offset % ChunkSize != 0 || offset + data.Length > file.Attachment.Size)
                return;

            int chunk = (int)(offset / ChunkSize);
            if (file.Requested[chunk])
            {
                file.Requested[chunk] = false;
                file.Outstanding = Math.Max(0, file.Outstanding - 1);
            }

            if (file.Have[chunk])
                return;

            try
            {
                RandomAccess.Write(file.Handle, data, offset);
            }
            catch
            {
                MarkUnavailable(key);
                return;
            }

            file.Have[chunk] = true;
            file.HaveCount++;
            file.LastActivity = Stopwatch.GetTimestamp();
            Monitor.PulseAll(file);

            if (file.HaveCount == file.Have.Length)
            {
                Complete(key, file);
                return;
            }

            EnsureProgressTimer();
            RequestMore(file);
        }
    }

    /// <summary>
    /// Запрашивает следующие недостающие куски, начиная с места, которое сейчас
    /// нужно плееру, а когда за ним всё есть — с начала файла. Вызывается под lock (file).
    /// </summary>
    private void RequestMore(RemoteFile file)
    {
        if (file.Completed || file.Failed || !file.WantsWhole && file.Readers == 0)
            return;

        int total = file.Have.Length;
        int focus = (int)Math.Min(file.Focus / ChunkSize, total - 1);

        int maxOutstanding = file.Readers > 0 ? StreamingOutstandingChunks : DownloadOutstandingChunks;
        while (file.Outstanding < maxOutstanding)
        {
            int start = FindMissing(file, focus, total);
            if (start < 0)
                start = FindMissing(file, 0, focus);
            if (start < 0)
                return;

            int end = start;
            while (end < total && end - start < WindowChunks && !file.Have[end] && !file.Requested[end])
                end++;

            for (int i = start; i < end; i++)
                file.Requested[i] = true;
            file.Outstanding += end - start;

            long offset = (long)start * ChunkSize;
            int length = (int)Math.Min((long)(end - start) * ChunkSize, file.Attachment.Size - offset);
            SendRequest(file.Attachment, offset, length);
        }
    }

    private static int FindMissing(RemoteFile file, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (!file.Have[i] && !file.Requested[i])
                return i;
        }
        return -1;
    }

    private void SendRequest(Attachment attachment, long offset, int length)
    {
        Span<byte> body = stackalloc byte[4 + 18];
        "#FRQ"u8.CopyTo(body);
        BinaryPrimitives.WriteUInt16LittleEndian(body[4..], _connectionInfo.LocalUserId);
        BinaryPrimitives.WriteUInt32LittleEndian(body[6..], attachment.FileId);
        BinaryPrimitives.WriteInt64LittleEndian(body[10..], offset);
        BinaryPrimitives.WriteInt32LittleEndian(body[18..], length);

        try
        {
            _fileChannel.Send(attachment.OwnerId, body);
        }
        catch { /* канал переподключается: перезапрос сделает сторож */ }
    }

    private async Task ServeRequests(CancellationToken token)
    {
        const int header = 4 + 14;
        var buffer = new byte[header + ChunkSize];
        "#FCH"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), _connectionInfo.LocalUserId);

        try
        {
            await foreach (var (requester, fileId, offset, length) in _requests.Reader.ReadAllAsync(token))
            {
                try
                {
                    if (!_shared.TryGetValue(fileId, out var path))
                        throw new FileNotFoundException();

                    using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6), fileId);

                    for (long sent = 0; sent < length && !token.IsCancellationRequested;)
                    {
                        int read = RandomAccess.Read(file, buffer.AsSpan(header, (int)Math.Min(ChunkSize, length - sent)), offset + sent);
                        if (read <= 0)
                            break;

                        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(10), offset + sent);
                        _fileChannel.Send(requester, buffer.AsSpan(0, header + read));
                        sent += read;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException) when (_shared.ContainsKey(fileId))
                {
                    /* обрыв канала: получатель перезапросит */
                }
                catch
                {
                    SendUnavailable(requester, fileId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SendUnavailable(ushort requester, uint fileId)
    {
        Span<byte> body = stackalloc byte[4 + 6];
        "#FNA"u8.CopyTo(body);
        BinaryPrimitives.WriteUInt16LittleEndian(body[4..], _connectionInfo.LocalUserId);
        BinaryPrimitives.WriteUInt32LittleEndian(body[6..], fileId);
        try { _fileChannel.Send(requester, body); } catch { }
    }

    private void CheckStalled()
    {
        long now = Stopwatch.GetTimestamp();

        foreach (var (key, file) in _files)
        {
            lock (file)
            {
                if (file.Outstanding == 0)
                {
                    RequestMore(file);
                    continue;
                }

                if (Stopwatch.GetElapsedTime(file.LastActivity, now) < StallTimeout)
                    continue;

                // Пока владелец в сессии, простой не делает файл недоступным.
                file.LastActivity = now;
                Array.Clear(file.Requested);
                file.Outstanding = 0;
                RequestMore(file);
            }
        }
    }

    private void EnsureProgressTimer()
    {
        if (Interlocked.Exchange(ref _progressTimerRunning, 1) == 1)
            return;

        Dispatch(() =>
        {
            _progressTimer ??= new DispatcherTimer(ProgressInterval, DispatcherPriority.Background,
                (_, _) => UpdateProgress(), Dispatcher.CurrentDispatcher);
            _progressTimer.Start();
        });
    }

    // Признак сбрасывается до обхода: кусок, пришедший во время обхода, снова запустит таймер.
    private void UpdateProgress()
    {
        Volatile.Write(ref _progressTimerRunning, 0);

        bool active = false;
        foreach (var file in _files.Values)
        {
            if (file.Completed || file.Failed)
                continue;

            file.Attachment.Progress = (double)file.HaveCount / file.Have.Length;
            active |= file.Outstanding > 0;
        }

        if (active)
            Volatile.Write(ref _progressTimerRunning, 1);
        else
            _progressTimer?.Stop();
    }

    /// <summary>Вызывается под lock (file).</summary>
    private void Complete((ushort, uint) key, RemoteFile file)
    {
        _files.TryRemove(key, out _);
        file.Completed = true;
        file.Handle.Dispose();
        Monitor.PulseAll(file);

        var attachment = file.Attachment;
        var path = file.Path;
        bool save = file.SaveToDownloads;

        _ = Task.Run(() =>
        {
            var thumbnail = attachment.IsImage ? TryLoad(() => MediaProbe.LoadImage(path, MediaProbe.ThumbnailWidth)) : null;

            string? saved = null;
            try
            {
                if (attachment.Kind == AttachmentKind.File)
                {
                    DownloadsFolder.MarkFromInternet(path);
                    saved = path;
                }
                else if (save)
                {
                    saved = DownloadsFolder.CreateUniquePath(attachment.FileName);
                    File.Copy(path, saved);
                    DownloadsFolder.MarkFromInternet(saved);
                }
            }
            catch { saved = null; }

            Dispatch(() =>
            {
                attachment.LocalPath = path;
                attachment.Progress = 1;
                if (thumbnail != null)
                    attachment.Thumbnail = thumbnail;
                if (saved != null)
                    attachment.SavedPath = saved;
                attachment.State = AttachmentState.Ready;
            });
        });
    }

    private void MarkUnavailable((ushort Owner, uint FileId) key)
    {
        if (_files.TryRemove(key, out var file))
        {
            lock (file)
            {
                file.Failed = true;
                file.Handle.Dispose();
                Monitor.PulseAll(file);
                try { File.Delete(file.Path); } catch { }
            }
        }

        if (_announced.TryGetValue(key, out var attachment))
        {
            Dispatch(() =>
            {
                if (attachment.State != AttachmentState.Ready)
                    attachment.State = AttachmentState.Failed;
            });
        }
    }

    private static void SetState(Attachment attachment, AttachmentState state) => Dispatch(() => attachment.State = state);

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private static T? TryLoad<T>(Func<T> load) where T : class
    {
        try { return load(); } catch { return null; }
    }

    /// <summary>Удаляет кэш этого запуска и кэши прошлых запусков, которые не успели убрать за собой.</summary>
    public static void CleanCache(bool includeCurrent)
    {
        if (!Directory.Exists(CacheRoot))
            return;

        foreach (var directory in Directory.EnumerateDirectories(CacheRoot))
        {
            var name = Path.GetFileName(directory);
            bool isCurrent = name == Environment.ProcessId.ToString();
            bool isAlive = !isCurrent && int.TryParse(name, out var pid) && IsProcessAlive(pid);

            if (isCurrent ? includeCurrent : !isAlive)
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.StartsWith("Talknado", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Stop();

    private sealed class RemoteFile(Attachment attachment, string path, SafeFileHandle handle)
    {
        private static int ChunkCount(long size) => Math.Max(1, (int)((size + ChunkSize - 1) / ChunkSize));

        public Attachment Attachment { get; } = attachment;
        public string Path { get; } = path;
        public SafeFileHandle Handle { get; } = handle;
        public bool[] Have { get; } = new bool[ChunkCount(attachment.Size)];
        public bool[] Requested { get; } = new bool[ChunkCount(attachment.Size)];
        public int HaveCount { get; set; }
        public int Outstanding { get; set; }
        public long Focus { get; set; }
        public int Readers { get; set; }
        public bool WantsWhole { get; set; }
        public bool SaveToDownloads { get; set; }
        public bool Completed { get; set; }
        public bool Failed { get; set; }
        public long LastActivity { get; set; } = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Чтение для плеера. Недостающий кусок запрашивается в первую очередь,
    /// чтение ждёт его прихода. Пока читатель открыт, загрузка идёт вперёд
    /// от места чтения; закрыли видео, не досмотрев, — загрузка встаёт.
    /// </summary>
    private sealed class RemoteFileReader : IMediaReader
    {
        private readonly FileTransferManager _owner;
        private readonly RemoteFile _file;
        private readonly SafeFileHandle _readHandle;

        public RemoteFileReader(FileTransferManager owner, RemoteFile file)
        {
            _owner = owner;
            _file = file;
            _readHandle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            lock (file)
            {
                file.Readers++;
                owner.RequestMore(file);
            }
        }

        public long Size => _file.Attachment.Size;

        public bool IsWaiting { get; private set; }

        public int Read(long position, Span<byte> buffer, CancellationToken token)
        {
            if (position >= Size)
                return 0;

            int chunk = (int)(position / ChunkSize);

            lock (_file)
            {
                _file.Focus = position;

                while (!_file.Completed && !_file.Have[chunk])
                {
                    if (_file.Failed)
                        throw new IOException("File is unavailable");

                    token.ThrowIfCancellationRequested();

                    if (!_file.Requested[chunk])
                        _owner.RequestMore(_file);

                    IsWaiting = true;
                    Monitor.Wait(_file, 100);
                }

                IsWaiting = false;
                if (_file.Failed)
                    throw new IOException("File is unavailable");

                long available = Size - position;
                if (!_file.Completed)
                {
                    int last = chunk;
                    while (last + 1 < _file.Have.Length && _file.Have[last + 1])
                        last++;
                    available = Math.Min(available, (long)(last + 1) * ChunkSize - position);
                }

                int count = (int)Math.Min(buffer.Length, available);
                return RandomAccess.Read(_readHandle, buffer[..count], position);
            }
        }

        public bool Has(long from, long to)
        {
            int first = (int)(from / ChunkSize);
            int last = (int)(Math.Max(from, to - 1) / ChunkSize);

            lock (_file)
            {
                if (_file.Completed)
                    return true;

                for (int i = first; i <= last && i < _file.Have.Length; i++)
                {
                    if (!_file.Have[i])
                        return false;
                }
                return true;
            }
        }

        public void Dispose()
        {
            lock (_file)
                _file.Readers--;
            _readHandle.Dispose();
        }
    }
}

public interface IMediaReader : IDisposable
{
    long Size { get; }
    bool IsWaiting { get; }

    /// <summary>Блокирует, пока данных на этом месте нет. 0 — конец файла.</summary>
    int Read(long position, Span<byte> buffer, CancellationToken token);

    /// <summary>Загружены ли байты от <paramref name="from"/> до <paramref name="to"/>.</summary>
    bool Has(long from, long to);
}

public sealed class LocalFileReader(string path) : IMediaReader
{
    private readonly SafeFileHandle _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public long Size => RandomAccess.GetLength(_handle);
    public bool IsWaiting => false;

    public int Read(long position, Span<byte> buffer, CancellationToken token) =>
        position >= Size ? 0 : RandomAccess.Read(_handle, buffer, position);

    public bool Has(long from, long to) => true;

    public void Dispose() => _handle.Dispose();
}

/// <summary>Папка «Загрузки» пользователя и пометка «из интернета» для скачанного.</summary>
public static class DownloadsFolder
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Path
    {
        get
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try { return Marshal.PtrToStringUni(pointer)!; }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public static string CreateUniquePath(string fileName)
    {
        var folder = Path;
        Directory.CreateDirectory(folder);

        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        var candidate = System.IO.Path.Combine(folder, fileName);

        for (int i = 1; File.Exists(candidate); i++)
            candidate = System.IO.Path.Combine(folder, $"{name} ({i}){extension}");

        return candidate;
    }

    // Та же пометка, что ставят браузеры: Windows покажет предупреждение
    // SmartScreen при запуске исполняемого файла от другого участника.
    public static void MarkFromInternet(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch { /* не NTFS: пометку поставить некуда */ }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
