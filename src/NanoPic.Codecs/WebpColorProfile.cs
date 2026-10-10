using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoPic.Codecs;

// dwebp exports pixels but not the original RIFF ICC chunk. Attach the profile
// explicitly so preview and quality screening compare displayed colours.
internal static class WebpColorProfile
{
    public static BitmapFrame Attach(BitmapFrame frame, string webpPath, CancellationToken token)
    {
        using var stream = File.OpenRead(webpPath);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 12 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") return frame;
        var end = Math.Min(stream.Length, (long)reader.ReadUInt32() + 8);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WEBP") return frame;
        while (stream.Position + 8 <= end)
        {
            token.ThrowIfCancellationRequested();
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32();
            if (length > end - stream.Position) throw new InvalidDataException("WebP 色彩配置块不完整。");
            if (type == "ICCP")
            {
                if (length == 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("WebP 色彩配置超出安全范围。");
                var temporary = Path.Combine(Path.GetTempPath(), "nanopic-profile-" + Guid.NewGuid().ToString("N") + ".icc");
                try
                {
                    File.WriteAllBytes(temporary, reader.ReadBytes((int)length));
                    var context = new ColorContext(new Uri(temporary));
                    // WIC loads the profile into its context; no file-backed pixel data
                    // escapes the call. The temporary file is removed immediately.
                    using (var profile = context.OpenProfileStream()) { if (profile.Length == 0) throw new InvalidDataException("色彩配置为空。"); }
                    var result = BitmapFrame.Create(frame, frame.Thumbnail, frame.Metadata as BitmapMetadata,
                        new ReadOnlyCollection<ColorContext>(new[] { context }));
                    result.Freeze(); return result;
                }
                finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            stream.Position += (long)length + (length & 1);
        }
        return frame;
    }
}
