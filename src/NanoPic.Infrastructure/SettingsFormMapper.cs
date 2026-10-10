using NanoPic.Core;

namespace NanoPic.Infrastructure;

public sealed record SettingsFormState(
    int OutputFormatIndex,
    string Quality,
    bool UseTargetSize,
    string TargetSizeKilobytes,
    bool TargetSizeInMegabytes,
    bool AllowExceedTarget,
    bool ResizeEnabled,
    string Width,
    string Height,
    bool PreserveAspectRatio,
    bool AutoOrient,
    bool StripMetadata,
    bool BrightnessEnabled,
    string BrightnessPercent,
    string BackgroundColorHex,
    bool WatermarkEnabled,
    string WatermarkText,
    string WatermarkColorHex,
    string WatermarkOpacityPercent,
    string WatermarkFontSize,
    string WatermarkMargin,
    string OutputFilenameTemplate,
    string OutputIndex,
    int ConflictPolicyIndex,
    string MaxThreads,
    bool UseGpu,
    bool TopMost,
    string OutputDirectory,
    int WatermarkPositionIndex = 0,
    bool MetadataNoteEnabled = false,
    string MetadataNoteText = "",
    bool AutoDownscaleOnExceed = true,
    string SoftMaxPixels = "200",
    bool AllowResizeForTarget = false,
    int CompressionPreferenceIndex = 0,
    int ImageContentIndex = 0,
    OutputDirectoryMode OutputMode = OutputDirectoryMode.SeparateDirectory);

public static class SettingsFormMapper
{
    public static SettingsFormState ToForm(NanoPicSettings settings)
    {
        var targetBytes = settings.Compress.TargetBytes;
        var inMegabytes = targetBytes >= 1024L * 1024L && targetBytes % (1024L * 1024L) == 0;
        var targetValue = inMegabytes
            ? targetBytes / (1024L * 1024L)
            : Math.Max(1, targetBytes / 1024);
        return new SettingsFormState(
        settings.Compress.OutputFormat switch
        {
            ImageOutputFormat.Png => 1,
            ImageOutputFormat.Webp => 2,
            ImageOutputFormat.Gif => 3,
            ImageOutputFormat.Bmp => 4,
            ImageOutputFormat.Tiff => 5,
            ImageOutputFormat.Ico => 6,
            ImageOutputFormat.Original => 7,
            _ => 0
        },
        settings.Compress.Quality.ToString(),
        settings.Compress.UseTargetSize,
        targetValue.ToString(),
        inMegabytes,
        settings.Compress.AllowExceedTarget,
        settings.Resize.Enabled,
        settings.Resize.Width.ToString(),
        settings.Resize.Height.ToString(),
        settings.Resize.PreserveAspectRatio,
        settings.Processing.AutoOrient,
        settings.Processing.StripMetadata,
        settings.Graph.IsBrightnessEnabled,
        settings.Graph.BrightnessPercent.ToString(),
        settings.Graph.BackgroundColorHex,
        settings.Watermark.Enabled,
        settings.Watermark.Text,
        settings.Watermark.ColorHex,
        settings.Watermark.OpacityPercent.ToString(),
        settings.Watermark.FontSize.ToString(),
        settings.Watermark.Margin.ToString(),
        settings.Compress.OutputFilenameTemplate == "{name}" ? string.Empty : settings.Compress.OutputFilenameTemplate,
        settings.Compress.OutputIndex.ToString(),
        settings.Compress.ConflictPolicy switch
        {
            OutputConflictPolicy.AutoRename => 1,
            OutputConflictPolicy.Skip => 2,
            OutputConflictPolicy.Fail => 3,
            _ => 0
        },
        settings.System.MaxThreads.ToString(),
        settings.System.UseGpu,
        settings.System.TopMost,
        settings.Ui.OutputDirectory,
        (int)settings.Watermark.Position,
        settings.MetadataNote.Enabled,
        settings.MetadataNote.Text,
        settings.System.AutoDownscaleOnExceed,
        (settings.OversizedImage.SoftMaxPixels / 1_000_000).ToString(),
        settings.Compress.AllowResizeForTarget,
        settings.Compress.Preference == CompressionPreference.PreserveFidelity ? 0 : 1,
        settings.Compress.ContentKind == ImageContentKind.Auto ? 0 : settings.Compress.ContentKind == ImageContentKind.Photo ? 1 : 2,
        settings.Ui.OutputMode);
    }

