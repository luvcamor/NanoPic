using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoPic.Codecs;

// Public WIC COM interfaces only; the OS codec stays outside the portable EXE.
internal static class Jpeg444Encoder
{
    private static readonly Guid Bgr24 = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    private static readonly ConditionalWeakTable<BitmapFrame, Carrier> MetadataCache = new();
    internal sealed class MetadataException : Exception
    {
        public MetadataException(Exception inner) : base("WIC JPEG 元数据序列化失败。", inner) { }
    }

    private sealed class CarrierStream : MemoryStream
    {
        private const int MaximumBytes = 32 * 1024 * 1024;
        private readonly CancellationToken _token;
        public CarrierStream(CancellationToken token) => _token = token;
        public override void Write(byte[] buffer, int offset, int count)
        {
            _token.ThrowIfCancellationRequested();
            if (Position + count > MaximumBytes) throw new NotSupportedException("JPEG 元数据载体超过安全内存上限。");
            base.Write(buffer, offset, count);
        }
        public override void WriteByte(byte value)
        {
            _token.ThrowIfCancellationRequested();
            if (Position >= MaximumBytes) throw new NotSupportedException("JPEG 元数据载体超过安全内存上限。");
            base.WriteByte(value);
        }
        public override void SetLength(long value)
        {
            if (value > MaximumBytes) throw new NotSupportedException("JPEG 元数据载体超过安全内存上限。");
            base.SetLength(value);
        }
    }

    private sealed class Carrier
    {
        public byte[] Bytes { get; }
        public Carrier(BitmapFrame source, CancellationToken token)
        {
            // Let WPF serialize exactly the already-filtered metadata/ICC/thumbnail.
            // Only metadata is read back: these low-quality pixels are NEVER used
            // as the source of the final JPEG. Cache once per frozen prepared frame,
            // so a target-size search does not repeatedly serialize the same data.
            var encoder = new JpegBitmapEncoder { QualityLevel = 1 };
            encoder.Frames.Add(source);
            using var stream = new CarrierStream(token);
            encoder.Save(stream);
            Bytes = stream.ToArray();
        }
    }

