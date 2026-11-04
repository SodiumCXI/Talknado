using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Models.Helpers.Network;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.Models;

public interface IUsersAudioPlayer
{
    void Play(ushort userId, ushort sequence, ReadOnlySpan<byte> opusData);
}

public class UsersAudioPlayer : IUsersAudioPlayer, IDisposable
{
    private readonly IUsersInfo _usersInfo;
    private readonly ISettingsManager _settingsManager;

    private readonly ConcurrentDictionary<ushort, UserAudioStream> _userAudioStreams = [];

    // Снимок потоков для тика: тик идёт сто раз в секунду, а состав меняется
    // только с приходом и уходом участника.
    private volatile UserAudioStream[] _streams = [];
    private readonly object _streamsLock = new();
    // Свой поток, а не таймер пула: пул делят сеть и передача файлов, и когда они его
    // занимают, тик опаздывает, устройство пустеет и щёлкает, а срабатывания таймера,
    // накопившиеся в очереди, идут внахлёст и декодируют одним декодером из двух потоков.
    private const int PlaybackTickMilliseconds = 10;
    private readonly Thread _playbackThread;
    private volatile bool _disposed;


    public UsersAudioPlayer(ISettingsManager settingsManager, IUsersInfo usersInfo)
    {
        _usersInfo = usersInfo;

        _settingsManager = settingsManager;
        _playbackThread = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "Audio playback"
        };
        _playbackThread.Start();
        _settingsManager.OutputDeviceChanged += HandleOutputDeviceChanged;

    }

    public void Play(ushort userId, ushort sequence, ReadOnlySpan<byte> opusData)
    {
        if (_usersInfo.GetUsernameByUserId(userId) == string.Empty)
            return;

        if (!_userAudioStreams.TryGetValue(userId, out UserAudioStream? value))
        {
            var deviceId = ResolveOutputDeviceId(_settingsManager.SelectedOutputDevice);
            value = new UserAudioStream(_usersInfo, userId, deviceId);
            _userAudioStreams[userId] = value;
            UpdateStreams();
        }

        value.AddPacket(sequence, opusData);
    }

    private void RemoveUserStream(ushort userId)
    {
        if (_userAudioStreams.TryRemove(userId, out var stream))
        {
            UpdateStreams();
            stream.Dispose();
        }
    }

    private void UpdateStreams()
    {
        lock (_streamsLock)
            _streams = [.. _userAudioStreams.Values];
    }

    private void PlaybackLoop()
    {
        ProAudioThread.Enter();
        while (!_disposed)
        {
            PlaybackTick();
            Thread.Sleep(PlaybackTickMilliseconds);
        }
    }

    private void PlaybackTick()
    {
        var streams = _streams;

        foreach (var stream in streams)
            stream.Playback();

        foreach (var stream in streams)
        {
            if (stream.ConsecutiveLosses >= 50)
                RemoveUserStream(stream.UserId);
        }
    }

    internal static string? ResolveOutputDeviceId(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName) || deviceName == Strings.DefaultDeviceText)
            return null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                if (device.FriendlyName == deviceName)
                {
                    return device.ID;
                }
            }
        }
        catch { }

        return null;
    }

    private void HandleOutputDeviceChanged()
    {
        foreach (var userId in _userAudioStreams.Keys.ToList())
            RemoveUserStream(userId);
    }

    public void Dispose()
    {
        _disposed = true;
        _playbackThread.Join();

        foreach (var stream in _userAudioStreams.Values)
            stream.Dispose();

        _userAudioStreams.Clear();
        _settingsManager.OutputDeviceChanged -= HandleOutputDeviceChanged;


        GC.SuppressFinalize(this);
    }

    public class UserAudioStream : IDisposable
    {
        private readonly IUsersInfo _usersInfo;

        private WasapiOut? WasapiOut { get; set; }
        private BufferedWaveProvider WaveProvider { get; }
        private MMDevice? Device { get; set; }
        public int ConsecutiveLosses { get; private set; }
        public ushort UserId => _userId;

        private readonly ushort _userId;
        private readonly OpusCodecDecoder _decoder = new();
        private readonly byte[] _pcm = new byte[OpusCodecDecoder.FrameBytes];
        private readonly byte[] _silence = new byte[OpusCodecDecoder.FrameBytes];
        private readonly object _queueLock = new();
        private readonly SpeechDetector _speechDetector = new();

        // Недостающий пакет ждут, пока в устройстве есть запас: он мог быть переставлен
        // сетью или задержан. Запас кончился — лежат следующие, значит потерян, на его
        // место PLC; не лежит ничего — PLC сверх очереди, номер не сдвигается.
        private const int SlotCount = 64;
        private readonly PooledBuffer[] _slots = new PooledBuffer[SlotCount];
        private readonly bool[] _filled = new bool[SlotCount];
        private int _queued;
        private ushort _next;
        private bool _started;

        // Запас обязан быть больше порции, которой WasapiOut забирает звук, — всей
        // его задержки, иначе устройство голодает. Здесь вдвое больше: 20 мс против 40.
        private const int OutputLatencyMilliseconds = 20;
        private const int MIN_BUFFER_FRAMES = 4;
        private const int MAX_BUFFER_FRAMES = 30;
        private const int FRAME_BYTES = 960;
        private const int FRAME_MILLISECONDS = 10;

        private const int LOW_WATER_FRAMES = 2;

        // Дальше PLC тянет гул.
        private const int MAX_CONCEALED_FRAMES = 10;

        // Запас — разброс задержки пакетов за минуту и ещё два кадра: пакеты идут
        // парами раз в 20 мс, и вывод забирает звук порциями. PLC без сдвига номера
        // растит задержку сверх запаса; лишнее уходит сжатием кадров, не чаще
        // одного кадра из двух.
        private readonly DelaySpread _delaySpread = new();
        private int _targetBufferFrames = MIN_BUFFER_FRAMES;
        private bool _compressedLast;
        private int _concealed;
        private bool _hasPlayed;
        // Громкость, с которой ушёл прошлый кадр; -1 — кадров ещё не было
        private float _appliedVolume = -1;


        public UserAudioStream(IUsersInfo usersInfo, ushort userId, string? deviceId)
        {
            _usersInfo = usersInfo;
            _userId = userId;

            WaveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
            {
                DiscardOnBufferOverflow = true
            };

            try
            {
                var enumerator = new MMDeviceEnumerator();

                if (string.IsNullOrEmpty(deviceId))
                {
                    Device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                else
                {
                    Device = enumerator.GetDevice(deviceId);
                }

                WasapiOut = new WasapiOut(Device, AudioClientShareMode.Shared, true, OutputLatencyMilliseconds);
                WasapiOut.Init(new ProAudioWaveProvider(WaveProvider));
                WasapiOut.Play();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing audio output: {ex.Message}");

                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    Device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    WasapiOut = new WasapiOut(Device, AudioClientShareMode.Shared, true, OutputLatencyMilliseconds);
                    WasapiOut.Init(new ProAudioWaveProvider(WaveProvider));
                    WasapiOut.Play();
                }
                catch { /* ignore */ }
            }
        }

        public void AddPacket(ushort sequence, ReadOnlySpan<byte> opusData)
        {
            lock (_queueLock)
            {
                if (!_started)
                {
                    _next = sequence;
                    _started = true;
                }

                _delaySpread.Add(sequence, DelaySpread.NowMilliseconds());

                int ahead = (short)(sequence - _next);

                if (ahead < -1000 || ahead >= SlotCount)
                {
                    Clear();
                    _next = sequence;
                    ahead = 0;
                }

                if (ahead < 0)
                    return;

                int slot = sequence % SlotCount;
                if (_filled[slot])
                    return;

                _slots[slot] = PooledBuffer.CopyOf(opusData);
                _filled[slot] = true;
                _queued++;

                int dropThreshold = Math.Min(Math.Max(_targetBufferFrames * 3, 30), SlotCount - 4);
                while (_queued > dropThreshold)
                {
                    if (TakeNext(out var dropped))
                        dropped.Return();
                }
            }
        }

        /// <summary>Под _queueLock. false — пакета на месте нет.</summary>
        private bool TakeNext(out PooledBuffer packet)
        {
            int slot = _next % SlotCount;
            _next++;

            packet = _slots[slot];
            if (!_filled[slot])
                return false;

            _filled[slot] = false;
            _slots[slot] = default;
            _queued--;
            return true;
        }

        private void Clear()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (_filled[i])
                    _slots[i].Return();
                _filled[i] = false;
                _slots[i] = default;
            }
            _queued = 0;
        }

        private void ApplyVolume(float target, int length)
        {
            VolumeController.AdjustVolume(_pcm.AsSpan(0, length), _appliedVolume, target);
            _appliedVolume = target;
        }

        /// <summary>
        /// Скорость воспроизведения задаёт устройство, поэтому тик не выдаёт пакет,
        /// а доливает буфер до целевого запаса.
        /// </summary>
        public void Playback()
        {
            var userVolumeMultiplier = _usersInfo.GetVolumeByUserId(_userId) / 50f;
            if (_appliedVolume < 0)
                _appliedVolume = userVolumeMultiplier;

            bool empty;
            int spread;
            lock (_queueLock)
            {
                empty = _queued == 0;
                spread = _delaySpread.Spread(DelaySpread.NowMilliseconds());
            }
            _targetBufferFrames = Math.Clamp(spread / FRAME_MILLISECONDS + 2, MIN_BUFFER_FRAMES, MAX_BUFFER_FRAMES);

            // Счёт ведётся по приходу пакетов, а не по факту выдачи: при полном
            // буфере тик ничего не берёт из очереди, и это не потеря.
            if (empty)
                ConsecutiveLosses++;
            else
                ConsecutiveLosses = 0;

            while (WaveProvider.BufferedBytes / FRAME_BYTES < _targetBufferFrames)
            {
                bool present;
                PooledBuffer packet = default;

                lock (_queueLock)
                {
                    present = _filled[_next % SlotCount];

                    if (!present && WaveProvider.BufferedBytes / FRAME_BYTES >= LOW_WATER_FRAMES)
                        break;

                    if (present || _queued > 0)
                    {
                        TakeNext(out packet);
                    }
                }

                if (present)
                {
                    try
                    {
                        _decoder.Decode(packet.Span, _pcm);
                    }
                    finally
                    {
                        packet.Return();
                    }

                    _usersInfo.UpdateSpeakingState(_userId, _speechDetector.IsSpeech(_pcm));
                    _hasPlayed = true;
                    _concealed = 0;

                    int length = _pcm.Length;
                    bool compress = !_compressedLast
                        && WaveProvider.BufferedBytes / FRAME_BYTES + Volatile.Read(ref _queued) > _targetBufferFrames + 1;
                    if (compress)
                        length = FrameCompressor.Compress(MemoryMarshal.Cast<byte, short>(_pcm.AsSpan())) * 2;
                    _compressedLast = length < _pcm.Length;

                    ApplyVolume(userVolumeMultiplier, length);
                    WaveProvider.AddSamples(_pcm, 0, length);
                    continue;
                }

                // Потеря посреди речи не гасит индикатор.
                if (_hasPlayed && _concealed++ < MAX_CONCEALED_FRAMES)
                {
                    _decoder.DecodePLC(_pcm);
                    ApplyVolume(userVolumeMultiplier, _pcm.Length);
                    WaveProvider.AddSamples(_pcm, 0, _pcm.Length);
                }
                else
                {
                    _usersInfo.UpdateSpeakingState(_userId, false);
                    WaveProvider.AddSamples(_silence, 0, _silence.Length);
                }
            }

        }

        public void Dispose()
        {
            WasapiOut?.Stop();
            WasapiOut?.Dispose();
            Device?.Dispose();
            _decoder?.Dispose();
            _speechDetector.Dispose();

            lock (_queueLock)
                Clear();

            GC.SuppressFinalize(this);
        }
    }
}