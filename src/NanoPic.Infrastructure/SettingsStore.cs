using System.Text.Json;
using NanoPic.Core;

namespace NanoPic.Infrastructure;

public sealed record NanoPicSettings(
    int SchemaVersion,
    SystemSettings System,
    WatermarkSettings Watermark,
    ResizeSettings Resize,
    GraphSettings Graph,
    CompressionSettings Compress,
    UiSettings Ui)
{
    public const int CurrentSchemaVersion = 1;

    public ProcessingSettings Processing { get; init; } = ProcessingSettings.Default;
    public MetadataNoteSettings MetadataNote { get; init; } = MetadataNoteSettings.Default;
    public OversizedImageSettings OversizedImage { get; init; } = OversizedImageSettings.Default;

    public static NanoPicSettings Default { get; } = new(
        CurrentSchemaVersion,
        new SystemSettings(2, TopMost: false, UseGpu: false, AutoDownscaleOnExceed: true),
        new WatermarkSettings(false, string.Empty, "#000000", 100, "Segoe UI", 24),
        new ResizeSettings(false, 1920, 1080, PreserveAspectRatio: true),
        new GraphSettings("#FFFFFF", 100),
        new CompressionSettings(ImageOutputFormat.Jpeg, 80, false, 200L * 1024L, false, "{name}", 1),
        new UiSettings(string.Empty));
}

public sealed record SystemSettings(int MaxThreads, bool TopMost, bool UseGpu, bool AutoDownscaleOnExceed = true);
public sealed record WatermarkSettings(bool Enabled, string Text, string ColorHex, int OpacityPercent, string FontFamily, int FontSize)
{
    public int Margin { get; init; } = 16;
    public ImageWatermarkPosition Position { get; init; } = ImageWatermarkPosition.BottomRight;
}
public sealed record ResizeSettings(bool Enabled, int Width, int Height, bool PreserveAspectRatio);
public sealed record GraphSettings(string BackgroundColorHex, int BrightnessPercent);
public sealed record CompressionSettings(
    ImageOutputFormat OutputFormat,
    int Quality,
    bool AllowExceedTarget,
    long TargetBytes,
    bool UseTargetSize,
    string OutputFilenameTemplate,
    int OutputIndex)
{
    public OutputConflictPolicy ConflictPolicy { get; init; } = OutputConflictPolicy.Overwrite;
}
public sealed record UiSettings(string OutputDirectory);
public sealed record ProcessingSettings(bool AutoOrient, bool StripMetadata)
{
    public static ProcessingSettings Default { get; } = new(AutoOrient: true, StripMetadata: false);
}
public sealed record MetadataNoteSettings(bool Enabled, string Text)
{
    public static MetadataNoteSettings Default { get; } = new(false, string.Empty);
}

public enum SettingsLoadSource
{
    Defaults = 0,
    CurrentFile,
    LegacyMigrated,
    InvalidCurrentFile
}

public sealed record SettingsLoadResult(NanoPicSettings Settings, SettingsLoadSource Source, ImageOperationFailure? Failure);
public sealed record SettingsSaveResult(bool Saved, ImageOperationFailure? Failure);

public sealed class JsonSettingsStore
{
    private const int MaxSettingsBytes = 1_048_576;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public JsonSettingsStore(string settingsPath, string? legacySettingsPath = null)
    {
        SettingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        LegacySettingsPath = legacySettingsPath;
    }

    public string SettingsPath { get; }
    public string? LegacySettingsPath { get; }
    public string BackupPath => SettingsPath + ".bak";