    // path must be an exclusively owned temporary output; final replacement is
    // the responsibility of ImageFileProcessingService after verification.
    public static void Write(BitmapFrame source, string path, int quality, CancellationToken token, bool preserveColorContexts = true)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (!source.IsFrozen) throw new ArgumentException("JPEG 编码输入帧必须已冻结。", nameof(source));
        token.ThrowIfCancellationRequested();
        var completed = false;
        var fileCreated = false;
        var objects = new List<object>();
        T Own<T>(T instance) where T : class { objects.Add(instance); return instance; }
        try
        {
            var encoder = Own((IEncoder)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("1a34f5c1-4a5a-46dc-b644-1f4567e7a676"), true)!)!);
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x1011, 0x80, true, null, out var output));
            fileCreated = true;
            Own(output);
            encoder.Initialize(output, 2); // WICBitmapEncoderNoCache
            encoder.CreateNewFrame(out var frame, out var bag);
            Own(frame); Own(bag);
            var properties = new[]
            {
                new Property { Name = "ImageQuality", Type = 1, VariantType = 4 }, // VT_R4
                new Property { Name = "JpegYCrCbSubsampling", Type = 1, VariantType = 17 } // VT_UI1
            };
            bag.Write(2, properties, new object[] { quality / 100f, (byte)3 }); // 444
            frame.Initialize(bag);
            frame.SetSize((uint)source.PixelWidth, (uint)source.PixelHeight);
            frame.SetResolution(source.DpiX, source.DpiY);
            // Keep CMYK and greyscale in their own colour model; converting them
            // to RGB while copying their original ICC would mislabel the pixels.
            var targetFormat = source.Format == PixelFormats.Cmyk32 ? PixelFormats.Cmyk32
                : source.Format == PixelFormats.Gray8 || source.Format == PixelFormats.Gray16 ||
                  source.Format == PixelFormats.Gray4 || source.Format == PixelFormats.Gray2 || source.Format == PixelFormats.BlackWhite
                    ? PixelFormats.Gray8 : PixelFormats.Bgr24;
            var expectedFormat = targetFormat == PixelFormats.Cmyk32 ? new Guid("6fddc324-4e03-4bfe-b185-3d77768dc91c")
                : targetFormat == PixelFormats.Gray8 ? new Guid("6fddc324-4e03-4bfe-b185-3d77768dc908") : Bgr24;
            var pixelFormat = expectedFormat;
            frame.SetPixelFormat(ref pixelFormat);
            if (pixelFormat != expectedFormat) throw new NotSupportedException("WIC JPEG 不支持所需的像素格式。");

            try
            {
                if (source.Metadata is not null || source.ColorContexts?.Count > 0 || source.Thumbnail is not null)
                {
                    var carrier = MetadataCache.GetValue(source, s => new Carrier(s, token));
                    token.ThrowIfCancellationRequested();
                    var metadataBytes = preserveColorContexts ? carrier.Bytes : WithoutIcc(carrier.Bytes);
                    var input = Own(SHCreateMemStream(metadataBytes, (uint)metadataBytes.Length)
                        ?? throw new OutOfMemoryException("无法创建 WIC 元数据流。"));
                    var factory = Own((IFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(
                        new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a"), true)!)!);
                    factory.CreateDecoderFromStream(input, IntPtr.Zero, 1, out var decoder);
                    Own(decoder);
                    decoder.GetFrame(0, out var metadataSource);
                    Own(metadataSource);
                    // QI casts share the frame RCWs; release each RCW only once below.
                    ((IBlockWriter)frame).InitializeFromBlockReader((IBlockReader)metadataSource);
                    metadataSource.GetColorContexts(0, null, out var count);
                    if (preserveColorContexts && count > 0)
                    {
                        var contexts = new IColorContext[count];
                        for (var i = 0; i < count; i++)
                        {
                            factory.CreateColorContext(out contexts[i]);
                            Own(contexts[i]);
                        }
                        metadataSource.GetColorContexts(count, contexts, out var actual);
                        if (actual > count) throw new InvalidOperationException("WIC 返回了无效的色彩配置数量。");
                        if (actual > 0) frame.SetColorContexts(actual, contexts);
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or OverflowException or
                FileFormatException or NotSupportedException or InvalidOperationException)
            {
                throw new MetadataException(ex);
            }

            // Bounded strip buffer instead of a second full-image allocation.
            BitmapSource pixels = source.Format == targetFormat ? source
                : new FormatConvertedBitmap(source, targetFormat, null, 0);
            var stride = checked(source.PixelWidth * (targetFormat.BitsPerPixel / 8));
            var buffer = new byte[checked(stride * Math.Min(64, source.PixelHeight))];
            for (var y = 0; y < source.PixelHeight; y += 64)
            {
                token.ThrowIfCancellationRequested();
                var rows = Math.Min(64, source.PixelHeight - y);
                pixels.CopyPixels(new Int32Rect(0, y, source.PixelWidth, rows), buffer, stride, 0);
                frame.WritePixels((uint)rows, (uint)stride, (uint)(stride * rows), buffer);
            }
            token.ThrowIfCancellationRequested();
            frame.Commit();
            encoder.Commit();
            output.Commit(0);
            completed = true;
        }
        finally
        {
            for (var i = objects.Count - 1; i >= 0; i--) Marshal.ReleaseComObject(objects[i]);
            if (!completed && fileCreated)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Flush(flushToDisk: true);
    }

    // ICC is exposed as an "unknown" APP2 metadata block by the JPEG reader.
    // Remove only these known blocks when fallback explicitly discards colour
    // contexts; clearing the ColorContexts property alone does not remove them.
    private static byte[] WithoutIcc(byte[] jpeg)
    {
        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2);
        var offset = 2;
        var signature = System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0");
        while (offset + 4 <= jpeg.Length && jpeg[offset] == 0xFF)
        {
            var marker = jpeg[offset + 1];
            if (marker == 0xDA || marker == 0xD9) break;
            var length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) throw new FileFormatException("JPEG 元数据载体无效。");
            var icc = marker == 0xE2 && length >= signature.Length + 2;
            for (var i = 0; icc && i < signature.Length; i++) icc = jpeg[offset + 4 + i] == signature[i];
            if (!icc) output.Write(jpeg, offset, length + 2);
            offset += length + 2;
        }
        output.Write(jpeg, offset, jpeg.Length - offset);
        return output.ToArray();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IStream? template, out IStream stream);
    [DllImport("shlwapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern IStream? SHCreateMemStream(byte[] bytes, uint length);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Property
    {
        public uint Type;
        public ushort VariantType, ClipboardFormat;
        public uint Hint;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public Guid ClassId;
    }

    // Unused slots are retained in SDK vtable order. Do not call their placeholders.
    [ComImport, Guid("22F55882-280B-11d0-A8A9-00A0C90C2004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        void Read();
        void Write(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Property[] properties,
            [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 0)] object[] values);
    }

    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEncoder
    {
        void Initialize(IStream stream, uint cache);
        void GetContainerFormat(); void GetEncoderInfo(); void SetColorContexts();
        void SetPalette(); void SetThumbnail(); void SetPreview();
        void CreateNewFrame(out IFrameEncode frame, out IPropertyBag options);
        void Commit();
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFrameEncode
    {
        void Initialize(IPropertyBag options);
        void SetSize(uint width, uint height);
        void SetResolution(double x, double y);
        void SetPixelFormat(ref Guid format);
        void SetColorContexts(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IColorContext[] contexts);
        void SetPalette(); void SetThumbnail();
        void WritePixels(uint lines, uint stride, uint size, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pixels);
        void WriteSource(); void Commit();
    }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream(IStream stream, IntPtr vendor, uint cache, out IDecoder decoder);
        void CreateDecoderFromFileHandle(); void CreateComponentInfo(); void CreateDecoder();
        void CreateEncoder(); void CreatePalette(); void CreateFormatConverter();
        void CreateBitmapScaler(); void CreateBitmapClipper(); void CreateBitmapFlipRotator(); void CreateStream();
        void CreateColorContext(out IColorContext context);
    }

    [ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDecoder
    {
        void QueryCapability(); void Initialize(); void GetContainerFormat(); void GetDecoderInfo();
        void CopyPalette(); void GetMetadataQueryReader(); void GetPreview(); void GetColorContexts();
        void GetThumbnail(); void GetFrameCount(); void GetFrame(uint index, out IFrameDecode frame);
    }

    [ComImport, Guid("3B16811B-6A43-4ec9-A813-3D930C13B940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFrameDecode
    {
        void GetSize(); void GetPixelFormat(); void GetResolution(); void CopyPalette(); void CopyPixels();
        void GetMetadataQueryReader();
        void GetColorContexts(uint count, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IColorContext[]? contexts, out uint actual);
    }

    [ComImport, Guid("3C613A02-34B2-44ea-9A7C-45AEA9C6FD6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IColorContext { }

    [ComImport, Guid("FEAA2A8D-B3F3-43E4-B25C-D1DE990A1AE1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBlockReader { }

    [ComImport, Guid("08FB9676-B444-41E8-8DBE-6A53A542BFF1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBlockWriter
    {
        void GetContainerFormat(); void GetCount(); void GetReaderByIndex(); void GetEnumerator();
        void InitializeFromBlockReader(IBlockReader reader);
    }
}