    public static NanoPicSettings Capture(NanoPicSettings basis, SettingsFormState form)
    {
        var quality = ParseOrDefault(form.Quality, basis.Compress.Quality);
        var manualQualityActive = form.CompressionPreferenceIndex == 1 && !form.UseTargetSize && form.OutputFormatIndex is 0 or 1 or 2 or 7;
        if (!manualQualityActive) quality = ValidOrDefault(quality, 1, 100, basis.Compress.Quality);
        var unitBytes = form.TargetSizeInMegabytes ? 1024L * 1024 : 1024L;
        var fallbackTargetValue = form.TargetSizeInMegabytes
            ? Math.Max(1, basis.Compress.TargetBytes / (1024L * 1024))
            : Math.Max(1, basis.Compress.TargetBytes / 1024);
        var targetValue = ParseLongOrDefault(form.TargetSizeKilobytes, fallbackTargetValue);
        var width = ParseOrDefault(form.Width, basis.Resize.Width);
        var height = ParseOrDefault(form.Height, basis.Resize.Height);
        var brightness = ParseOrDefault(form.BrightnessPercent, basis.Graph.BrightnessPercent);
        var watermarkOpacity = ParseOrDefault(form.WatermarkOpacityPercent, basis.Watermark.OpacityPercent);
        var watermarkFontSize = ParseOrDefault(form.WatermarkFontSize, basis.Watermark.FontSize);
        var watermarkMargin = ParseOrDefault(form.WatermarkMargin, basis.Watermark.Margin);
        var outputIndex = ParseOrDefault(form.OutputIndex, basis.Compress.OutputIndex);
        var maxThreads = ParseOrDefault(form.MaxThreads, basis.System.MaxThreads);
        var softMaxPixels = ParseLongOrDefault(form.SoftMaxPixels, basis.OversizedImage.SoftMaxPixels / 1_000_000) * 1_000_000L;
        // Valid inactive drafts survive mode switches; invalid inactive values must
        // not block processing or persist an unusable configuration.
        if (!form.UseTargetSize && (targetValue <= 0 || targetValue > long.MaxValue / unitBytes)) targetValue = fallbackTargetValue;
        if (!form.ResizeEnabled)
        {
            width = ValidOrDefault(width, 1, 32768, basis.Resize.Width);
            height = ValidOrDefault(height, 1, 32768, basis.Resize.Height);
        }
        if (!form.BrightnessEnabled) brightness = ValidOrDefault(brightness, 0, 200, basis.Graph.BrightnessPercent);
        var watermarkColor = form.WatermarkColorHex.Trim();
        if (!form.WatermarkEnabled)
        {
            watermarkOpacity = ValidOrDefault(watermarkOpacity, 0, 100, basis.Watermark.OpacityPercent);
            watermarkFontSize = ValidOrDefault(watermarkFontSize, 4, 256, basis.Watermark.FontSize);
            watermarkMargin = ValidOrDefault(watermarkMargin, 0, 4096, basis.Watermark.Margin);
            if (!IsColor(watermarkColor)) watermarkColor = basis.Watermark.ColorHex;
        }
        var backgroundColor = form.BackgroundColorHex.Trim();
        if (form.OutputFormatIndex is not (0 or 4 or 7) && !IsColor(backgroundColor)) backgroundColor = basis.Graph.BackgroundColorHex;
        var template = string.IsNullOrWhiteSpace(form.OutputFilenameTemplate) ? "{name}" : form.OutputFilenameTemplate.Trim();
        if (form.OutputMode == OutputDirectoryMode.SourceDirectory)
        {
            outputIndex = ValidOrDefault(outputIndex, 1, int.MaxValue, basis.Compress.OutputIndex);
            if (!IsTemplate(template)) template = basis.Compress.OutputFilenameTemplate;
        }

        return basis with
        {
            System = basis.System with { MaxThreads = maxThreads, UseGpu = form.UseGpu, TopMost = form.TopMost, AutoDownscaleOnExceed = form.AutoDownscaleOnExceed },
            OversizedImage = new OversizedImageSettings(
                SoftMaxPixels: Math.Max(OversizedImageSettings.MinSoftMaxPixels, Math.Min(OversizedImageSettings.MaxSoftMaxPixels, softMaxPixels)),
                AutoDownsample: form.AutoDownscaleOnExceed),
            Compress = basis.Compress with
            {
                OutputFormat = form.OutputFormatIndex switch
                {
                    1 => ImageOutputFormat.Png,
                    2 => ImageOutputFormat.Webp,
                    3 => ImageOutputFormat.Gif,
                    4 => ImageOutputFormat.Bmp,
                    5 => ImageOutputFormat.Tiff,
                    6 => ImageOutputFormat.Ico,
                    7 => ImageOutputFormat.Original,
                    _ => ImageOutputFormat.Jpeg
                },
                Quality = quality,
                UseTargetSize = form.UseTargetSize,
                TargetBytes = targetValue * unitBytes,
                AllowExceedTarget = form.AllowExceedTarget,
                AllowResizeForTarget = form.AllowResizeForTarget,
                Preference = form.CompressionPreferenceIndex == 1 ? CompressionPreference.SizeFirst : CompressionPreference.PreserveFidelity,
                ContentKind = form.ImageContentIndex == 2 ? ImageContentKind.TextOrScreenshot : form.ImageContentIndex == 1 ? ImageContentKind.Photo : ImageContentKind.Auto,
                OutputFilenameTemplate = template,
                OutputIndex = outputIndex,
                ConflictPolicy = form.ConflictPolicyIndex switch
                {
                    1 => OutputConflictPolicy.AutoRename,
                    2 => OutputConflictPolicy.Skip,
                    3 => OutputConflictPolicy.Fail,
                    _ => OutputConflictPolicy.Overwrite
                }
            },
            Resize = basis.Resize with { Enabled = form.ResizeEnabled, Width = width, Height = height, PreserveAspectRatio = form.PreserveAspectRatio },
            Processing = new ProcessingSettings(form.AutoOrient, form.StripMetadata),
            MetadataNote = new MetadataNoteSettings(
                form.MetadataNoteEnabled && !string.IsNullOrWhiteSpace(form.MetadataNoteText),
                form.MetadataNoteText.Trim()),
            Graph = basis.Graph with { BrightnessPercent = brightness, BrightnessEnabled = basis.Graph.BrightnessEnabled is null && form.BrightnessEnabled == (brightness != 100) ? null : form.BrightnessEnabled, BackgroundColorHex = backgroundColor },
            Watermark = basis.Watermark with
            {
                Enabled = form.WatermarkEnabled,
                Text = form.WatermarkText,
                ColorHex = watermarkColor,
                OpacityPercent = watermarkOpacity,
                FontSize = watermarkFontSize,
                Margin = watermarkMargin,
                Position = form.WatermarkPositionIndex switch
                {
                    1 => ImageWatermarkPosition.BottomLeft,
                    2 => ImageWatermarkPosition.TopRight,
                    3 => ImageWatermarkPosition.TopLeft,
                    4 => ImageWatermarkPosition.Center,
                    5 => ImageWatermarkPosition.Random,
                    _ => ImageWatermarkPosition.BottomRight
                }
            },
            Ui = new UiSettings(form.OutputDirectory.Trim()) { OutputMode = form.OutputMode }
        };
    }

