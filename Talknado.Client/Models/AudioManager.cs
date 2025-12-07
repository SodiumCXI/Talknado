using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Wave;
using Talknado.Client.Models.Helpers;
using Talknado.Client.Models.Helpers.Audio;

namespace Talknado.Client.Models
{
    public interface IAudioManager
    {
        void ToggleMicrophoneStatus();

    }
    public partial class AudioManager : ObservableObject, IAudioManager, IDisposable
    {
        private readonly INetworkUtils _networkUtils;
        private readonly IUsersInfo _usersInfo;
        private readonly ICryptoSessionManager _cryptoSessionManager;
        private readonly IConnectionInfo _connectionInfo;
        private readonly IUsersAudioPlayer _usersAudioPlayer;
        private readonly ISettingsManager _settingsManager;

        private readonly CancellationTokenSource _receiveCancellationTokenSource;
        private CancellationTokenSource? _sendCancellationTokenSource;

        private readonly Thread _audioReceiveThread;
        private Thread? _audioSendThread;

        [ObservableProperty]
        private bool _isMicrophoneActive = false;

        public AudioManager(INetworkUtils networkUtils,
            IUsersInfo usersInfo,
            ICryptoSessionManager cryptoSessionManager,
            IConnectionInfo connectionInfo,
            IUsersAudioPlayer usersAudioPlayer,
            ISettingsManager settingsManager)
        {
            _networkUtils = networkUtils;
            _usersInfo = usersInfo;
            _cryptoSessionManager = cryptoSessionManager;
            _connectionInfo = connectionInfo;
            _usersAudioPlayer = usersAudioPlayer;
            _settingsManager = settingsManager;

            _settingsManager.InputDeviceChanged += HandleInputDeviceChanged;

            _receiveCancellationTokenSource = new CancellationTokenSource();
            _audioReceiveThread = new(() => HandleReceiveAudio(_receiveCancellationTokenSource.Token))
            {
                IsBackground = true
            };
            _audioReceiveThread.Start();
        }

        public void ToggleMicrophoneStatus()
        {
            IsMicrophoneActive = !IsMicrophoneActive;

            if (IsMicrophoneActive)
            {
                StartRecording();
            }
            else
            {
                StopRecording();
            }
        }

        private void StartRecording()
        {
            var deviceIndex = ResolveDeviceIndex(_settingsManager.SelectedInputDevice);
            _sendCancellationTokenSource = new CancellationTokenSource();
            _audioSendThread = new(() => HandleSendAudio(deviceIndex, _sendCancellationTokenSource.Token))
            {
                IsBackground = true
            };
            _audioSendThread.Start();
        }

        private void StopRecording()
        {
            _sendCancellationTokenSource?.Cancel();
            _audioSendThread?.Join();
            _sendCancellationTokenSource?.Dispose();
            _sendCancellationTokenSource = null;
        }

        private static int ResolveDeviceIndex(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName) || deviceName == "Устройство по умолчанию")
                return -1;

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var caps = WaveIn.GetCapabilities(i);
                if (caps.ProductName == deviceName)
                    return i;
            }

            return -1;
        }

        private void HandleInputDeviceChanged()
        {
            if (IsMicrophoneActive)
            {
                StopRecording();
                StartRecording();
            }
        }

        private void HandleReceiveAudio(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var packet = _networkUtils.ReceiveAudioPacketAsync(token).GetAwaiter().GetResult();

                    byte[] decryptedData = _cryptoSessionManager.DecryptMessage(packet);
                    SplitAudioPacket(decryptedData, out byte[] audioData, out ushort userId);

                    if (AdjustTrackBarVolume(audioData, userId))
                        _usersAudioPlayer.Play(userId, audioData);
                    else
                        _usersAudioPlayer.Play(userId, null);
                }
                catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
                {
                    return;
                }
                catch { /* ignore */ }
            }
        }

        private void HandleSendAudio(int deviceIndex, CancellationToken token)
        {
            using var waveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 10
            };

            void OnDataAvailable(object? sender, WaveInEventArgs e)
            {
                if (token.IsCancellationRequested) return;

                var microphoneData = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, microphoneData, e.BytesRecorded);

                var audioData = NoiseSuppressor.Denoise(microphoneData);

                var audioPacket = AddIdToAudioPacket(audioData, _connectionInfo.LocalUserId);
                var encryptedAudioPacket = _cryptoSessionManager.EncryptMessage(audioPacket);

                try
                {
                    _networkUtils.SendAudioPacketAsync(encryptedAudioPacket).GetAwaiter().GetResult();
                }
                catch { /* ignore */ }

                if (AdjustTrackBarVolume(audioData, _connectionInfo.LocalUserId))
                    _usersAudioPlayer.Play(_connectionInfo.LocalUserId, audioData);
                else
                    _usersAudioPlayer.Play(_connectionInfo.LocalUserId, null);
            }

            waveIn.DataAvailable += OnDataAvailable;
            waveIn.StartRecording();

            try
            {
                token.WaitHandle.WaitOne();
            }
            finally
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.StopRecording();
            }
        }

        private static byte[] AddIdToAudioPacket(byte[] audioData, ushort userId)
        {
            var userIdBytes = BitConverter.GetBytes(userId);
            var result = new byte[userIdBytes.Length + audioData.Length];

            Array.Copy(userIdBytes, 0, result, 0, userIdBytes.Length);
            Array.Copy(audioData, 0, result, userIdBytes.Length, audioData.Length);

            return result;
        }

        private static void SplitAudioPacket(byte[] audioPacket, out byte[] audioData, out ushort userId, int audioDataLength = 960)
        {
            if (audioPacket.Length < audioDataLength + sizeof(ushort))
                throw new ArgumentException("Invalid audio packet length.", nameof(audioPacket));

            userId = BitConverter.ToUInt16(audioPacket, 0);

            audioData = new byte[audioDataLength];
            Array.Copy(audioPacket, sizeof(ushort), audioData, 0, audioDataLength);
        }

        private bool AdjustTrackBarVolume(byte[] audioData, ushort userId)
        {
            short[] samples = new short[audioData.Length / 2];
            Buffer.BlockCopy(audioData, 0, samples, 0, audioData.Length);
            float userVolumeMultiplier = _usersInfo.GetVolumeByUserId(userId);

            if (userVolumeMultiplier == 0f)
            {
                return false;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                int adjustedSample = (int)(samples[i] * (userVolumeMultiplier / 50));

                samples[i] = (short)Math.Clamp(adjustedSample, short.MinValue, short.MaxValue);
            }

            Buffer.BlockCopy(samples, 0, audioData, 0, audioData.Length);

            return true;
        }
        public void Dispose()
        {
            StopRecording();

            _receiveCancellationTokenSource.Cancel();
            _audioReceiveThread.Join();
            _receiveCancellationTokenSource.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
