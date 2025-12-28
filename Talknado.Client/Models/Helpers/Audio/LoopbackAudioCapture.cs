using System.Runtime.InteropServices;

namespace Talknado.Client.Models.Helpers.Audio;

public partial class LoopbackAudioCapture
{
    private delegate void AudioCallback(IntPtr data, int length);

    [LibraryImport("ApplicationLoopback.dll")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static partial void SetAudioCallback(AudioCallback callback);

    [LibraryImport("ApplicationLoopback.dll")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static partial IntPtr StartCaptureAsync(uint processId, [MarshalAs(UnmanagedType.Bool)] bool includeProcessTree,
        ushort channel, uint sampleRate, ushort bitsPerSample);

    [LibraryImport("ApplicationLoopback.dll")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static partial void StopCaptureAsync();

    public static Action<byte[]> SendAudioPacket { get; set; } = null!;

    private static Thread? _captureThread;
    private static bool _isInitialized;
    private static volatile bool _isEnabled;


    public static void InitializeAudio()
    {
        if (_isInitialized)
        {
            _isEnabled = true;
            return;
        }

        _isInitialized = true;
        _isEnabled = true;

        _captureThread = new Thread(() =>
        {
            int currentProcessId = Environment.ProcessId;
            SetAudioCallback(OnAudioReceived);
            StartCaptureAsync((uint)currentProcessId, false, 1, 48000, 16);
        })
        {
            IsBackground = true
        };

        _captureThread.Start();
    }

    private static void OnAudioReceived(IntPtr data, int length)
    {
        if (!_isEnabled) return;

        try
        {
            byte[] buffer = new byte[length];
            Marshal.Copy(data, buffer, 0, length);
            SendAudioPacket?.Invoke(buffer);
        }
        catch { }
    }

    public static void Stop()
    {
        _isEnabled = false;
    }

    public static void Dispose()
    {
        StopCaptureAsync();
    }
}
