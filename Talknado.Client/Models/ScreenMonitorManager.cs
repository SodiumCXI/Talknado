using CommunityToolkit.Mvvm.ComponentModel;
using SharpDX;
using SharpDX.DXGI;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Talknado.Client.Models.Helpers.ScreenShare;


namespace Talknado.Client.Models;

public interface IScreenMonitorManager
{
    void CaptureAll();
    void BuildPreviews();
    void ClearMonitors();
    bool IsWindowVisible { get; set; }
    int GridColumns { get; set; }
    ObservableCollection<ScreenMonitorManager.MonitorSnapshot> Monitors { get; }
    ScreenMonitorManager.MonitorSnapshot? SelectedMonitor { get; set; }
}

public partial class ScreenMonitorManager : ObservableObject, IScreenMonitorManager
{
    [ObservableProperty]
    private bool _isWindowVisible;
    [ObservableProperty]
    private int _gridColumns = 1;
    [ObservableProperty]
    private ObservableCollection<MonitorSnapshot> _monitors = [];
    public MonitorSnapshot? SelectedMonitor { get; set; }

    public void CaptureAll()
    {
        var factory = new Factory1();
        var snapshots = new ObservableCollection<MonitorSnapshot>();
        int monitorNumber = 1;

        for (int ai = 0; ai < factory.GetAdapterCount1(); ai++)
        {
            using var adapter = factory.GetAdapter1(ai);

            if (adapter.Description1.Flags.HasFlag(AdapterFlags.Software))
                continue;

            int oi = 0;
            while (true)
            {
                Output? output = null;
                try
                {
                    output = adapter.GetOutput(oi);
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == ResultCode.NotFound.Code)
                {
                    break;
                }

                using (output)
                {
                    try
                    {
                        ScreenGrabber.SelectMonitor(ai, oi);

                        byte[]? frameData = null;
                        int w = 0, h = 0;
                        const int maxAttempts = 10;

                        for (int attempt = 0; attempt < maxAttempts && frameData == null; attempt++)
                            frameData = ScreenGrabber.CaptureFrame(out w, out h);

                        if (frameData == null)
                            continue;

                        snapshots.Add(new MonitorSnapshot(monitorNumber++, ai, oi, frameData, w, h));
                    }
                    catch { /* ignore */ }
                }
                oi++;
            }
        }

        factory.Dispose();
        Monitors = snapshots;

        if (Monitors.Count == 1)
            SelectedMonitor = Monitors[0];
    }

    public void BuildPreviews()
    {
        foreach (var snapshot in Monitors)
            snapshot.BuildPreview();
    }

    public void ClearMonitors()
    {
        Monitors = [];
    }

    public record MonitorSnapshot(
        int MonitorNumber,
        int AdapterIndex,
        int OutputIndex,
        byte[] FrameData,
        int FrameWidth,
        int FrameHeight)
    {
        public double AspectRatio => FrameHeight > 0 ? (double)FrameWidth / FrameHeight : 1.0;

        private WriteableBitmap? _bitmap;
        public ImageSource? Preview => _bitmap;

        public void BuildPreview()
        {
            _bitmap = new WriteableBitmap(FrameWidth, FrameHeight, 96, 96, PixelFormats.Bgr32, null);
            _bitmap.Lock();
            _bitmap.WritePixels(new Int32Rect(0, 0, FrameWidth, FrameHeight), FrameData, FrameWidth * 4, 0);
            _bitmap.Unlock();
        }
    }
}