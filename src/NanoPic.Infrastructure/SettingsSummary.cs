namespace NanoPic.Infrastructure;

// Summaries describe effective settings, not inactive drafts hidden in the UI.
public sealed record SettingsSummary(bool CompressionChanged, bool ImageChanged, bool NamingChanged, bool PreferencesChanged)
{
    public static SettingsSummary FromSettings(NanoPicSettings settings)
    {
        var defaults = NanoPicSettings.Default;
        var encoding = settings.Compress;
        var transform = ImageProcessingOptionsMapper.FromSettings(settings).Transform;
        var manualQuality = encoding.Preference == NanoPic.Core.CompressionPreference.SizeFirst && !encoding.UseTargetSize &&
            NanoPic.Core.ImageFormatCapabilities.SupportsManualQuality(encoding.OutputFormat);
        var backgroundUsed = encoding.OutputFormat is NanoPic.Core.ImageOutputFormat.Jpeg or NanoPic.Core.ImageOutputFormat.Bmp or NanoPic.Core.ImageOutputFormat.Original;
        return new SettingsSummary(
            encoding.ContentKind != defaults.Compress.ContentKind || manualQuality && encoding.Quality != defaults.Compress.Quality ||
                encoding.UseTargetSize && (encoding.AllowExceedTarget || encoding.AllowResizeForTarget),
            transform.Resize is { Enabled: true } || transform.BrightnessPercent != 100 || transform.Watermark is { Enabled: true } ||
                transform.StripMetadata || !transform.AutoOrient || transform.MetadataNote is { Enabled: true } ||
                backgroundUsed && !string.Equals(settings.Graph.BackgroundColorHex, defaults.Graph.BackgroundColorHex, StringComparison.OrdinalIgnoreCase),
            settings.Ui.OutputMode != OutputDirectoryMode.SourceDirectory &&
                (encoding.OutputFilenameTemplate != defaults.Compress.OutputFilenameTemplate || encoding.OutputIndex != defaults.Compress.OutputIndex ||
                    encoding.ConflictPolicy != defaults.Compress.ConflictPolicy),
            settings.System.MaxThreads != defaults.System.MaxThreads || settings.System.TopMost || settings.System.UseGpu ||
                settings.System.AutoDownscaleOnExceed != defaults.System.AutoDownscaleOnExceed ||
                settings.OversizedImage.SoftMaxPixels != defaults.OversizedImage.SoftMaxPixels);
    }
}
