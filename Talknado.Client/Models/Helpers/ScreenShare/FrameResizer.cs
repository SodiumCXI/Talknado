using System.Buffers;
using System.Runtime.CompilerServices;

namespace Talknado.Client.Models.Helpers.ScreenShare
{
    public static class FrameResizer
    {
        public static byte[] ResizeFrame(
        byte[] sourceData,
        int srcWidth,
        int srcHeight,
        int dstWidth,
        int dstHeight,
        out int outStride)
        {
            const int bytesPerPixel = 4;
            outStride = dstWidth * bytesPerPixel;

            byte[] result = ArrayPool<byte>.Shared.Rent(outStride * dstHeight);

            if (srcWidth == dstWidth && srcHeight == dstHeight)
            {
                Array.Copy(sourceData, result, outStride * dstHeight);
                return result;
            }

            ResizeAreaAverage(sourceData, srcWidth, srcHeight, result, dstWidth, dstHeight);

            return result;
        }

        private static unsafe void ResizeAreaAverage(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
        {
            const int bpp = 4;
            float xScale = (float)sw / dw;
            float yScale = (float)sh / dh;

            fixed (byte* srcPtr = src)
            fixed (byte* dstPtr = dst)
            {
                for (int dy = 0; dy < dh; dy++)
                {
                    float srcY0 = dy * yScale;
                    float srcY1 = (dy + 1) * yScale;
                    int y0 = (int)srcY0;
                    int y1 = Math.Min((int)Math.Ceiling(srcY1), sh);

                    byte* dstRow = dstPtr + dy * dw * bpp;

                    for (int dx = 0; dx < dw; dx++)
                    {
                        float srcX0 = dx * xScale;
                        float srcX1 = (dx + 1) * xScale;
                        int x0 = (int)srcX0;
                        int x1 = Math.Min((int)Math.Ceiling(srcX1), sw);

                        double b = 0, g = 0, r = 0, a = 0;
                        float totalWeight = 0;

                        for (int sy = y0; sy < y1; sy++)
                        {
                            float yWeight = Math.Min(sy + 1, srcY1) - Math.Max(sy, srcY0);
                            byte* srcRow = srcPtr + sy * sw * bpp;

                            for (int sx = x0; sx < x1; sx++)
                            {
                                float xWeight = Math.Min(sx + 1, srcX1) - Math.Max(sx, srcX0);
                                float weight = xWeight * yWeight;
                                totalWeight += weight;

                                byte* pixel = srcRow + sx * bpp;
                                b += pixel[0] * weight;
                                g += pixel[1] * weight;
                                r += pixel[2] * weight;
                                a += pixel[3] * weight;
                            }
                        }

                        int dstIdx = dx * bpp;
                        dstRow[dstIdx + 0] = (byte)(b / totalWeight);
                        dstRow[dstIdx + 1] = (byte)(g / totalWeight);
                        dstRow[dstIdx + 2] = (byte)(r / totalWeight);
                        dstRow[dstIdx + 3] = (byte)(a / totalWeight);
                    }
                }
            }
        }
    }
}
