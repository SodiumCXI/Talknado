using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Talknado.Client.Models.Client.Helpers;

public static partial class CursorRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(out CURSORINFO pci);

    [LibraryImport("user32.dll", EntryPoint = "GetIconInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfoNative(IntPtr hIcon, out ICONINFO piconinfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr hObject);

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;
    private const uint DI_DEFAULTSIZE = 0x0008;

    public static unsafe void OverlayCursorOnByteBuffer(byte[] pixelBuffer, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixelBuffer);
        if (pixelBuffer.Length < width * height * 4)
            throw new ArgumentException("Buffer is too small.");

        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };

        if (!GetCursorInfo(out ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero)
            return;

        bool hasIconInfo = GetIconInfoNative(ci.hCursor, out ICONINFO iconInfo);

        try
        {
            fixed (byte* bufferPtr = pixelBuffer)
            {
                using var bmp = new Bitmap(width, height, width * 4,
                    PixelFormat.Format32bppArgb, new IntPtr(bufferPtr));
                using var g = Graphics.FromImage(bmp);

                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                IntPtr hdc = g.GetHdc();
                try
                {
                    int x = ci.ptScreenPos.X - SystemInformation.VirtualScreen.Left;
                    int y = ci.ptScreenPos.Y - SystemInformation.VirtualScreen.Top;

                    if (hasIconInfo)
                    {
                        x -= iconInfo.xHotspot;
                        y -= iconInfo.yHotspot;
                    }

                    DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL | DI_DEFAULTSIZE);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
        }
        finally
        {
            if (hasIconInfo)
            {
                if (iconInfo.hbmMask != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmColor);
            }
        }
    }
}