    public static string GetDefaultSettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "NanoPic", "settings.json");
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(SettingsPath))
        {
            var current = await TryReadCurrentAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
            return current.IsSuccess && current.Value is not null
                ? new SettingsLoadResult(current.Value, SettingsLoadSource.CurrentFile, null)
                : new SettingsLoadResult(NanoPicSettings.Default, SettingsLoadSource.InvalidCurrentFile, current.Failure);
        }

        var legacySettingsPath = LegacySettingsPath;
        if (legacySettingsPath is not null && legacySettingsPath.Trim().Length > 0 && File.Exists(legacySettingsPath))
        {
            var legacy = await TryReadLegacyAsync(legacySettingsPath, cancellationToken).ConfigureAwait(false);
            if (legacy.IsSuccess && legacy.Value is not null)
            {
                var save = await SaveAsync(legacy.Value, cancellationToken).ConfigureAwait(false);
                return save.Saved
                    ? new SettingsLoadResult(legacy.Value, SettingsLoadSource.LegacyMigrated, null)
                    : new SettingsLoadResult(legacy.Value, SettingsLoadSource.LegacyMigrated, save.Failure);
            }

            return new SettingsLoadResult(NanoPicSettings.Default, SettingsLoadSource.InvalidCurrentFile, legacy.Failure);
        }

        return new SettingsLoadResult(NanoPicSettings.Default, SettingsLoadSource.Defaults, null);
    }

    public async Task<SettingsSaveResult> SaveAsync(NanoPicSettings settings, CancellationToken cancellationToken)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        var validation = SettingsValidator.Validate(settings);
        if (validation is not null)
        {
            return new SettingsSaveResult(false, validation);
        }

        var temporaryPath = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return FailedSave(ImageFailureKind.InvalidConfiguration, "配置路径必须包含有效目录。");
            }

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(SettingsPath))
            {
                File.Replace(temporaryPath, SettingsPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }

            temporaryPath = string.Empty;
            return new SettingsSaveResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return FailedSave(ImageFailureKind.TaskCanceled, "保存配置已取消。");
        }
        catch (UnauthorizedAccessException exception)
        {
            return FailedSave(ImageFailureKind.FileAccessConflict, "没有写入配置文件的权限。", exception);
        }
        catch (IOException exception)
        {
            return FailedSave(ImageFailureKind.FileAccessConflict, "写入配置文件时发生 I/O 错误。", exception);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<ImageOperationResult<NanoPicSettings>> TryReadCurrentAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            EnsureSizeWithinLimit(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, useAsync: true);
            var settings = await JsonSerializer.DeserializeAsync<NanoPicSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (settings is null || settings.SchemaVersion != NanoPicSettings.CurrentSchemaVersion)
            {
                return ImageOperationResult<NanoPicSettings>.Failed(ImageFailureKind.InvalidConfiguration, "配置文件版本不受支持或内容为空。");
            }

            var validation = SettingsValidator.Validate(settings);
            return validation is null
                ? ImageOperationResult<NanoPicSettings>.Success(settings)
                : new ImageOperationResult<NanoPicSettings>(default, validation);
        }
        catch (JsonException exception)
        {
            return ImageOperationResult<NanoPicSettings>.Failed(ImageFailureKind.InvalidConfiguration, "配置文件格式无效。", exception);
        }
        catch (IOException exception)
        {
            return ImageOperationResult<NanoPicSettings>.Failed(ImageFailureKind.FileAccessConflict, "无法读取配置文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ImageOperationResult<NanoPicSettings>.Failed(ImageFailureKind.FileAccessConflict, "没有读取配置文件的权限。", exception);
        }
    }

    private async Task<ImageOperationResult<NanoPicSettings>> TryReadLegacyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            EnsureSizeWithinLimit(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, useAsync: true);
            var legacy = await JsonSerializer.DeserializeAsync<LegacySettingsDocument>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (legacy is null)
            {
                return ImageOperationResult<NanoPicSettings>.Failed(ImageFailureKind.InvalidConfiguration, "旧版配置文件内容为空。");
            }

            var migrated = LegacySettingsMapper.Map(legacy);
            var validation = SettingsValidator.Validate(migrated);
            return validation is null
                ? ImageOperationResult<NanoPicSettings>.Success(migrated)
                : new ImageOperationResult<NanoPicSettings>(default, validation);
        }
        catch (JsonException exception)
        {
            return ImageOperationResult<NanoPicSettings>.Failed(ImageFailureKind.InvalidConfiguration, "旧版配置文件格式无效。", exception);
        }
        catch (IOException exception)
        {
            return ImageOperationResult<NanoPicSettings>.Failed(ImageFailureKind.FileAccessConflict, "无法读取旧版配置文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ImageOperationResult<NanoPicSettings>.Failed(ImageFailureKind.FileAccessConflict, "没有读取旧版配置文件的权限。", exception);
        }
    }

    private static void EnsureSizeWithinLimit(string path)
    {
        if (new FileInfo(path).Length > MaxSettingsBytes)
        {
            throw new IOException("配置文件超过安全大小上限。");
        }
    }

    private static SettingsSaveResult FailedSave(ImageFailureKind kind, string message, Exception? exception = null) =>
        new(false, new ImageOperationFailure(kind, message, exception));

    private static void TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class SettingsValidator
{
    public static ImageOperationFailure? Validate(NanoPicSettings settings)
    {
        if (settings.SchemaVersion != NanoPicSettings.CurrentSchemaVersion || settings.System.MaxThreads is < 1 or > 64 ||
            settings.Compress.Quality is < 1 or > 100 || settings.Compress.TargetBytes <= 0 || settings.Compress.OutputIndex < 1 ||
            settings.Resize.Width is < 1 or > 32_768 || settings.Resize.Height is < 1 or > 32_768 ||
            settings.Graph.BrightnessPercent is < 0 or > 200 || settings.Watermark.OpacityPercent is < 0 or > 100 ||
            settings.Watermark.FontSize is < 4 or > 256 || settings.Watermark.Margin is < 0 or > 4096 ||
            settings.OversizedImage.SoftMaxPixels < OversizedImageSettings.MinSoftMaxPixels ||
            settings.OversizedImage.SoftMaxPixels > OversizedImageSettings.MaxSoftMaxPixels ||
            !Enum.IsDefined(typeof(OutputConflictPolicy), settings.Compress.ConflictPolicy) ||
            !IsRgbHex(settings.Graph.BackgroundColorHex) || !IsRgbHex(settings.Watermark.ColorHex) ||
            string.IsNullOrWhiteSpace(settings.Compress.OutputFilenameTemplate) ||
            settings.Compress.OutputFilenameTemplate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            settings.Compress.OutputFilenameTemplate.Contains(Path.DirectorySeparatorChar) ||
            settings.Compress.OutputFilenameTemplate.Contains(Path.AltDirectorySeparatorChar))
        {
            return new ImageOperationFailure(ImageFailureKind.InvalidConfiguration, "配置包含超出允许范围的值。");
        }

        return null;
    }

    private static bool IsRgbHex(string value) =>
        value.Length == 7 && value[0] == '#' && value.Substring(1).All(Uri.IsHexDigit);
}

