using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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
        if (string.IsNullOrEmpty(deviceName) || deviceName == Strings.DefaultDeviceText)
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

    private void HandleSendAudio(int deviceIndex, CancellationToken token)
    {
        if (WaveInEvent.DeviceCount == 0)
        {
            IsMicrophoneActive = false;
            MessageBox.Show(Strings.MicrophoneNotDetectedText, Strings.MicrophoneErrorText,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        int targetSampleRate = 48000;
        int microphoneSampleRate = targetSampleRate;

        try
        {
            using var testWaveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(targetSampleRate, 16, 1)
            };
        }
        catch
        {
            using var tempWaveIn = new WaveInEvent { DeviceNumber = deviceIndex };
            microphoneSampleRate = tempWaveIn.WaveFormat.SampleRate;
        }

        using var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(microphoneSampleRate, 16, 1),
            BufferMilliseconds = 10
        };

        BufferedWaveProvider? bufferProvider = null;
        WdlResamplingSampleProvider? resampler = null;

        if (microphoneSampleRate != targetSampleRate)
        {
            bufferProvider = new BufferedWaveProvider(waveIn.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferLength = waveIn.WaveFormat.AverageBytesPerSecond * 2
            };

            resampler = new WdlResamplingSampleProvider(
                bufferProvider.ToSampleProvider(),
                targetSampleRate
            );
        }

        void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (token.IsCancellationRequested) return;

            try
            {
                byte[] audioData;

                if (resampler != null && bufferProvider != null)
                {
                    bufferProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);

                    int inputSamples = e.BytesRecorded / waveIn.WaveFormat.BlockAlign;
                    int outputSamples = (int)(inputSamples * (long)targetSampleRate / microphoneSampleRate) + 100;
                    float[] floatBuffer = new float[outputSamples];

                    int samplesRead = resampler.Read(floatBuffer, 0, outputSamples);

                    if (samplesRead == 0) return;

                    audioData = new byte[samplesRead * 2];
                    for (int i = 0; i < samplesRead; i++)
                    {
                        short pcmSample = (short)Math.Clamp(floatBuffer[i] * 32767f, -32768f, 32767f);
                        audioData[i * 2] = (byte)(pcmSample & 0xFF);
                        audioData[i * 2 + 1] = (byte)(pcmSample >> 8);
                    }
                }
                else
                {
                    audioData = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, audioData, e.BytesRecorded);
                }

                var denoisedData = NoiseSuppressor.Denoise(audioData);
                var opusData = _encoder.Encode(denoisedData);
                SendOpusPacket(opusData, _connectionInfo.LocalUserId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio processing error: {ex.Message}");
            }
        }

        waveIn.DataAvailable += OnDataAvailable;

        try
        {
            waveIn.StartRecording();
            token.WaitHandle.WaitOne();
        }
        catch
        {
            IsMicrophoneActive = false;
            MessageBox.Show(Strings.MicrophoneUnableText, Strings.MicrophoneErrorText,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.StopRecording();
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