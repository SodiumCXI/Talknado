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
        private readonly TimeSpan _streamTimeout = TimeSpan.FromMilliseconds(100);
        private readonly Timer _cleanupTimer;

        public event Action<ushort>? UserAdded;
        public event Action<ushort>? UserRemoved;

        public UsersAudioPlayer()
        {
            _cleanupTimer = new(CheckInactiveStreams, null, 0, 1000);
            _cleanupTimer.ConfigureAwait(false);
        }

        public void Play(ushort userId, byte[]? audioData)
        {
            if (!_userAudioStreams.TryGetValue(userId, out UserAudioStream? value))
            {
                value = new UserAudioStream();
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

        public void Dispose()
        {
            _cleanupTimer.Dispose();

            UserAdded = null;
            UserRemoved = null;

            GC.SuppressFinalize(this);
        }

        internal class UserAudioStream : IDisposable
        {
            internal WaveOutEvent WaveOut { get; }
            internal BufferedWaveProvider WaveProvider { get; }
            internal DateTime LastActiveTime { get; private set; }

            internal UserAudioStream()
            {
                WaveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
                {
                    DiscardOnBufferOverflow = true
                };

                WaveOut = new WaveOutEvent();
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
            }
        }
    }
}
