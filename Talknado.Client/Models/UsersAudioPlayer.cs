using NAudio.Wave;
using System.Collections.Concurrent;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.Models;

public interface IUsersAudioPlayer
{
    void Play(ushort userId, byte[] opusData);
}

public class UsersAudioPlayer : IUsersAudioPlayer, IDisposable
{
    private readonly IUsersInfo _usersInfo;
    private readonly ISettingsManager _settingsManager;

    private readonly ConcurrentDictionary<ushort, UserAudioStream> _userAudioStreams = [];
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

    public void Play(ushort userId, byte[] opusData)
    {
        if (!_userAudioStreams.TryGetValue(userId, out UserAudioStream? value))
        {
            var deviceIndex = ResolveDeviceIndex(_settingsManager.SelectedOutputDevice);
            value = new UserAudioStream(_usersInfo, userId, deviceIndex);
            _userAudioStreams[userId] = value;
            UserAdded?.Invoke(userId);
        }

        value.AddPacket(opusData);
    }

    private void RemoveUserStream(ushort userId)
    {
        if (_userAudioStreams.TryRemove(userId, out var stream))
        {
            stream.Dispose();
            UserRemoved?.Invoke(userId);
        }
    }

    private void PlaybackTick(object? state)
    {
        foreach (var stream in _userAudioStreams.Values)
        {
            stream.Playback();
        }

        var inactive = _userAudioStreams
            .Where(kvp => kvp.Value.ConsecutiveLosses >= 50)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var userId in inactive)
            RemoveUserStream(userId);
    }

    private static int ResolveDeviceIndex(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName) || deviceName == Strings.DefaultDeviceText)
            return -1;

        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            if (caps.ProductName == deviceName)
                return i;
        }

        return -1;
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
        private WaveOutEvent WaveOut { get; }
        private BufferedWaveProvider WaveProvider { get; }

        private readonly IUsersInfo _usersInfo;
        private readonly ushort _userId;

        private readonly OpusCodecDecoder _decoder = new();
        private readonly Queue<byte[]> _packetQueue = new();
        public int ConsecutiveLosses { get; private set; }

        public UserAudioStream(IUsersInfo usersInfo, ushort userId, int deviceIndex)
        {
            _usersInfo = usersInfo;
            _userId = userId;

            WaveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
            {
                DiscardOnBufferOverflow = true
            };

            WaveOut = new WaveOutEvent
            {
                DeviceNumber = deviceIndex
            };

            WaveOut.Init(WaveProvider);
            WaveOut.Play();
        }

        public void AddPacket(byte[] opusData)
        {
            _packetQueue.Enqueue(opusData);
        }

        public void Playback()
        {
            var userVolumeMultiplier = _usersInfo.GetVolumeByUserId(_userId) / 50f;

            if (_packetQueue.TryDequeue(out byte[]? opusData))
            {
                byte[] pcmData = _decoder.Decode(opusData);
                VolumeController.AdjustVolume(pcmData, userVolumeMultiplier);
                WaveProvider.AddSamples(pcmData, 0, pcmData.Length);
                ConsecutiveLosses = 0;
            }
            else
            {
                ConsecutiveLosses++;
                if (ConsecutiveLosses <= 5)
                {
                    byte[] plcData = _decoder.DecodePLC();
                    VolumeController.AdjustVolume(plcData, userVolumeMultiplier);
                    WaveProvider.AddSamples(plcData, 0, plcData.Length);
                }
            }
        }

        public void Dispose()
        {
            WaveOut?.Stop();
            WaveOut?.Dispose();
            _decoder?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}