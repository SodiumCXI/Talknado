using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Models.Helpers.Network;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.Models;

public interface IUsersAudioPlayer
{
    void Play(ushort userId, ReadOnlySpan<byte> opusData);
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

    public void Play(ushort userId, ReadOnlySpan<byte> opusData)
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

        value.AddPacket(opusData);
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
        private readonly Queue<PooledBuffer> _packetQueue = new();
        private readonly byte[] _pcm = new byte[OpusCodecDecoder.FrameBytes];
        private readonly object _queueLock = new();
        private readonly EnergyVad _energyVad = new();

        // Запас обязан быть больше порции, которой WasapiOut забирает звук, — всей
        // его задержки, иначе устройство голодает. Здесь вдвое больше: 20 мс против 40.
        private const int OutputLatencyMilliseconds = 20;
        private const int MIN_BUFFER_FRAMES = 4;
        private const int MAX_BUFFER_FRAMES = 20;
        private const int FRAME_BYTES = 960;

        private int _targetBufferFrames = 5;

        private int _underflowCounter = 0;
        private int _playbackCounter = 0;
        private bool _hasPlayed;


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

        public void AddPacket(ReadOnlySpan<byte> opusData)
        {
            var packet = PooledBuffer.CopyOf(opusData);

            lock (_queueLock)
            {
                _packetQueue.Enqueue(packet);

                int dropThreshold = Math.Max(_targetBufferFrames * 3, 10);

                while (_packetQueue.Count > dropThreshold)
                {
                    _packetQueue.Dequeue().Return();
                }
            }
        }

        /// <summary>
        /// Скорость воспроизведения задаёт устройство, поэтому тик не выдаёт пакет,
        /// а доливает буфер до целевого запаса.
        /// </summary>
        public void Playback()
        {
            var userVolumeMultiplier = _usersInfo.GetVolumeByUserId(_userId) / 50f;

            bool queueEmpty;
            lock (_queueLock)
                queueEmpty = _packetQueue.Count == 0;

            // Счёт ведётся по приходу пакетов, а не по факту выдачи: при полном
            // буфере тик ничего не берёт из очереди, и это не потеря.
            if (queueEmpty)
                ConsecutiveLosses++;
            else
                ConsecutiveLosses = 0;

            _playbackCounter++;

            if (_playbackCounter >= 100)
            {
                AdaptBufferSize();
                _playbackCounter = 0;
                _underflowCounter = 0;
            }

            while (WaveProvider.BufferedBytes / FRAME_BYTES < _targetBufferFrames)
            {
                bool dequeued;
                PooledBuffer packet;

                lock (_queueLock)
                    dequeued = _packetQueue.TryDequeue(out packet);

                if (dequeued)
                {
                    try
                    {
                        _decoder.Decode(packet.Span, _pcm);
                    }
                    finally
                    {
                        packet.Return();
                    }

                    _usersInfo.UpdateSpeakingState(_userId, _energyVad.IsSpeech(_pcm));

                    VolumeController.AdjustVolume(_pcm, userVolumeMultiplier);
                    WaveProvider.AddSamples(_pcm, 0, _pcm.Length);
                    _hasPlayed = true;
                    continue;
                }

                _underflowCounter++;
                _usersInfo.UpdateSpeakingState(_userId, false);

                // Целевой запас не набивается выдуманным звуком: один кадр PLC
                // на тик и только там, где поток уже шёл и обрыв недолгий.
                if (_hasPlayed && ConsecutiveLosses <= 10)
                {
                    _decoder.DecodePLC(_pcm);
                    VolumeController.AdjustVolume(_pcm, userVolumeMultiplier);
                    WaveProvider.AddSamples(_pcm, 0, _pcm.Length);
                }

                break;
            }
        }

        private void AdaptBufferSize()
        {
            double underflowRate = _underflowCounter / 100.0;

            if (underflowRate > 0.10)
            {
                _targetBufferFrames = Math.Min(_targetBufferFrames + 1, MAX_BUFFER_FRAMES);
            }
            else if (underflowRate == 0 && _targetBufferFrames > MIN_BUFFER_FRAMES)
            {
                _targetBufferFrames = Math.Max(_targetBufferFrames - 1, MIN_BUFFER_FRAMES);
            }
        }

        public void Dispose()
        {
            WasapiOut?.Stop();
            WasapiOut?.Dispose();
            Device?.Dispose();
            _decoder?.Dispose();

            lock (_queueLock)
            {
                while (_packetQueue.TryDequeue(out var packet))
                    packet.Return();
            }

            GC.SuppressFinalize(this);
        }
    }
}