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

            if (string.IsNullOrEmpty(deviceId))
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            else
                device = enumerator.GetDevice(deviceId);

            if (device == null)
            {
                IsMicrophoneActive = false;
                MessageBox.Show(Strings.MicrophoneNotDetectedText, Strings.MicrophoneErrorText,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            capture = new WasapiCapture(device);

            const int targetSampleRate = 48000;
            const int targetChannels = 1;
            const int frameBytes = 960;

            bool needsConversion = capture.WaveFormat.SampleRate != targetSampleRate ||
                                  capture.WaveFormat.Channels != targetChannels;

            BufferedWaveProvider? bufferProvider = null;
            IWaveProvider? outputProvider = null;

            if (needsConversion)
            {
                bufferProvider = new BufferedWaveProvider(capture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferLength = capture.WaveFormat.AverageBytesPerSecond
                };

                ISampleProvider sampleProvider = bufferProvider.ToSampleProvider();

                if (capture.WaveFormat.Channels > 1)
                    sampleProvider = sampleProvider.ToMono();

                if (capture.WaveFormat.SampleRate != targetSampleRate)
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, targetSampleRate);

                outputProvider = sampleProvider.ToWaveProvider16();
            }

            var audioBuffer = new List<byte>();
            var lockObj = new object();

            capture.DataAvailable += (s, e) =>
            {
                if (token.IsCancellationRequested) return;

                try
                {
                    byte[] convertedData;

                    if (needsConversion && bufferProvider != null && outputProvider != null)
                    {
                        // Используем pipeline для конвертации
                        bufferProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);

                        byte[] tempBuffer = new byte[frameBytes * 10];
                        int bytesRead = outputProvider.Read(tempBuffer, 0, tempBuffer.Length);

                        convertedData = new byte[bytesRead];
                        Buffer.BlockCopy(tempBuffer, 0, convertedData, 0, bytesRead);
                    }
                    else if (capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                    {
                        // Просто конвертируем float -> PCM16
                        int sampleCount = e.BytesRecorded / 4;
                        convertedData = new byte[sampleCount * 2];

                        for (int i = 0; i < sampleCount; i++)
                        {
                            float sample = BitConverter.ToSingle(e.Buffer, i * 4);
                            short pcm = (short)Math.Clamp(sample * 32767f, -32768f, 32767f);
                            convertedData[i * 2] = (byte)(pcm & 0xFF);
                            convertedData[i * 2 + 1] = (byte)(pcm >> 8);
                        }
                    }
                    else
                    {
                        convertedData = new byte[e.BytesRecorded];
                        Buffer.BlockCopy(e.Buffer, 0, convertedData, 0, e.BytesRecorded);
                    }

                    lock (lockObj)
                    {
                        audioBuffer.AddRange(convertedData);

                        while (audioBuffer.Count >= frameBytes)
                        {
                            byte[] frame = [.. audioBuffer.GetRange(0, frameBytes)];
                            audioBuffer.RemoveRange(0, frameBytes);

                            var denoisedData = NoiseSuppressor.Denoise(frame);
                            var opusData = _encoder.Encode(denoisedData);
                            SendOpusPacket(opusData, _connectionInfo.LocalUserId);
                        }

                        if (audioBuffer.Count > frameBytes * 10)
                        {
                            audioBuffer.RemoveRange(0, audioBuffer.Count - frameBytes * 5);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Audio error: {ex.Message}");
                }
            };

            capture.StartRecording();
            token.WaitHandle.WaitOne();
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            IsMicrophoneActive = false;
            MessageBox.Show($"{Strings.MicrophoneUnableText}\n\n{ex.Message}",
                Strings.MicrophoneErrorText, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
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