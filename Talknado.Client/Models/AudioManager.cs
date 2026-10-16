using System.Buffers.Binary;
using System.Runtime.InteropServices;
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

    private const int HeaderSize = 2 * sizeof(ushort);

    private ushort _micSequence;
    private ushort _systemSequence;
    private readonly ISettingsManager _settingsManager;
    private readonly IScreenSharePlayer _screenSharePlayer;

    private readonly CancellationTokenSource _receiveCancellationTokenSource;
    private CancellationTokenSource? _sendCancellationTokenSource;

    private readonly Thread _audioReceiveThread;
    private Thread? _audioSendThread;

    private readonly OpusCodecEncoder _encoder = new(false);

    private const int CaptureBufferMilliseconds = 20;

    private const int FrameSamples = 480;
    // Несколько кадров: потеря одного не оставит получателя с оборванным звуком.
    private const int EndSilenceFrames = 3;

    [ObservableProperty]
    private bool _isMicrophoneActive = false;

    // Поток захвата читает на каждом кадре, кнопка меняет из потока UI.
    private volatile bool _sendRequested;

    partial void OnIsMicrophoneActiveChanged(bool value) => _sendRequested = value;

    // Опрос состояния, а не перехват клавиатуры: виден и когда окно не в фокусе.
    // Кадр идёт раз в 10 мс, этого хватает.
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    private bool IsSendAllowed()
    {
        if (!_sendRequested)
            return false;
        if (!_settingsManager.PushToTalk)
            return true;
        int key = _settingsManager.PushToTalkKey;
        return key != 0 && (GetAsyncKeyState(key) & 0x8000) != 0;
    }

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

    // Микрофон открыт всю сессию, кнопка только разрешает отправку: повторное
    // открытие устройства съедало начало фразы, а шумоподавление выдавало хвост
    // прошлого включения.
    public void ToggleMicrophoneStatus()
    {
        IsMicrophoneActive = !IsMicrophoneActive;

        if (IsMicrophoneActive && _audioSendThread is not { IsAlive: true })
            StartRecording();
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
        if (_audioSendThread is { IsAlive: true })
        {
            StopRecording();
            StartRecording();
        }
    }

    // Пакет: номер участника, номер пакета и Opus. Поток приёма один, поэтому
    // буфер под расшифровку общий на все пакеты.
    private void HandleReceiveAudio(CancellationToken token)
    {
        var plain = new byte[CryptoSessionManager.EncryptedLength(HeaderSize + OpusCodecEncoder.MaxPacketSize)];

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

                if (length <= HeaderSize)
                    continue;

                ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(sizeof(ushort)));
                _usersAudioPlayer.Play(userId, sequence, plain.AsSpan(HeaderSize, length - HeaderSize));
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
        var dataReady = new AutoResetEvent(false);

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

            // Обработка — не в обработчике захвата: там она задерживает чтение
            // устройства, и буфер захвата переполняется.
            capture.DataAvailable += (s, e) =>
            {
                captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                dataReady.Set();
            };

            // Пока отправка запрещена, кадры не обрабатываются. Выход шумоподавления
            // отстаёт на DelayFrames, поэтому после выключения уходит ещё столько кадров:
            // это звук до нажатия. Последний затухает, следом тишина. При включении
            // шумоподавление и кодировщик начинают заново, первый кадр нарастает.
            bool sending = false;
            int tailFrames = 0;

            void SendFrame(Ramp ramp)
            {
                for (int i = 0; i < FrameSamples; i++)
                {
                    short pcmSample = (short)Math.Clamp(frameSamples[i] * 32767f, -32768f, 32767f);
                    frameData[i * 2] = (byte)(pcmSample & 0xFF);
                    frameData[i * 2 + 1] = (byte)(pcmSample >> 8);
                }

                var denoisedData = NoiseSuppressor.Denoise(frameData);
                if (ramp != Ramp.None)
                    ApplyRamp(denoisedData, ramp);
                int opusLength = _encoder.Encode(denoisedData, opusData);
                SendOpusPacket(opusData.AsSpan(0, opusLength), _connectionInfo.LocalUserId);
            }

            void SendSilence()
            {
                Array.Clear(frameData);
                for (int i = 0; i < EndSilenceFrames; i++)
                {
                    int opusLength = _encoder.Encode(frameData, opusData);
                    SendOpusPacket(opusData.AsSpan(0, opusLength), _connectionInfo.LocalUserId);
                }
            }

            void ProcessFrame()
            {
                bool requested = IsSendAllowed();

                // Без шумоподавления задержки нет: затухает первый кадр после нажатия.
                if (sending && !requested)
                {
                    sending = false;
                    tailFrames = Math.Max(1, NoiseSuppressor.DelayFrames);
                }

                if (tailFrames > 0)
                {
                    tailFrames--;
                    SendFrame(tailFrames == 0 ? Ramp.Out : Ramp.None);
                    if (tailFrames == 0)
                        SendSilence();
                    return;
                }

                if (!requested)
                    return;

                if (!sending)
                {
                    NoiseSuppressor.Reset();
                    _encoder.Reset();
                    sending = true;
                    SendFrame(Ramp.In);
                    return;
                }

                SendFrame(Ramp.None);
            }

            capture.StartRecording();

            var waitHandles = new[] { dataReady, token.WaitHandle };
            while (WaitHandle.WaitAny(waitHandles) == 0)
            {
                try
                {
                    int read;
                    while ((read = provider.Read(frameSamples, filled, FrameSamples - filled)) > 0)
                    {
                        filled += read;

                        if (filled < FrameSamples)
                            continue;

                        filled = 0;
                        ProcessFrame();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Audio error: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
                }
            }

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
            dataReady.Dispose();
            device?.Dispose();
            enumerator?.Dispose();
        }
    }

    private enum Ramp { None, In, Out }

    private static void ApplyRamp(Span<byte> pcm, Ramp ramp)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        for (int i = 0; i < samples.Length; i++)
        {
            float gain = (float)i / samples.Length;
            samples[i] = (short)(samples[i] * (ramp == Ramp.In ? gain : 1f - gain));
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
        ushort sequence = userId == 0 ? _systemSequence++ : _micSequence++;

        Span<byte> packet = stackalloc byte[HeaderSize + OpusCodecEncoder.MaxPacketSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, userId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[sizeof(ushort)..], sequence);
        opusData.CopyTo(packet[HeaderSize..]);
        packet = packet[..(HeaderSize + opusData.Length)];

        Span<byte> encrypted = stackalloc byte[CryptoSessionManager.EncryptedLength(HeaderSize + OpusCodecEncoder.MaxPacketSize)];
        int length = _cryptoSessionManager.Encrypt(packet, encrypted);

        try
        {
            _networkUtils.SendAudioPacket(encrypted[..length]);
        }
        catch { /* ignore */ }

        if (userId != 0)
        {
            _usersAudioPlayer.Play(userId, sequence, opusData);
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