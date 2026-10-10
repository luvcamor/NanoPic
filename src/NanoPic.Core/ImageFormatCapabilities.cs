namespace NanoPic.Core;

public static class ImageFormatCapabilities
{
    public static bool SupportsMetadataNote(ImageFormat format) => format is ImageFormat.Jpeg or ImageFormat.Png or ImageFormat.Tiff;
    public static bool CanUseMetadataNote(ImageOutputFormat format) => format is ImageOutputFormat.Original or ImageOutputFormat.Jpeg or ImageOutputFormat.Png or ImageOutputFormat.Tiff;
    public static bool SupportsManualQuality(ImageOutputFormat format) => format is ImageOutputFormat.Original or ImageOutputFormat.Jpeg or ImageOutputFormat.Png or ImageOutputFormat.Webp;
}
