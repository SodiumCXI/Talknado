using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Talknado.Client.Models.Helpers.Audio;

public class WasapiLoopbackCaptureWithProcessExclusion : IDisposable
{
    private readonly WasapiLoopbackCapture _audioCapture;
    private readonly MMDevice _audioDevice;
    private readonly int _currentProcessId;
    private bool _disposed;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat
    {
        get => _audioCapture?.WaveFormat!;
        set
        {
            if (_audioCapture != null)
                _audioCapture.WaveFormat = value;
        }
    }

    public WasapiLoopbackCaptureWithProcessExclusion()
    {
        _currentProcessId = Environment.ProcessId;

        var enumerator = new MMDeviceEnumerator();
        _audioDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        _audioCapture = new WasapiLoopbackCapture(_audioDevice);

        _audioCapture.DataAvailable += OnDataAvailable!;
        _audioCapture.RecordingStopped += (s, e) => RecordingStopped?.Invoke(this, e);
    }

    public WasapiLoopbackCaptureWithProcessExclusion(MMDevice device)
    {
        _currentProcessId = Environment.ProcessId;
        _audioDevice = device ?? throw new ArgumentNullException(nameof(device));

        _audioCapture = new WasapiLoopbackCapture(_audioDevice);

        _audioCapture.DataAvailable += OnDataAvailable!;
        _audioCapture.RecordingStopped += (s, e) => RecordingStopped?.Invoke(this, e);
    }

    private void OnDataAvailable(object sender, WaveInEventArgs e)
    {
        if (IsOurProcessPlayingAudio())
            return;

        DataAvailable?.Invoke(this, e);
    }

    private bool IsOurProcessPlayingAudio()
    {
        try
        {
            var sessionManager = _audioDevice.AudioSessionManager;
            var sessions = sessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                try
                {
                    if (session.GetProcessID == _currentProcessId)
                    {
                        float peak = session.AudioMeterInformation.MasterPeakValue;

                        if (peak > 0.01)
                            return true;
                    }
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    public void StartRecording()
    {
        _audioCapture?.StartRecording();
    }

    public void StopRecording()
    {
        _audioCapture?.StopRecording();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        if (_audioCapture != null)
        {
            _audioCapture.DataAvailable -= OnDataAvailable!;
            _audioCapture.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}