    private static int ValidOrDefault(int value, int min, int max, int fallback) => value >= min && value <= max ? value : Math.Max(min, Math.Min(max, fallback));
    private static bool IsColor(string value) => value.Length == 7 && value[0] == '#' && value.Substring(1).All(Uri.IsHexDigit);
    private static bool IsTemplate(string value) => value.Length > 0 && value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 &&
        value.IndexOf(System.IO.Path.DirectorySeparatorChar) < 0 && value.IndexOf(System.IO.Path.AltDirectorySeparatorChar) < 0;

    private static int ParseOrDefault(string value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
    private static long ParseLongOrDefault(string value, long fallback) => long.TryParse(value, out var parsed) ? parsed : fallback;
}

public sealed record ImageProcessingOptions(ImageEncodingOptions Encoding, ImageTransformOptions Transform);

public static class ImageProcessingOptionsMapper
{
    public static ImageProcessingOptions FromSettings(NanoPicSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        return new ImageProcessingOptions(
            new ImageEncodingOptions(
                settings.Compress.OutputFormat,
                settings.Compress.Quality,
                settings.Compress.UseTargetSize
                    ? new TargetSizeOptions(settings.Compress.TargetBytes, settings.Compress.AllowExceedTarget, AllowResizeForTarget: settings.Compress.AllowResizeForTarget)
                    : null,
                settings.Compress.Preference,
                settings.Compress.ContentKind),
            new ImageTransformOptions(
                AutoOrient: settings.Processing.AutoOrient,
                Resize: settings.Resize.Enabled
                    ? new ImageResizeOptions(true, settings.Resize.Width, settings.Resize.Height, settings.Resize.PreserveAspectRatio)
                    : null,
                BrightnessPercent: settings.Graph.IsBrightnessEnabled ? settings.Graph.BrightnessPercent : 100,
            Watermark: settings.Watermark.Enabled
                ? new ImageWatermarkOptions(
                    true,
                    settings.Watermark.Text,
                    settings.Watermark.ColorHex,
                    settings.Watermark.OpacityPercent,
                    settings.Watermark.FontSize,
                    settings.Watermark.Margin,
                    settings.Watermark.Position)
                : null,
                Background: new ImageBackgroundOptions(true, settings.Graph.BackgroundColorHex),
                StripMetadata: settings.Processing.StripMetadata,
                MetadataNote: settings.MetadataNote.Enabled && !string.IsNullOrWhiteSpace(settings.MetadataNote.Text)
                    ? new ImageMetadataNoteOptions(true, settings.MetadataNote.Text.Trim())
                    : null));
    }
}
