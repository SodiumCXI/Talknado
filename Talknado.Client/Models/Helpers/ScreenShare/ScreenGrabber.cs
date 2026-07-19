using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Diagnostics;
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
    private static int _adapterIndex = 0;
    private static int _outputIndex = 0;

    private static byte[]? _cursorShapeBuffer = null;
    private static OutputDuplicatePointerShapeInformation _cursorShapeInfo;
    private static System.Drawing.Point _cursorPosition;
    private static bool _cursorVisible = false;

    private static byte[]? _lastLinearFrame = null;
    private static int _lastWidth;
    private static int _lastHeight;
    private static long _lastFrameTimestamp = 0;
    private static readonly long _forceFrameInterval = Stopwatch.Frequency; // 1 сек

    static ScreenGrabber()
    {
        _factory = new Factory1();
        InitializeCapture();
    }

    public static void SelectMonitor(int adapterIndex, int outputIndex)
    {
        _adapterIndex = adapterIndex;
        _outputIndex = outputIndex;
        InitializeCapture();
    }

    private static void InitializeCapture()
    {
        _duplicator?.Dispose();
        _stagingTex?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _stagingTex = null;
        _lastLinearFrame = null;
        _lastFrameTimestamp = 0;

        using var adapter = _factory.GetAdapter1(_adapterIndex);
        using var output = adapter.GetOutput(_outputIndex).QueryInterface<Output1>();

        _device = new Device(adapter, DeviceCreationFlags.BgraSupport);
        _context = _device.ImmediateContext;
        _duplicator = output.DuplicateOutput(_device);
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

    public static byte[]? CaptureFrame(out int width, out int height)
    {
        width = 0;
        height = 0;
        Resource? dxgiResource = null;
        int retryCount = 0;
        const int maxRetries = 3;

        while (retryCount < maxRetries)
        {
            try
            {
                if (_lastLinearFrame != null &&
                    Stopwatch.GetTimestamp() - _lastFrameTimestamp >= _forceFrameInterval)
                {
                    return BuildForcedFrame(out width, out height);
                }

                var result = _duplicator.TryAcquireNextFrame(100, out var frameInfo, out dxgiResource);

                if (!result.Success)
                {
                    if (result.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
                        throw new SharpDXException(result);

                    if (result.Code != SharpDX.DXGI.ResultCode.WaitTimeout.Code)
                        result.CheckError();

                    return null;
                }

                if (frameInfo.PointerShapeBufferSize > 0)
                    {
                        _cursorShapeBuffer = new byte[frameInfo.PointerShapeBufferSize];
                        unsafe
                        {
                            fixed (byte* bufPtr = _cursorShapeBuffer)
                            {
                                _duplicator.GetFramePointerShape(
                                    frameInfo.PointerShapeBufferSize,
                                    (nint)bufPtr,
                                    out _,
                                    out _cursorShapeInfo);
                            }
                        }
                    }

                    if (frameInfo.LastMouseUpdateTime != 0)
                    {
                        _cursorVisible = frameInfo.PointerPosition.Visible;
                        _cursorPosition = new System.Drawing.Point(
                            frameInfo.PointerPosition.Position.X,
                            frameInfo.PointerPosition.Position.Y);
                    }

                    if (frameInfo.LastPresentTime == 0)
                    {
                        _duplicator.ReleaseFrame();
                        dxgiResource?.Dispose();
                        dxgiResource = null;

                        bool cursorMoved = frameInfo.LastMouseUpdateTime != 0;
                        if (cursorMoved && _lastLinearFrame != null)
                            return BuildForcedFrame(out width, out height);

                        return null;
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
                                    Buffer.MemoryCopy(
                                        srcPtr + y * srcStride,
                                        dstBase + y * dstStride,
                                        dstStride, dstStride);
                                }
                            }
                        }

                        _lastLinearFrame = (byte[])linear.Clone();
                        _lastWidth = width;
                        _lastHeight = height;
                        _lastFrameTimestamp = Stopwatch.GetTimestamp();

                        if (_cursorVisible && _cursorShapeBuffer != null)
                            DrawCursor(linear, width, height, dstStride);

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
                dxgiResource = null;
            }
            catch
            {
                dxgiResource?.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("Failed to capture frame");
    }

    private static byte[] BuildForcedFrame(out int width, out int height)
    {
        width = _lastWidth;
        height = _lastHeight;

        var frame = (byte[])_lastLinearFrame!.Clone();

        if (_cursorVisible && _cursorShapeBuffer != null)
            DrawCursor(frame, width, height, width * 4);

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        return frame;
    }

    private static void DrawCursor(byte[] frame, int frameW, int frameH, int stride)
    {
        int left = _cursorPosition.X - _cursorShapeInfo.HotSpot.X;
        int top = _cursorPosition.Y - _cursorShapeInfo.HotSpot.Y;

        int shapeW = _cursorShapeInfo.Width;
        int shapeH = _cursorShapeInfo.Height;
        int shapePitch = _cursorShapeInfo.Pitch;

        switch ((OutputDuplicatePointerShapeType)_cursorShapeInfo.Type)
        {
            case OutputDuplicatePointerShapeType.Color:
                DrawColorCursor(frame, frameW, frameH, stride,
                    left, top, shapeW, shapeH, shapePitch);
                break;

            case OutputDuplicatePointerShapeType.Monochrome:
                DrawMonochromeCursor(frame, frameW, frameH, stride,
                    left, top, shapeW, shapeH / 2, shapePitch);
                break;

            case OutputDuplicatePointerShapeType.MaskedColor:
                DrawMaskedColorCursor(frame, frameW, frameH, stride,
                    left, top, shapeW, shapeH, shapePitch);
                break;
        }
    }

    private static void DrawColorCursor(
        byte[] frame, int frameW, int frameH, int stride,
        int left, int top, int shapeW, int shapeH, int shapePitch)
    {
        for (int cy = 0; cy < shapeH; cy++)
        {
            int fy = top + cy;
            if (fy < 0 || fy >= frameH) continue;

            for (int cx = 0; cx < shapeW; cx++)
            {
                int fx = left + cx;
                if (fx < 0 || fx >= frameW) continue;

                int srcOff = cy * shapePitch + cx * 4;
                byte srcB = _cursorShapeBuffer![srcOff];
                byte srcG = _cursorShapeBuffer[srcOff + 1];
                byte srcR = _cursorShapeBuffer[srcOff + 2];
                byte srcA = _cursorShapeBuffer[srcOff + 3];

                if (srcA == 0) continue;

                int dstOff = fy * stride + fx * 4;
                if (srcA == 255)
                {
                    frame[dstOff] = srcB;
                    frame[dstOff + 1] = srcG;
                    frame[dstOff + 2] = srcR;
                }
                else
                {
                    float a = srcA / 255f;
                    frame[dstOff] = (byte)(srcB * a + frame[dstOff] * (1f - a));
                    frame[dstOff + 1] = (byte)(srcG * a + frame[dstOff + 1] * (1f - a));
                    frame[dstOff + 2] = (byte)(srcR * a + frame[dstOff + 2] * (1f - a));
                }
            }
        }
    }

    private static void DrawMonochromeCursor(
        byte[] frame, int frameW, int frameH, int stride,
        int left, int top, int shapeW, int shapeH, int shapePitch)
    {
        for (int cy = 0; cy < shapeH; cy++)
        {
            int fy = top + cy;
            if (fy < 0 || fy >= frameH) continue;

            for (int cx = 0; cx < shapeW; cx++)
            {
                int fx = left + cx;
                if (fx < 0 || fx >= frameW) continue;

                int byteIdx = cy * shapePitch + cx / 8;
                int bitMask = 0x80 >> (cx % 8);

                bool andBit = (_cursorShapeBuffer![byteIdx] & bitMask) != 0;
                int xorByteIdx = (cy + shapeH) * shapePitch + cx / 8;
                bool xorBit = (_cursorShapeBuffer[xorByteIdx] & bitMask) != 0;

                int dstOff = fy * stride + fx * 4;

                if (!andBit && !xorBit)
                {
                    frame[dstOff] = frame[dstOff + 1] = frame[dstOff + 2] = 0;
                }
                else if (!andBit && xorBit)
                {
                    frame[dstOff] = frame[dstOff + 1] = frame[dstOff + 2] = 255;
                }
                else if (andBit && xorBit)
                {
                    frame[dstOff] ^= 0xFF;
                    frame[dstOff + 1] ^= 0xFF;
                    frame[dstOff + 2] ^= 0xFF;
                }
            }
        }
    }

    private static void DrawMaskedColorCursor(
        byte[] frame, int frameW, int frameH, int stride,
        int left, int top, int shapeW, int shapeH, int shapePitch)
    {
        for (int cy = 0; cy < shapeH; cy++)
        {
            int fy = top + cy;
            if (fy < 0 || fy >= frameH) continue;

            for (int cx = 0; cx < shapeW; cx++)
            {
                int fx = left + cx;
                if (fx < 0 || fx >= frameW) continue;

                int srcOff = cy * shapePitch + cx * 4;
                byte srcB = _cursorShapeBuffer![srcOff];
                byte srcG = _cursorShapeBuffer[srcOff + 1];
                byte srcR = _cursorShapeBuffer[srcOff + 2];
                byte srcA = _cursorShapeBuffer[srcOff + 3];

                int dstOff = fy * stride + fx * 4;

                if (srcA == 0)
                {
                    frame[dstOff] ^= srcB;
                    frame[dstOff + 1] ^= srcG;
                    frame[dstOff + 2] ^= srcR;
                }
                else
                {
                    float a = srcA / 255f;
                    frame[dstOff] = (byte)(srcB * a + frame[dstOff] * (1f - a));
                    frame[dstOff + 1] = (byte)(srcG * a + frame[dstOff + 1] * (1f - a));
                    frame[dstOff + 2] = (byte)(srcR * a + frame[dstOff + 2] * (1f - a));
                }
            }
        }
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