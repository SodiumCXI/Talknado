using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = System.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Resource = SharpDX.DXGI.Resource;

namespace Talknado.Client.Models.Helpers.ScreenShare;

public static class ScreenGrabber
{
    private static Device _device = null!;
    private static DeviceContext _context = null!;
    private static OutputDuplication _duplicator = null!;
    private static Texture2D? _stagingTex = null;
    private static Factory1 _factory = null!;
    private static readonly int _adapterIndex = 0;
    private static readonly int _outputIndex = 0;

    static ScreenGrabber()
    {
        InitializeCapture();
    }

    private static void InitializeCapture()
    {
        _duplicator?.Dispose();
        _stagingTex?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _factory?.Dispose();

        _stagingTex = null;

        _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _context = _device.ImmediateContext;
        _factory = new Factory1();

        var adapter = _factory.GetAdapter1(_adapterIndex);
        var output = adapter.GetOutput(_outputIndex).QueryInterface<Output1>();

        _duplicator = output.DuplicateOutput(_device);

        adapter.Dispose();
        output.Dispose();
    }

    private static void ReinitializeIfNeeded()
    {
        try
        {
            InitializeCapture();
        }
        catch (SharpDXException ex)
        {
            throw new InvalidOperationException("Failed to reinitialize screen capture", ex);
        }
    }

    public static byte[] CaptureFrame(out int width, out int height)
    {
        Resource dxgiResource = null!;
        int retryCount = 0;
        const int maxRetries = 3;

        while (retryCount < maxRetries)
        {
            try
            {
                while (true)
                {
                    var result = _duplicator.TryAcquireNextFrame(100, out _, out dxgiResource);

                    if (result.Success)
                        break;

                    if (result.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
                    {
                        throw new SharpDXException(result);
                    }

                    if (result.Code != SharpDX.DXGI.ResultCode.WaitTimeout.Code)
                    {
                        result.CheckError();
                    }
                }

                try
                {
                    using var screenTex = dxgiResource.QueryInterface<Texture2D>();

                    var fmt = screenTex.Description.Format;
                    if (fmt != Format.B8G8R8A8_UNorm)
                        throw new InvalidOperationException($"Unexpected format {fmt}, expected B8G8R8A8_UNorm");

                    if (_stagingTex == null ||
                        _stagingTex.Description.Width != screenTex.Description.Width ||
                        _stagingTex.Description.Height != screenTex.Description.Height)
                    {
                        _stagingTex?.Dispose();

                        var desc = screenTex.Description;
                        desc.Usage = ResourceUsage.Staging;
                        desc.CpuAccessFlags = CpuAccessFlags.Read;
                        desc.BindFlags = BindFlags.None;
                        desc.OptionFlags = ResourceOptionFlags.None;
                        _stagingTex = new Texture2D(_device, desc);
                    }

                    _context.CopyResource(screenTex, _stagingTex);

                    var dataBox = _context.MapSubresource(_stagingTex, 0, MapMode.Read, MapFlags.None);
                    try
                    {
                        width = _stagingTex.Description.Width;
                        height = _stagingTex.Description.Height;
                        int srcStride = dataBox.RowPitch;
                        int dstStride = width * 4;

                        byte[] linear = new byte[dstStride * height];

                        unsafe
                        {
                            byte* srcPtr = (byte*)dataBox.DataPointer;
                            fixed (byte* dstBase = linear)
                            {
                                for (int y = 0; y < height; y++)
                                {
                                    byte* srcRow = srcPtr + y * srcStride;
                                    byte* dstRow = dstBase + y * dstStride;
                                    Buffer.MemoryCopy(srcRow, dstRow, dstStride, dstStride);
                                }
                            }
                        }

                        return linear;
                    }
                    finally
                    {
                        _context.UnmapSubresource(_stagingTex, 0);
                    }
                }
                finally
                {
                    _duplicator.ReleaseFrame();
                    dxgiResource?.Dispose();
                }
            }
            catch (SharpDXException ex) when (
                ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Code ||
                ex.ResultCode.Code == SharpDX.DXGI.ResultCode.DeviceRemoved.Code ||
                ex.ResultCode.Code == SharpDX.DXGI.ResultCode.DeviceReset.Code)
            {
                retryCount++;

                if (retryCount >= maxRetries)
                    throw new InvalidOperationException($"Failed to capture after {maxRetries} retries", ex);

                Thread.Sleep(100);

                ReinitializeIfNeeded();
                dxgiResource?.Dispose();
                dxgiResource = null!;
            }
            catch
            {
                dxgiResource?.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("Failed to capture frame");
    }

    public static void Dispose()
    {
        _duplicator?.Dispose();
        _stagingTex?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _factory?.Dispose();
    }
}