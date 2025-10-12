using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Talknado.Client.Models.Helpers.ScreenShare
{
    public static partial class WebPCodec
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WebPConfig
        {
            public int lossless;
            public float quality;
            public int method;
            public int image_hint;
            public int target_size;
            public float target_PSNR;
            public int segments;
            public int sns_strength;
            public int filter_strength;
            public int filter_sharpness;
            public int filter_type;
            public int autofilter;
            public int alpha_compression;
            public int alpha_filtering;
            public int alpha_quality;
            public int pass;
            public int show_compressed;
            public int preprocessing;
            public int partitions;
            public int partition_limit;
            public int emulate_jpeg_size;
            public int thread_level;
            public int low_memory;
            public int near_lossless;
            public int exact;
            public int use_delta_palette;
            public int use_sharp_yuv;
            public int qmin;
            public int qmax;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WebPPicture
        {
            public int use_argb;
            public int colorspace;
            public int width;
            public int height;
            public IntPtr y;
            public IntPtr u;
            public IntPtr v;
            public int y_stride;
            public int uv_stride;
            public IntPtr a;
            public int a_stride;
            public uint pad1_0;
            public uint pad1_1;
            public IntPtr argb;
            public int argb_stride;
            public uint pad2_0;
            public uint pad2_1;
            public uint pad2_2;
            public IntPtr writer;
            public IntPtr custom_ptr;
            public int extra_info_type;
            public IntPtr extra_info;
            public IntPtr stats;
            public uint error_code;
            public IntPtr progress_hook;
            public IntPtr user_data;
            public uint pad3_0;
            public uint pad3_1;
            public uint pad3_2;
            public IntPtr pad4;
            public IntPtr pad5;
            public uint pad6_0;
            public uint pad6_1;
            public uint pad6_2;
            public uint pad6_3;
            public uint pad6_4;
            public uint pad6_5;
            public uint pad6_6;
            public uint pad6_7;
            public IntPtr memory_;
            public IntPtr memory_argb_;
            public IntPtr pad7_0;
            public IntPtr pad7_1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WebPMemoryWriter
        {
            public IntPtr mem;
            public UIntPtr size;
            public UIntPtr max_size;
            public uint pad;
        }

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static partial int WebPConfigInitInternal(ref WebPConfig config, int preset, float quality, int version);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static partial int WebPPictureInitInternal(ref WebPPicture picture, int version);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static partial void WebPMemoryWriterInit(ref WebPMemoryWriter writer);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static partial void WebPMemoryWriterClear(ref WebPMemoryWriter writer);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static partial int WebPMemoryWrite(IntPtr data, UIntPtr data_size, IntPtr picture);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static unsafe partial int WebPPictureImportBGRA(ref WebPPicture picture, byte* bgra, int stride);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static partial int WebPEncode(ref WebPConfig config, ref WebPPicture picture);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static partial void WebPPictureFree(ref WebPPicture picture);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static unsafe partial IntPtr WebPDecodeBGRAInto(
            byte* data,
            UIntPtr data_size,
            byte* output_buffer,
            UIntPtr output_buffer_size,
            int output_stride);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static unsafe partial int WebPGetInfo(byte* data, UIntPtr data_size, out int width, out int height);

        [LibraryImport("libwebp.dll")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        private static partial void WebPFree(IntPtr ptr);

        private const int WEBP_ENCODER_ABI_VERSION = 0x0210;

        private const int WEBP_PRESET_DEFAULT = 0;
        private const int WEBP_PRESET_PICTURE = 1;
        private const int WEBP_PRESET_PHOTO = 2;
        private const int WEBP_PRESET_DRAWING = 3;
        private const int WEBP_PRESET_ICON = 4;
        private const int WEBP_PRESET_TEXT = 5;

        [ThreadStatic]
        private static WebPWriterDelegate? _cachedWriterDelegate;

        /// <summary>
        /// Кодирование BGRA в WebP
        /// </summary>
        public static unsafe byte[] Encode(byte* bgraBuf, int width, int height, int pitch, float quality)
        {
            WebPConfig config = default;
            WebPPicture picture = default;
            WebPMemoryWriter writer = default;

            try
            {
                // Инициализация с preset для графики
                if (WebPConfigInitInternal(ref config, WEBP_PRESET_DEFAULT, quality, WEBP_ENCODER_ABI_VERSION) == 0)
                    throw new Exception("WebP config initialization failed");

                config.method = 0;                 // Самый быстрый метод
                config.segments = 1;               // Минимум сегментов
                config.thread_level = 0;           // Отключить многопоточность
                config.filter_strength = 0;        // Отключить фильтрацию
                config.filter_sharpness = 0;       // Отключить резкость
                config.filter_type = 0;            // Простая фильтрация
                config.autofilter = 0;             // Отключить автофильтрацию
                config.preprocessing = 0;          // Отключить предобработку
                config.partitions = 0;             // Минимум разделов
                config.partition_limit = 0;        // Без ограничений
                config.emulate_jpeg_size = 0;      // Отключить эмуляцию JPEG
                config.low_memory = 0;             // Приоритет скорости
                config.exact = 0;                  // Неточное кодирование
                config.use_delta_palette = 0;      // Отключить дельта-палитру
                config.use_sharp_yuv = 0;          // Отключить sharp YUV
                config.pass = 1;                   // Один проход
                config.show_compressed = 0;        // Не показывать сжатые данные
                config.sns_strength = 0;           // Отключить SNS
                config.alpha_compression = 0;      // Отключить сжатие альфа
                config.alpha_filtering = 0;        // Отключить фильтрацию альфа
                config.alpha_quality = 0;          // Минимальное качество альфа
                config.lossless = 0;               // Lossy кодирование быстрее
                config.near_lossless = 0;          // Отключить near-lossless
                config.qmin = 0;                   // Минимальное качество
                config.qmax = 100;                 // Максимальное качество
                config.image_hint = 0;             // Без hint
                config.target_size = 0;            // Не ограничивать размер
                config.target_PSNR = 0;            // Не ограничивать PSNR

                if (WebPPictureInitInternal(ref picture, WEBP_ENCODER_ABI_VERSION) == 0)
                    throw new Exception("WebP picture initialization failed");

                picture.width = width;
                picture.height = height;
                picture.use_argb = 1;

                WebPMemoryWriterInit(ref writer);

                _cachedWriterDelegate ??= WebPMemoryWrite;
                picture.writer = Marshal.GetFunctionPointerForDelegate(_cachedWriterDelegate);
                picture.custom_ptr = new IntPtr(Unsafe.AsPointer(ref writer));

                if (WebPPictureImportBGRA(ref picture, bgraBuf, pitch) == 0)
                    throw new Exception("WebP picture import failed");

                if (WebPEncode(ref config, ref picture) == 0)
                    throw new Exception($"WebP encoding failed. Error code: {picture.error_code}");

                // Копирование результата
                byte[] result = new byte[(int)writer.size];
                Marshal.Copy(writer.mem, result, 0, (int)writer.size);

                return result;
            }
            finally
            {
                WebPPictureFree(ref picture);
                WebPMemoryWriterClear(ref writer);
            }
        }

        /// <summary>
        /// Декодирование WebP в BGRA
        /// </summary>
        public static unsafe byte[] Decode(byte[] webpData, out int width, out int height)
        {
            fixed (byte* dataPtr = webpData)
            {
                if (WebPGetInfo(dataPtr, (UIntPtr)webpData.Length, out width, out height) == 0)
                    throw new Exception("WebP info extraction failed");

                int stride = width * 4;
                int size = stride * height;
                byte[] result = new byte[size];

                fixed (byte* resultPtr = result)
                {
                    IntPtr decodedPtr = WebPDecodeBGRAInto(
                        dataPtr,
                        (UIntPtr)webpData.Length,
                        resultPtr,
                        (UIntPtr)size,
                        stride);

                    if (decodedPtr == IntPtr.Zero)
                        throw new Exception("WebP decoding failed");
                }

                return result;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int WebPWriterDelegate(IntPtr data, UIntPtr data_size, IntPtr picture);
    }
}
