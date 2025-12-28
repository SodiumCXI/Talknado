using NAudio.Wave;
using System.Collections.Concurrent;

namespace Talknado.Client.Models
{
    public interface IUsersAudioPlayer
    {
        event Action<ushort>? UserAdded;
        event Action<ushort>? UserRemoved;
        void Play(ushort userId, byte[]? audioData);
    }

    public class UsersAudioPlayer : IUsersAudioPlayer, IDisposable
    {
        private readonly ConcurrentDictionary<ushort, UserAudioStream> _userAudioStreams = [];
        private readonly TimeSpan _streamTimeout = TimeSpan.FromMilliseconds(150);
        private readonly Timer _cleanupTimer;

        public event Action<ushort>? UserAdded;
        public event Action<ushort>? UserRemoved;

        private readonly ISettingsManager _settingsManager;

        public UsersAudioPlayer(ISettingsManager settingsManager)
        {
            _settingsManager = settingsManager;

            _cleanupTimer = new(CheckInactiveStreams, null, 0, 200);
            _cleanupTimer.ConfigureAwait(false);

            _settingsManager.OutputDeviceChanged += HandleOutputDeviceChanged;
        }

        public void Play(ushort userId, byte[]? audioData)
        {
            if (!_userAudioStreams.TryGetValue(userId, out UserAudioStream? value))
            {
                var deviceIndex = ResolveDeviceIndex(_settingsManager.SelectedInputDevice);
                value = new UserAudioStream(deviceIndex);
                _userAudioStreams[userId] = value;

                UserAdded?.Invoke(userId);
            }
            if (audioData != null)
                value.AddAudio(audioData);
            else
                value.UpdateLastActiveTime();
        }

        private void RemoveUserStream(ushort userId)
        {
            if (_userAudioStreams.TryRemove(userId, out var stream))
            {
                stream.Dispose();

                UserRemoved?.Invoke(userId);
            }
        }

        private void CheckInactiveStreams(object? state)
        {
            var now = DateTime.UtcNow;
            var inactive = _userAudioStreams
                .Where(kvp => now - kvp.Value.LastActiveTime > _streamTimeout)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var userId in inactive)
                RemoveUserStream(userId);
        }

        private static int ResolveDeviceIndex(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName) || deviceName == "Устройство по умолчанию")
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
            {
                RemoveUserStream(userId);
            }
        }

        public void Dispose()
        {
            _cleanupTimer.Dispose();

            UserAdded = null;
            UserRemoved = null;

            GC.SuppressFinalize(this);
        }

        public class UserAudioStream : IDisposable
        {
            public WaveOutEvent WaveOut { get; }
            public BufferedWaveProvider WaveProvider { get; }
            public DateTime LastActiveTime { get; private set; }

            public UserAudioStream(int deviceIndex)
            {
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

                LastActiveTime = DateTime.UtcNow;
            }

            public void AddAudio(byte[] data)
            {
                WaveProvider.AddSamples(data, 0, data.Length);
                LastActiveTime = DateTime.UtcNow;
            }

            public void UpdateLastActiveTime()
            {
                LastActiveTime = DateTime.UtcNow;
            }

            public void Dispose()
            {
                WaveOut.Stop();
                WaveOut.Dispose();

                GC.SuppressFinalize(this);
            }
        }
    }
}
