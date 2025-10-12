using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Buffers;
using Buffer = System.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Resource = SharpDX.DXGI.Resource;

namespace Talknado.Client.Models.Helpers.ScreenShare;

/// <summary>
/// Захват экрана через Desktop Duplication API (SharpDX).
/// </summary>
public static class ScreenGrabber
{
    private static Device _device;
    private static DeviceContext _context;
    private static OutputDuplication _duplicator;
    private static Texture2D? _stagingTex = null;

    static ScreenGrabber()
    {
        _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _context = _device.ImmediateContext;

        using var factory1 = new Factory1();
        using var adapter = factory1.GetAdapter1(0);
        using var output = adapter.GetOutput(0).QueryInterface<Output1>();

        _duplicator = output.DuplicateOutput(_device);
    }

    /// <summary>
    /// Захват следующего кадра.
    /// </summary>
    /// <param name="width">Ширина кадра в пикселях.</param>
    /// <param name="height">Высота кадра в пикселях.</param>
    /// <param name="stride">Шаг (RowPitch) в байтах.</param>
    /// <returns>Указатель на начало буфера B8G8R8A8 (не густой Alpha), валиден до следующего вызова.</returns>
    public static byte[] CaptureFrame(out int width, out int height, out int stride)
    {
        Resource dxgiResource = null!;

        while (true)
        {
            var result = _duplicator.TryAcquireNextFrame(100, out _, out dxgiResource);
            if (result.Success)
                break;
        }

        try
        {
            using var screenTex = dxgiResource.QueryInterface<Texture2D>();

            var fmt = screenTex.Description.Format;
            if (fmt != Format.B8G8R8A8_UNorm)
                throw new InvalidOperationException($"Unexpected format {fmt}, expected B8G8R8A8_UNorm");

            if (_stagingTex == null)
            {
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
                stride = dstStride;

                byte[] linear = ArrayPool<byte>.Shared.Rent(dstStride * height);

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
                _duplicator.ReleaseFrame();
            }
        }
        finally
        {
            dxgiResource?.Dispose();
        }
    }
}
