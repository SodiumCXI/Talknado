using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.IO;
using System.Windows;
using Talknado.Client.Models.Helpers;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.Models;

public interface IAudioManager
{
    void ToggleMicrophoneStatus();
}

public partial class AudioManager : ObservableObject, IAudioManager, IDisposable
{
    private readonly INetworkUtils _networkUtils;
    private readonly ICryptoSessionManager _cryptoSessionManager;
    private readonly IConnectionInfo _connectionInfo;
    private readonly IUsersAudioPlayer _usersAudioPlayer;
    private readonly ISettingsManager _settingsManager;
    private readonly IScreenSharePlayer _screenSharePlayer;

    private readonly CancellationTokenSource _receiveCancellationTokenSource;
    private CancellationTokenSource? _sendCancellationTokenSource;

    private readonly Thread _audioReceiveThread;
    private Thread? _audioSendThread;

    private readonly OpusCodecEncoder _encoder = new(false);

    [ObservableProperty]
    private bool _isMicrophoneActive = false;

    public AudioManager(INetworkUtils networkUtils,
        ICryptoSessionManager cryptoSessionManager,
        IConnectionInfo connectionInfo,
        IUsersAudioPlayer usersAudioPlayer,
        ISettingsManager settingsManager,
        IScreenSharePlayer screenSharePlayer)
    {
        _networkUtils = networkUtils;
        _cryptoSessionManager = cryptoSessionManager;
        _connectionInfo = connectionInfo;
        _usersAudioPlayer = usersAudioPlayer;
        _settingsManager = settingsManager;
        _screenSharePlayer = screenSharePlayer;

        _settingsManager.InputDeviceChanged += HandleInputDeviceChanged;

        _receiveCancellationTokenSource = new CancellationTokenSource();
        _audioReceiveThread = new(() => HandleReceiveAudio(_receiveCancellationTokenSource.Token))
        {
            IsBackground = true
        };
        _audioReceiveThread.Start();

        LoopbackAudioCapture.SendOpusData = SendScreenShareOpusData;
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
        var deviceId = ResolveDeviceId(_settingsManager.SelectedInputDevice);
        _sendCancellationTokenSource = new CancellationTokenSource();
        _audioSendThread = new(() => HandleSendAudio(deviceId, _sendCancellationTokenSource.Token))
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

    private static string? ResolveDeviceId(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName) || deviceName == Strings.DefaultDeviceText)
            return null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            foreach (var device in devices)
            {
                if (device.FriendlyName == deviceName)
                {
                    return device.ID;
                }
            }
        }
        catch { /* ignore */ }

        return null;
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
                var opusPacket = _cryptoSessionManager.DecryptMessage(packet);

                SplitAudioPacket(opusPacket, out byte[] opusData, out ushort userId);

                if (userId == 0 && !_screenSharePlayer.IsWindowVisible)
                    continue;

                _usersAudioPlayer.Play(userId, opusData);
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                return;
            }
            catch { /* ignore */ }
        }
    }

    private void HandleSendAudio(string? deviceId, CancellationToken token)
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiCapture? capture = null;

        try
        {
            enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            if (devices.Count == 0)
            {
                IsMicrophoneActive = false;
                MessageBox.Show(Strings.MicrophoneNotDetectedText, Strings.MicrophoneErrorText,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrEmpty(deviceId))
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            else
            {
                device = enumerator.GetDevice(deviceId);
            }

            if (device == null)
            {
                IsMicrophoneActive = false;
                MessageBox.Show(Strings.MicrophoneNotDetectedText, Strings.MicrophoneErrorText,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            capture = new WasapiCapture(device);

            int targetSampleRate = 48000;
            int sourceSampleRate = capture.WaveFormat.SampleRate;
            int sourceChannels = capture.WaveFormat.Channels;

            const int frameBytes = 960;

            var audioBuffer = new List<byte>();

            capture.DataAvailable += (s, e) =>
            {
                if (token.IsCancellationRequested) return;

                try
                {
                    using var ms = new MemoryStream(e.Buffer, 0, e.BytesRecorded);
                    using var rawSource = new RawSourceWaveStream(ms, capture.WaveFormat);

                    var provider = rawSource.ToSampleProvider();

                    if (sourceChannels > 1)
                        provider = provider.ToMono();

                    if (sourceSampleRate != targetSampleRate)
                        provider = new WdlResamplingSampleProvider(provider, targetSampleRate);

                    float[] chunk = new float[4096];
                    int read;
                    while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            short pcmSample = (short)Math.Clamp(chunk[i] * 32767f, -32768f, 32767f);
                            audioBuffer.Add((byte)(pcmSample & 0xFF));
                            audioBuffer.Add((byte)(pcmSample >> 8));
                        }
                    }

                    while (audioBuffer.Count >= frameBytes)
                    {
                        byte[] frameData = [.. audioBuffer.Take(frameBytes)];
                        audioBuffer.RemoveRange(0, frameBytes);

                        var denoisedData = NoiseSuppressor.Denoise(frameData);
                        var opusData = _encoder.Encode(denoisedData);
                        SendOpusPacket(opusData, _connectionInfo.LocalUserId);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Audio error: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
                }
            };

            capture.StartRecording();
            token.WaitHandle.WaitOne();
        }
        catch (Exception ex)
        {
            IsMicrophoneActive = false;
            MessageBox.Show($"{Strings.MicrophoneUnableText}\n\n{ex.Message}",
                Strings.MicrophoneErrorText, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            capture?.StopRecording();
            capture?.Dispose();
            device?.Dispose();
            enumerator?.Dispose();
        }
    }

    private void SendScreenShareOpusData(byte[] audioData)
    {
        SendOpusPacket(audioData, 0);
    }

    private void SendOpusPacket(byte[] opusData, ushort userId)
    {
        var opusPacket = AddIdToAudioPacket(opusData, userId);
        var encryptedOpusPacket = _cryptoSessionManager.EncryptMessage(opusPacket);

        try
        {
            _networkUtils.SendAudioPacketAsync(encryptedOpusPacket).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        if (userId != 0)
        {
            _usersAudioPlayer.Play(userId, opusData);
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

    private static void SplitAudioPacket(byte[] audioPacket, out byte[] audioData, out ushort userId)
    {
        userId = BitConverter.ToUInt16(audioPacket, 0);

        var audioDataLength = audioPacket.Length - sizeof(ushort);
        audioData = new byte[audioDataLength];

        Array.Copy(audioPacket, sizeof(ushort), audioData, 0, audioDataLength);
    }

    public void Dispose()
    {
        StopRecording();

        _receiveCancellationTokenSource.Cancel();
        _audioReceiveThread.Join();
        _receiveCancellationTokenSource.Dispose();

        _encoder.Dispose();

        GC.SuppressFinalize(this);
    }
}