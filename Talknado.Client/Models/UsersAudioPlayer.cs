using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;
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
    private readonly Timer _playbackTimer;

    private event Action<ushort>? UserAdded;
    private event Action<ushort>? UserRemoved;

    public UsersAudioPlayer(ISettingsManager settingsManager, IUsersInfo usersInfo)
    {
        _usersInfo = usersInfo;

        _settingsManager = settingsManager;
        _playbackTimer = new(PlaybackTick, null, 0, 10);
        _settingsManager.OutputDeviceChanged += HandleOutputDeviceChanged;

        UserAdded += userId => _usersInfo.UpdateMicrophoneState(userId, true);
        UserRemoved += userId => _usersInfo.UpdateMicrophoneState(userId, false);
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
            UserAdded?.Invoke(userId);
        }

        value.AddPacket(sequence, opusData);
    }

    private void RemoveUserStream(ushort userId)
    {
        if (_userAudioStreams.TryRemove(userId, out var stream))
        {
            UpdateStreams();
            stream.Dispose();
            UserRemoved?.Invoke(userId);
        }
    }

    private void UpdateStreams()
    {
        lock (_streamsLock)
            _streams = [.. _userAudioStreams.Values];
    }

    private void PlaybackTick(object? state)
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
        foreach (var stream in _userAudioStreams.Values)
            stream.Dispose();

        _userAudioStreams.Clear();
        _playbackTimer.Dispose();
        _settingsManager.OutputDeviceChanged -= HandleOutputDeviceChanged;

        UserAdded = null;
        UserRemoved = null;

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
        private const int MAX_BUFFER_FRAMES = 20;
        private const int FRAME_BYTES = 960;

        private const int LOW_WATER_FRAMES = 2;

        // Дальше PLC тянет гул.
        private const int MAX_CONCEALED_FRAMES = 10;

        private int _targetBufferFrames = 5;
        private int _concealed;
        private int _underruns;
        private int _quietSeconds;
        private int _playbackCounter;
        private bool _hasPlayed;

        // PLC без сдвига номера растит задержку. Если 10 секунд не было опустошений,
        // а очередь всё это время не опускалась ниже N, эти N кадров лишние: они
        // выбрасываются на тихих кадрах, где пропуск не слышен. Короче окно нельзя:
        // при повторяющихся замираниях запас нужен как раз на следующее.
        private const double QuietRms = 150;
        private int _minQueued = int.MaxValue;
        private int _excessFrames;


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

                WasapiOut = new WasapiOut(Device, AudioClientShareMode.Shared, false, OutputLatencyMilliseconds);
                WasapiOut.Init(WaveProvider);
                WasapiOut.Play();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing audio output: {ex.Message}");

                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    Device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    WasapiOut = new WasapiOut(Device, AudioClientShareMode.Shared, false, OutputLatencyMilliseconds);
                    WasapiOut.Init(WaveProvider);
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

                int dropThreshold = Math.Max(_targetBufferFrames * 3, 30);
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

        /// <summary>
        /// Скорость воспроизведения задаёт устройство, поэтому тик не выдаёт пакет,
        /// а доливает буфер до целевого запаса.
        /// </summary>
        public void Playback()
        {
            var userVolumeMultiplier = _usersInfo.GetVolumeByUserId(_userId) / 50f;

            bool empty;
            lock (_queueLock)
                empty = _queued == 0;

            // Счёт ведётся по приходу пакетов, а не по факту выдачи: при полном
            // буфере тик ничего не берёт из очереди, и это не потеря.
            if (empty)
                ConsecutiveLosses++;
            else
                ConsecutiveLosses = 0;

            if (++_playbackCounter >= 100)
            {
                AdaptBufferSize();
                _playbackCounter = 0;
            }

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
                    else
                    {
                        _underruns++;
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

                    if (_excessFrames > 0 && Rms(_pcm) < QuietRms)
                    {
                        _excessFrames--;
                        continue;
                    }

                    VolumeController.AdjustVolume(_pcm, userVolumeMultiplier);
                    WaveProvider.AddSamples(_pcm, 0, _pcm.Length);
                    continue;
                }

                // Потеря посреди речи не гасит индикатор.
                if (_hasPlayed && _concealed++ < MAX_CONCEALED_FRAMES)
                {
                    _decoder.DecodePLC(_pcm);
                    VolumeController.AdjustVolume(_pcm, userVolumeMultiplier);
                    WaveProvider.AddSamples(_pcm, 0, _pcm.Length);
                }
                else
                {
                    _usersInfo.UpdateSpeakingState(_userId, false);
                    WaveProvider.AddSamples(_silence, 0, _silence.Length);
                }
            }

            lock (_queueLock)
                _minQueued = Math.Min(_minQueued, _queued);
        }

        private static double Rms(ReadOnlySpan<byte> pcm)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
            double sum = 0;
            foreach (short sample in samples)
                sum += sample * sample;
            return Math.Sqrt(sum / samples.Length);
        }

        private void AdaptBufferSize()
        {
            int underruns;
            int minQueued;
            lock (_queueLock)
            {
                underruns = _underruns;
                _underruns = 0;
                if (underruns > 0)
                    _minQueued = int.MaxValue;
                minQueued = _minQueued;
            }

            if (underruns >= 2)
            {
                _targetBufferFrames = Math.Min(_targetBufferFrames + 1, MAX_BUFFER_FRAMES);
                _quietSeconds = 0;
            }
            else if (underruns == 0 && ++_quietSeconds >= 10)
            {
                _targetBufferFrames = Math.Max(_targetBufferFrames - 1, MIN_BUFFER_FRAMES);
                _quietSeconds = 0;
                _excessFrames = minQueued == int.MaxValue ? 0 : minQueued;
                lock (_queueLock)
                    _minQueued = int.MaxValue;
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