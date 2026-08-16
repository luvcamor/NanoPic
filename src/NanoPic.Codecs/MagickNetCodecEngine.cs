using ImageMagick;

namespace NanoPic.Codecs;

public static class MagickNetCodecEngine
{
    public const string PackageId = "Magick.NET-Q8-x64";

    public static string RuntimeAssemblyName => typeof(MagickImage).Assembly.GetName().Name ?? PackageId;
}
