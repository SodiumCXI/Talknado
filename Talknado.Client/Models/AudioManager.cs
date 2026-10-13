using System.Buffers.Binary;
using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Windows;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Models.Helpers.Network;
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

    private const int CaptureBufferMilliseconds = 20;

    private const int FrameSamples = 480;

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

    // Пакет: номер участника (2 байта) и Opus. Поток приёма один, поэтому
    // буфер под расшифровку общий на все пакеты.
    private void HandleReceiveAudio(CancellationToken token)
    {
        var plain = new byte[CryptoSessionManager.EncryptedLength(sizeof(ushort) + OpusCodecEncoder.MaxPacketSize)];

        while (!token.IsCancellationRequested)
        {
            try
            {
                var packet = _networkUtils.ReceiveAudioPacket(token);
                int length;
                try
                {
                    if (plain.Length < packet.Length)
                        plain = new byte[packet.Length];
                    length = _cryptoSessionManager.Decrypt(packet.Span, plain);
                }
                finally
                {
                    packet.Return();
                }

                ushort userId = BinaryPrimitives.ReadUInt16LittleEndian(plain);
                if (userId == 0 && !_screenSharePlayer.IsWindowVisible)
                    continue;

                _usersAudioPlayer.Play(userId, plain.AsSpan(sizeof(ushort), length - sizeof(ushort)));
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
                Application.Current.Dispatcher.Invoke(() =>
                    MessageBox.Show(Strings.MicrophoneNotDetectedText, Strings.MicrophoneErrorText, MessageBoxButton.OK, MessageBoxImage.Error));
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
                Application.Current.Dispatcher.Invoke(() =>
                    MessageBox.Show(Strings.MicrophoneNotDetectedText, Strings.MicrophoneErrorText, MessageBoxButton.OK, MessageBoxImage.Error));
                return;
            }

            // По умолчанию WASAPI отдаёт блоки по 50 мс, и кадры уходят в сеть пачками.
            capture = new WasapiCapture(device, false, CaptureBufferMilliseconds);

            int targetSampleRate = 48000;
            int sourceSampleRate = capture.WaveFormat.SampleRate;
            int sourceChannels = capture.WaveFormat.Channels;

            // Цепочка одна на весь захват: ресемплер хранит состояние фильтра между блоками.
            // ReadFully выключен, иначе недостающее дописывается нулями.
            var captureBuffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            ISampleProvider provider = captureBuffer.ToSampleProvider();

            if (sourceChannels > 1)
                provider = provider.ToMono();

            if (sourceSampleRate != targetSampleRate)
                provider = new WdlResamplingSampleProvider(provider, targetSampleRate);

            var frameSamples = new float[FrameSamples];
            var frameData = new byte[FrameSamples * 2];
            var opusData = new byte[OpusCodecEncoder.MaxPacketSize];
            int filled = 0;

            capture.DataAvailable += (s, e) =>
            {
                if (token.IsCancellationRequested) return;

                try
                {
                    captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

                    int read;
                    while ((read = provider.Read(frameSamples, filled, FrameSamples - filled)) > 0)
                    {
                        filled += read;

                        if (filled < FrameSamples)
                            continue;

                        filled = 0;

                        for (int i = 0; i < FrameSamples; i++)
                        {
                            short pcmSample = (short)Math.Clamp(frameSamples[i] * 32767f, -32768f, 32767f);
                            frameData[i * 2] = (byte)(pcmSample & 0xFF);
                            frameData[i * 2 + 1] = (byte)(pcmSample >> 8);
                        }

                        var denoisedData = NoiseSuppressor.Denoise(frameData);
                        int opusLength = _encoder.Encode(denoisedData, opusData);
                        SendOpusPacket(opusData.AsSpan(0, opusLength), _connectionInfo.LocalUserId);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Audio error: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
                }
            };

            NoiseSuppressor.Warmup();

            capture.StartRecording();
            token.WaitHandle.WaitOne();
        }
        catch (Exception ex)
        {
            IsMicrophoneActive = false;
            Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show($"{Strings.MicrophoneUnableText}\n\n{ex.Message}", Strings.MicrophoneErrorText, MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            capture?.StopRecording();
            capture?.Dispose();
            device?.Dispose();
            enumerator?.Dispose();
        }
    }

    private void SendScreenShareOpusData(ReadOnlySpan<byte> audioData)
    {
        SendOpusPacket(audioData, 0);
    }

    // Зовётся из потоков захвата микрофона и системного звука одновременно,
    // поэтому буферы — на стеке вызова, а не общие.
    private void SendOpusPacket(ReadOnlySpan<byte> opusData, ushort userId)
    {
        Span<byte> packet = stackalloc byte[sizeof(ushort) + OpusCodecEncoder.MaxPacketSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, userId);
        opusData.CopyTo(packet[sizeof(ushort)..]);
        packet = packet[..(sizeof(ushort) + opusData.Length)];

        Span<byte> encrypted = stackalloc byte[CryptoSessionManager.EncryptedLength(sizeof(ushort) + OpusCodecEncoder.MaxPacketSize)];
        int length = _cryptoSessionManager.Encrypt(packet, encrypted);

        try
        {
            _networkUtils.SendAudioPacket(encrypted[..length]);
        }
        catch { /* ignore */ }

        if (userId != 0)
        {
            _usersAudioPlayer.Play(userId, opusData);
        }
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