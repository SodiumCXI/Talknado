namespace Talknado.Client.Models.Helpers.ScreenShare;

public static class FrameResizer
{
    public static byte[] ResizeFrame(
        byte[] sourceData,
        int srcWidth,
        int srcHeight,
        int maxPixels,
        out int outWidth,
        out int outHeight)
    {
        const int bytesPerPixel = 4;
        int srcPixels = srcWidth * srcHeight;

        int outStride;
        byte[] result;

        if (srcPixels <= maxPixels)
        {
            outStride = srcWidth * bytesPerPixel;
            outWidth = srcWidth;
            outHeight = srcHeight;

            result = new byte[outStride * srcHeight];
            Array.Copy(sourceData, result, result.Length);
            return result;
        }

        double scale = Math.Sqrt((double)maxPixels / srcPixels);

        int dstWidth = (int)Math.Round(srcWidth * scale);
        int dstHeight = (int)Math.Round(srcHeight * scale);

        outWidth = dstWidth;
        outHeight = dstHeight;
        outStride = dstWidth * bytesPerPixel;

        result = new byte[outStride * dstHeight];

        ResizeBilinear(sourceData, srcWidth, srcHeight, result, dstWidth, dstHeight);

        return result;
    }

    private static unsafe void ResizeNearestNeighbor(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        const int bpp = 4;

        fixed (byte* srcPtr = src)
        fixed (byte* dstPtr = dst)
        {
            byte* srcP = srcPtr;
            byte* dstP = dstPtr;

            int srcStride = sw * bpp;
            int dstStride = dw * bpp;

            float xRatio = (float)sw / dw;
            float yRatio = (float)sh / dh;

            for (int y = 0; y < dh; y++)
            {
                int srcY = (int)(y * yRatio);
                byte* srcRow = srcP + srcY * srcStride;
                byte* dstRow = dstP + y * dstStride;

                for (int x = 0; x < dw; x++)
                {
                    int srcX = (int)(x * xRatio);
                    byte* srcPixel = srcRow + srcX * bpp;
                    byte* dstPixel = dstRow + x * bpp;

                    *(uint*)dstPixel = *(uint*)srcPixel;
                }
            }
        }
    }

    private static unsafe void ResizeBilinear(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        const int bpp = 4;
        float xRatio = (float)(sw - 1) / dw;
        float yRatio = (float)(sh - 1) / dh;

        fixed (byte* srcPtr = src)
        fixed (byte* dstPtr = dst)
        {
            for (int y = 0; y < dh; y++)
            {
                float srcY = y * yRatio;
                int y0 = (int)srcY;
                int y1 = Math.Min(y0 + 1, sh - 1);
                float yWeight = srcY - y0;
                byte* dstRow = dstPtr + y * dw * bpp;

                for (int x = 0; x < dw; x++)
                {
                    float srcX = x * xRatio;
                    int x0 = (int)srcX;
                    int x1 = Math.Min(x0 + 1, sw - 1);
                    float xWeight = srcX - x0;

                    int idx00 = (y0 * sw + x0) * bpp;
                    int idx10 = (y0 * sw + x1) * bpp;
                    int idx01 = (y1 * sw + x0) * bpp;
                    int idx11 = (y1 * sw + x1) * bpp;

                    for (int c = 0; c < bpp; c++)
                    {
                        float top = srcPtr[idx00 + c] * (1 - xWeight) + srcPtr[idx10 + c] * xWeight;
                        float bottom = srcPtr[idx01 + c] * (1 - xWeight) + srcPtr[idx11 + c] * xWeight;
                        dstRow[x * bpp + c] = (byte)(top * (1 - yWeight) + bottom * yWeight);
                    }
                }
            }
        }
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
