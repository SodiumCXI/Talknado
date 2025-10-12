using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Talknado.Client.Models.Client.Helpers
{
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

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorInfo(out CURSORINFO pci);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        private const int CURSOR_SHOWING = 0x00000001;

        public static unsafe void OverlayCursorOnByteBuffer(byte[] pixelBuffer, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(pixelBuffer);
            if (pixelBuffer.Length < width * height * 4) throw new ArgumentException("Buffer is too small.");

            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };

            if (!GetCursorInfo(out ci) || ci.flags != CURSOR_SHOWING)
                return;

            fixed (byte* bufferPtr = pixelBuffer)
            {
                using var bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, new IntPtr(bufferPtr));
                using var g = Graphics.FromImage(bmp);

                IntPtr hdc = g.GetHdc();
                try
                {
                    int x = ci.ptScreenPos.X - SystemInformation.VirtualScreen.Left;
                    int y = ci.ptScreenPos.Y - SystemInformation.VirtualScreen.Top;

                    DrawIcon(hdc, x, y, ci.hCursor);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
        }
    }
}