public sealed class LegacySettingsDocument
{
    public LegacySystemSettings? System { get; init; }
    public LegacyWatermarkSettings? Watermark { get; init; }
    public LegacyResizeSettings? Resize { get; init; }
    public LegacyGraphSettings? Graph { get; init; }
    public LegacyCompressSettings? Compress { get; init; }
    public LegacyUiSettings? Ui { get; init; }
}

public sealed class LegacySystemSettings { public int MaxThreads { get; init; } = 2; public bool TopMost { get; init; } public bool UseGpu { get; init; } }
public sealed class LegacyWatermarkSettings { public int WatermarkType { get; init; } public int WatermarkAlpha { get; init; } = 100; public int[]? WatermarkColor { get; init; } public string? WatermarkText { get; init; } public LegacyFontSettings? WatermarkFont { get; init; } }
public sealed class LegacyFontSettings { public string? Name { get; init; } public float Size { get; init; } = 24; }
public sealed class LegacyResizeSettings { public int LimitWidth { get; init; } = 1920; public int LimitHeight { get; init; } = 1080; public int ResizeType { get; init; } }
public sealed class LegacyGraphSettings { public int[]? BackgroundColor { get; init; } public int Brightness { get; init; } = 100; }
public sealed class LegacyCompressSettings { public int ExtensionType { get; init; } public int Quality { get; init; } = 80; public bool AcceptExceedPicture { get; init; } public long LimitSize { get; init; } = 200; public string? OutputFilename { get; init; } public int OutputIndex { get; init; } = 1; public int CompressType { get; init; } }
public sealed class LegacyUiSettings { public string? OutputDirectory { get; init; } }

public static class LegacySettingsMapper
{
    public static NanoPicSettings Map(LegacySettingsDocument legacy)
    {
        var defaults = NanoPicSettings.Default;
        return defaults with
        {
            System = new SystemSettings(legacy.System?.MaxThreads ?? defaults.System.MaxThreads, legacy.System?.TopMost ?? false, legacy.System?.UseGpu ?? false, AutoDownscaleOnExceed: true),
            Watermark = new WatermarkSettings(
                (legacy.Watermark?.WatermarkType ?? 0) != 0,
                legacy.Watermark?.WatermarkText ?? string.Empty,
                ToHex(legacy.Watermark?.WatermarkColor, "#000000"),
                legacy.Watermark?.WatermarkAlpha ?? defaults.Watermark.OpacityPercent,
                legacy.Watermark?.WatermarkFont?.Name ?? defaults.Watermark.FontFamily,
                Math.Max(4, (int)Math.Round(legacy.Watermark?.WatermarkFont?.Size ?? defaults.Watermark.FontSize))),
            Resize = new ResizeSettings(
                (legacy.Resize?.ResizeType ?? 0) != 0,
                legacy.Resize?.LimitWidth ?? defaults.Resize.Width,
                legacy.Resize?.LimitHeight ?? defaults.Resize.Height,
                PreserveAspectRatio: true),
            Graph = new GraphSettings(ToHex(legacy.Graph?.BackgroundColor, "#FFFFFF"), legacy.Graph?.Brightness ?? defaults.Graph.BrightnessPercent),
            Compress = new CompressionSettings(
                MapFormat(legacy.Compress?.ExtensionType ?? 0),
                legacy.Compress?.Quality ?? defaults.Compress.Quality,
                legacy.Compress?.AcceptExceedPicture ?? false,
                checked(Math.Max(1, legacy.Compress?.LimitSize ?? 200) * 1024L),
                (legacy.Compress?.CompressType ?? 0) != 0,
                legacy.Compress?.OutputFilename ?? defaults.Compress.OutputFilenameTemplate,
                Math.Max(1, legacy.Compress?.OutputIndex ?? defaults.Compress.OutputIndex)),
            Ui = new UiSettings(legacy.Ui?.OutputDirectory ?? string.Empty)
        };
    }

    private static ImageOutputFormat MapFormat(int format) => format switch
    {
        0 => ImageOutputFormat.Jpeg,
        1 => ImageOutputFormat.Png,
        2 => ImageOutputFormat.Webp,
        3 => ImageOutputFormat.Original,
        _ => ImageOutputFormat.Jpeg
    };

    private static string ToHex(IReadOnlyList<int>? color, string fallback) => color is { Count: 3 } && color.All(component => component is >= 0 and <= 255)
        ? $"#{color[0]:X2}{color[1]:X2}{color[2]:X2}"
        : fallback;
}
