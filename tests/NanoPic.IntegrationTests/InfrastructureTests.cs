using NanoPic.Codecs;
using NanoPic.Core;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.IntegrationTests;

public sealed class InfrastructureTests
{
    [Fact]
    public void Keyboard_shortcut_router_maps_core_actions_and_protects_text_editing()
    {
        static ApplicationShortcutAction Resolve(
            ApplicationShortcutKey key,
            bool control = false,
            bool shift = false,
            bool text = false,
            bool queue = false,
            bool running = false,
            bool canStart = true) => KeyboardShortcutRouter.Resolve(new(
                key, control, shift, text, queue, running, canStart));

        Assert.Equal(ApplicationShortcutAction.AddFiles, Resolve(ApplicationShortcutKey.O, control: true));
        Assert.Equal(ApplicationShortcutAction.AddFolder, Resolve(ApplicationShortcutKey.O, control: true, shift: true));
        Assert.Equal(ApplicationShortcutAction.SelectAll, Resolve(ApplicationShortcutKey.A, control: true));
        Assert.Equal(ApplicationShortcutAction.InvertSelection, Resolve(ApplicationShortcutKey.I, control: true));
        Assert.Equal(ApplicationShortcutAction.RemoveHighlighted, Resolve(ApplicationShortcutKey.Delete, queue: true));
        Assert.Equal(ApplicationShortcutAction.PreviewHighlighted, Resolve(ApplicationShortcutKey.Enter, queue: true));
        Assert.Equal(ApplicationShortcutAction.Start, Resolve(ApplicationShortcutKey.F5));
        Assert.Equal(ApplicationShortcutAction.Cancel, Resolve(ApplicationShortcutKey.Escape, running: true));
        Assert.Equal(ApplicationShortcutAction.None, Resolve(ApplicationShortcutKey.A, control: true, text: true));
        Assert.Equal(ApplicationShortcutAction.None, Resolve(ApplicationShortcutKey.F5, canStart: false));
    }

    [Fact]
    public void Input_root_resolver_finds_the_real_common_directory()
    {
        var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NanoPic-CommonRoot"));
        var paths = new[]
        {
            Path.Combine(baseDirectory, "album-a", "one.png"),
            Path.Combine(baseDirectory, "album-b", "nested", "two.png"),
            Path.Combine(baseDirectory, "three.png")
        };

        var result = InputRootDirectoryResolver.FindCommonDirectory(paths);

        Assert.Equal(baseDirectory, result, ignoreCase: true);
    }

    [Fact]
    public void Settings_form_mapper_round_trips_all_editable_processing_fields()
    {
        var settings = NanoPicSettings.Default with
        {
            System = new SystemSettings(4, TopMost: true, UseGpu: true),
            Watermark = new WatermarkSettings(true, "NanoPic", "#112233", 75, "Segoe UI", 18) { Margin = 23 },
            Resize = new ResizeSettings(true, 1280, 720, PreserveAspectRatio: false),
            Graph = new GraphSettings("#AABBCC", 125),
            Compress = new CompressionSettings(ImageOutputFormat.Webp, 77, true, 345L * 1024L, true, "{name}", 3) { ConflictPolicy = OutputConflictPolicy.Skip },
            Processing = new ProcessingSettings(AutoOrient: false, StripMetadata: true),
            Ui = new UiSettings("C:\\output")
        };

        var form = SettingsFormMapper.ToForm(settings);
        var captured = SettingsFormMapper.Capture(settings, form);

        Assert.Equal(settings, captured);
        Assert.True(form.ResizeEnabled);
        Assert.True(form.BrightnessEnabled);
        Assert.True(form.WatermarkEnabled);
        Assert.Equal(2, form.OutputFormatIndex);
        Assert.False(form.PreserveAspectRatio);
        Assert.True(form.StripMetadata);
        Assert.Equal(2, form.ConflictPolicyIndex);
        Assert.Equal("4", form.MaxThreads);

        var options = ImageProcessingOptionsMapper.FromSettings(captured);
        Assert.Equal(ImageOutputFormat.Webp, options.Encoding.OutputFormat);
        Assert.Equal(345L * 1024L, options.Encoding.TargetSize?.TargetBytes);
        Assert.False(options.Transform.AutoOrient);
        Assert.False(options.Transform.Resize?.PreserveAspectRatio);
        Assert.True(options.Transform.StripMetadata);
        Assert.Equal("#AABBCC", options.Transform.Background?.ColorHex);
        Assert.Equal("#112233", options.Transform.Watermark?.ColorHex);
        Assert.Equal(75, options.Transform.Watermark?.OpacityPercent);
        Assert.Equal(18, options.Transform.Watermark?.FontSize);
        Assert.Equal(23, options.Transform.Watermark?.Margin);
    }

    [Fact]
    public async Task Current_settings_without_new_optional_fields_load_with_compatible_defaults()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            await TestCompatibility.WriteAllTextAsync(path, """
                {
                  "SchemaVersion": 1,
                  "System": { "MaxThreads": 3, "TopMost": false, "UseGpu": false },
                  "Watermark": { "Enabled": false, "Text": "", "ColorHex": "#000000", "OpacityPercent": 100, "FontFamily": "Segoe UI", "FontSize": 24 },
                  "Resize": { "Enabled": false, "Width": 1920, "Height": 1080, "PreserveAspectRatio": true },
                  "Graph": { "BackgroundColorHex": "#FFFFFF", "BrightnessPercent": 100 },
                  "Compress": { "OutputFormat": 1, "Quality": 80, "AllowExceedTarget": false, "TargetBytes": 204800, "UseTargetSize": false, "OutputFilenameTemplate": "{index}", "OutputIndex": 1 },
                  "Ui": { "OutputDirectory": "" }
                }
                """);

            var loaded = await new JsonSettingsStore(path).LoadAsync(CancellationToken.None);

            Assert.Equal(SettingsLoadSource.CurrentFile, loaded.Source);
            Assert.Null(loaded.Failure);
            Assert.Equal(16, loaded.Settings.Watermark.Margin);
            Assert.Equal(OutputConflictPolicy.Overwrite, loaded.Settings.Compress.ConflictPolicy);
            Assert.True(loaded.Settings.Processing.AutoOrient);
            Assert.False(loaded.Settings.Processing.StripMetadata);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Settings_store_migrates_legacy_json_to_local_settings_path()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Settings-");
        try
        {
            var legacyPath = Path.Combine(directory.FullName, "NanoPic.settings.json");
            var currentPath = Path.Combine(directory.FullName, "appdata", "settings.json");
            await TestCompatibility.WriteAllTextAsync(legacyPath, """
                {
                  "System": { "MaxThreads": 4, "TopMost": true, "UseGpu": false },
                  "Watermark": { "WatermarkType": 0, "WatermarkAlpha": 80, "WatermarkColor": [1, 2, 3], "WatermarkText": "legacy" },
                  "Resize": { "LimitWidth": 800, "LimitHeight": 600, "ResizeType": 1 },
                  "Graph": { "BackgroundColor": [254, 253, 252], "Brightness": 90 },
                  "Compress": { "ExtensionType": 2, "Quality": 75, "AcceptExceedPicture": true, "LimitSize": 123, "OutputFilename": "{name}_{index}", "OutputIndex": 5, "CompressType": 1 },
                  "Ui": { "OutputDirectory": "D:\\output" }
                }
                """);

            var store = new JsonSettingsStore(currentPath, legacyPath);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.True(loaded.Source == SettingsLoadSource.LegacyMigrated, $"{loaded.Source}: {loaded.Failure?.UserMessage}{Environment.NewLine}{loaded.Failure?.Exception}");
            Assert.Equal(4, loaded.Settings.System.MaxThreads);
            Assert.Equal(ImageOutputFormat.Webp, loaded.Settings.Compress.OutputFormat);
            Assert.True(loaded.Settings.Compress.UseTargetSize);
            Assert.Equal(123L * 1024L, loaded.Settings.Compress.TargetBytes);
            Assert.True(File.Exists(currentPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Settings_store_uses_backup_on_atomic_replace_and_preserves_invalid_current_file()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var store = new JsonSettingsStore(path);
            var firstSave = await store.SaveAsync(NanoPicSettings.Default, CancellationToken.None);
            var changed = NanoPicSettings.Default with { System = new SystemSettings(3, false, false) };
            var secondSave = await store.SaveAsync(changed, CancellationToken.None);

            Assert.True(firstSave.Saved);
            Assert.True(secondSave.Saved);
            Assert.True(File.Exists(store.BackupPath));
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(SettingsLoadSource.CurrentFile, loaded.Source);
            Assert.Equal(3, loaded.Settings.System.MaxThreads);

            await TestCompatibility.WriteAllTextAsync(path, "{ malformed json");
            var invalid = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(SettingsLoadSource.InvalidCurrentFile, invalid.Source);
            Assert.Equal(ImageFailureKind.InvalidConfiguration, invalid.Failure?.Kind);
            Assert.True(File.Exists(store.BackupPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task File_scanner_filters_by_signature_and_output_naming_is_safe()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Scan-");
        try
        {
            var imagePath = Path.Combine(directory.FullName, "misleading.jpg");
            var textPath = Path.Combine(directory.FullName, "not-image.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), imagePath);
            await TestCompatibility.WriteAllTextAsync(textPath, "not an image");

            var scan = await new SupportedImageFileScanner().ScanAsync(directory.FullName, new FileScanOptions(Recursive: true), CancellationToken.None);
            var name = OutputNameTemplate.Render("{name}_{index}_{ext}", imagePath, ImageFormat.Webp, 7);

            var item = Assert.Single(scan.Files);
            Assert.Equal(ImageFormat.Png, item.Format);
            Assert.Equal(imagePath, item.Path);
            Assert.True(name.IsSuccess);
            Assert.Equal("misleading_7_webp.webp", name.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Literal_filename_template_starts_numbering_at_one()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-LiteralName-");
        try
        {
            var imagePath = Path.Combine(directory.FullName, "source.jpg");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), imagePath);

            var first = OutputNameTemplate.Render("luvbp", imagePath, ImageFormat.Jpeg, 1);
            var second = OutputNameTemplate.Render("luvbp", imagePath, ImageFormat.Jpeg, 2);

            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            Assert.Equal("luvbp_1.jpg", first.Value);
            Assert.Equal("luvbp_2.jpg", second.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Date_time_template_uses_source_file_last_write_time_without_index_suffix()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-DateName-");
        try
        {
            var imagePath = Path.Combine(directory.FullName, "vacation.jpg");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), imagePath);
            File.SetLastWriteTime(imagePath, new DateTime(2024, 5, 6, 7, 8, 9));

            var result = OutputNameTemplate.Render("{date}_{time}", imagePath, ImageFormat.Jpeg, 1);

            Assert.True(result.IsSuccess);
            Assert.Equal("2024-05-06_07-08-09.jpg", result.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Auto_rename_conflict_policy_keeps_existing_file_and_uses_unique_output_name()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Conflict-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            var destinationPath = Path.Combine(directory.FullName, "existing.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);
            await TestCompatibility.WriteAllTextAsync(destinationPath, "existing content");

            var service = new ImageFileProcessingService(new WicImageCodec());
            var result = await service.ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Png),
                    new ImageTransformOptions(),
                    ImageSafetyLimits.Default,
                    OutputConflictPolicy.AutoRename),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.EndsWith("existing_1.png", result.Value?.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("existing content", await TestCompatibility.ReadAllTextAsync(destinationPath));
            Assert.True(File.Exists(result.Value?.OutputPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Skip_conflict_policy_keeps_existing_file_and_reports_skipped_result()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Conflict-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            var destinationPath = Path.Combine(directory.FullName, "existing.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath);
            await TestCompatibility.WriteAllTextAsync(destinationPath, "existing content");

            var result = await new ImageFileProcessingService(new WicImageCodec()).ProcessAsync(
                new ImageFileProcessRequest(
                    sourcePath,
                    destinationPath,
                    new ImageEncodingOptions(ImageOutputFormat.Png),
                    new ImageTransformOptions(),
                    ImageSafetyLimits.Default,
                    OutputConflictPolicy.Skip),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.True(result.Value?.SkippedExistingOutput);
            Assert.Null(result.Value?.Output);
            Assert.Equal("existing content", await TestCompatibility.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_batch_with_same_output_path_does_not_fail_with_io_error()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-ConflictConcurrent-");
        try
        {
            var sourceRoot1 = Directory.CreateDirectory(Path.Combine(directory.FullName, "one")).FullName;
            var sourceRoot2 = Directory.CreateDirectory(Path.Combine(directory.FullName, "two")).FullName;
            var sourcePath1 = Path.Combine(sourceRoot1, "same.bmp");
            var sourcePath2 = Path.Combine(sourceRoot2, "same.bmp");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath1);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "transparent.png"), sourcePath2);
            var destinationPath = Path.Combine(directory.FullName, "same.png");

            ImageFileProcessRequest CreateRequest(string sourcePath) => new(
                sourcePath,
                destinationPath,
                new ImageEncodingOptions(ImageOutputFormat.Png),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default,
                OutputConflictPolicy.AutoRename);

            var batch = await new BoundedImageBatchProcessor(new ImageFileProcessingService(new WicImageCodec()))
                .ProcessAsync(
                    new[] { CreateRequest(sourcePath1), CreateRequest(sourcePath2) },
                    2,
                    progress: null,
                    CancellationToken.None);

            Assert.All(batch.Items, item => Assert.True(item.IsSuccess, item.Failure?.UserMessage));
            Assert.Equal(2, batch.Items.Count(item => item.IsSuccess));
            Assert.Equal(2, batch.Items.Select(item => item.Value?.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(batch.Items, item => Assert.DoesNotContain("I/O 错误", item.Failure?.UserMessage ?? string.Empty));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Empty_filename_template_defaults_to_original_file_name()
    {
        var settings = NanoPicSettings.Default;
        var form = SettingsFormMapper.ToForm(settings) with { OutputFilenameTemplate = string.Empty };

        var captured = SettingsFormMapper.Capture(settings, form);

        Assert.Equal("{name}", captured.Compress.OutputFilenameTemplate);
    }

    [Fact]
    public void Output_path_planner_preserves_relative_structure_without_path_escape()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Path-");
        try
        {
            var inputRoot = Path.Combine(directory.FullName, "input");
            var sourceDirectory = Path.Combine(inputRoot, "nested");
            var outputRoot = Path.Combine(directory.FullName, "output");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "image.jpg");
            File.WriteAllBytes(sourcePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

            var preserved = OutputPathPlanner.Plan(new OutputPathPlanRequest(
                sourcePath, inputRoot, outputRoot, OutputDirectoryMode.PreserveDirectoryStructure, "{name}_{index}", ImageFormat.Png, 2));
            var outside = OutputPathPlanner.Plan(new OutputPathPlanRequest(
                sourcePath, Path.Combine(directory.FullName, "other"), outputRoot, OutputDirectoryMode.PreserveDirectoryStructure, "{index}", ImageFormat.Png, 1));

            Assert.True(preserved.IsSuccess);
            Assert.Equal(Path.Combine(outputRoot, "nested", "image_2.png"), preserved.Value);
            Assert.False(outside.IsSuccess);
            Assert.Equal(ImageFailureKind.InvalidConfiguration, outside.Failure?.Kind);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(OutputDirectoryMode.SourceDirectory, true)]
    [InlineData(OutputDirectoryMode.SourceDirectory, false)]
    [InlineData(OutputDirectoryMode.SeparateDirectory, true)]
    [InlineData(OutputDirectoryMode.SeparateDirectory, false)]
    [InlineData(OutputDirectoryMode.PreserveDirectoryStructure, true)]
    [InlineData(OutputDirectoryMode.PreserveDirectoryStructure, false)]
    public void Output_path_planner_uses_selected_extension_for_filename_and_ext_token(
        OutputDirectoryMode mode, bool preserveSourceExtension)
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-OriginalPath-");
        try
        {
            var inputRoot = Path.Combine(directory.FullName, "input");
            var sourceDirectory = Path.Combine(inputRoot, "nested");
            var outputRoot = Path.Combine(directory.FullName, "output");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "image.jfif");
            File.WriteAllBytes(sourcePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

            var result = OutputPathPlanner.Plan(new OutputPathPlanRequest(
                sourcePath, inputRoot, outputRoot, mode, "{name}_{index}_{ext}", ImageFormat.Jpeg, 2,
                PreserveSourceExtension: preserveSourceExtension));

            var expectedDirectory = mode switch
            {
                OutputDirectoryMode.SourceDirectory => sourceDirectory,
                OutputDirectoryMode.SeparateDirectory => outputRoot,
                _ => Path.Combine(outputRoot, "nested")
            };
            var extension = preserveSourceExtension ? "jfif" : "jpg";
            Assert.True(result.IsSuccess, result.Failure?.UserMessage);
            Assert.Equal(Path.Combine(expectedDirectory, $"image_2_{extension}.{extension}"), result.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Default_logger_redacts_file_paths()
    {
        var directory = TestCompatibility.CreateTempSubdirectory("NanoPic-Log-");
        try
        {
            var logPath = Path.Combine(directory.FullName, "app.log");
            var logger = new RedactingFileLogger(logPath);
            await logger.WriteAsync("ERROR", "Failed C:\\Users\\Roz\\Private\\photo.png", new IOException("blocked"), CancellationToken.None);
            var log = await TestCompatibility.ReadAllTextAsync(logPath);

            Assert.Contains("<path>", log);
            Assert.DoesNotContain("C:\\Users\\Roz\\Private\\photo.png", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("blocked", log, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(ImageOutputFormat.Original)]
    [InlineData(ImageOutputFormat.Jpeg)]
    [InlineData(ImageOutputFormat.Png)]
    [InlineData(ImageOutputFormat.Webp)]
    [InlineData(ImageOutputFormat.Gif)]
    [InlineData(ImageOutputFormat.Bmp)]
    [InlineData(ImageOutputFormat.Tiff)]
    [InlineData(ImageOutputFormat.Ico)]
    public void Output_format_round_trips_through_every_combo_index(ImageOutputFormat format)
    {
        var settings = NanoPicSettings.Default with
        {
            Compress = NanoPicSettings.Default.Compress with { OutputFormat = format }
        };

        var form = SettingsFormMapper.ToForm(settings);
        Assert.InRange(form.OutputFormatIndex, 0, 7);
        var captured = SettingsFormMapper.Capture(settings, form);

        Assert.Equal(format, captured.Compress.OutputFormat);
    }

    [Theory]
    [InlineData(200L * 1024L)]
    [InlineData(1536L * 1024L)]
    [InlineData(2L * 1024L * 1024L)]
    [InlineData(16L * 1024L * 1024L)]
    public void Target_size_round_trips_across_kb_and_mb_units(long targetBytes)
    {
        var settings = NanoPicSettings.Default with
        {
            Compress = NanoPicSettings.Default.Compress with { TargetBytes = targetBytes, UseTargetSize = true }
        };

        var form = SettingsFormMapper.ToForm(settings);
        var captured = SettingsFormMapper.Capture(settings, form);

        Assert.Equal(targetBytes, captured.Compress.TargetBytes);
    }

    [Theory]
    [InlineData(0, ImageWatermarkPosition.BottomRight)]
    [InlineData(1, ImageWatermarkPosition.BottomLeft)]
    [InlineData(2, ImageWatermarkPosition.TopRight)]
    [InlineData(3, ImageWatermarkPosition.TopLeft)]
    [InlineData(4, ImageWatermarkPosition.Center)]
    [InlineData(5, ImageWatermarkPosition.Random)]
    public void Watermark_position_round_trips_through_the_combo_index(int index, ImageWatermarkPosition position)
    {
        var settings = NanoPicSettings.Default with
        {
            Watermark = NanoPicSettings.Default.Watermark with { Enabled = true, Position = position }
        };

        var form = SettingsFormMapper.ToForm(settings);
        Assert.Equal(index, form.WatermarkPositionIndex);
        var captured = SettingsFormMapper.Capture(settings, form);
        Assert.Equal(position, captured.Watermark.Position);
        Assert.Equal(position, ImageProcessingOptionsMapper.FromSettings(captured).Transform.Watermark?.Position);
    }

    [Fact]
    public void Metadata_note_round_trips_through_the_form()
    {
        var settings = NanoPicSettings.Default with
        {
            MetadataNote = new MetadataNoteSettings(true, "出处：https://example.com")
        };

        var form = SettingsFormMapper.ToForm(settings);
        Assert.True(form.MetadataNoteEnabled);
        Assert.Equal("出处：https://example.com", form.MetadataNoteText);
        var captured = SettingsFormMapper.Capture(settings, form);
        Assert.True(captured.MetadataNote.Enabled);
        Assert.Equal("出处：https://example.com", captured.MetadataNote.Text);
        Assert.Equal("出处：https://example.com", ImageProcessingOptionsMapper.FromSettings(captured).Transform.MetadataNote?.Text);
    }

    [Fact]
    public void Oversized_image_settings_round_trips_through_the_form()
    {
        var settings = NanoPicSettings.Default with
        {
            OversizedImage = new OversizedImageSettings(SoftMaxPixels: 300_000_000, AutoDownsample: true),
            System = NanoPicSettings.Default.System with { AutoDownscaleOnExceed = true }
        };

        var form = SettingsFormMapper.ToForm(settings);
        Assert.Equal("300", form.SoftMaxPixels);
        Assert.True(form.AutoDownscaleOnExceed);

        var captured = SettingsFormMapper.Capture(settings, form);
        Assert.Equal(300_000_000, captured.OversizedImage.SoftMaxPixels);
        Assert.True(captured.OversizedImage.AutoDownsample);
    }

    [Fact]
    public void Oversized_image_settings_round_trips_with_auto_downscale_off()
    {
        var settings = NanoPicSettings.Default with
        {
            OversizedImage = new OversizedImageSettings(SoftMaxPixels: 100_000_000, AutoDownsample: false),
            System = NanoPicSettings.Default.System with { AutoDownscaleOnExceed = false }
        };

        var form = SettingsFormMapper.ToForm(settings);
        Assert.Equal("100", form.SoftMaxPixels);
        Assert.False(form.AutoDownscaleOnExceed);

        var captured = SettingsFormMapper.Capture(settings, form);
        Assert.Equal(100_000_000, captured.OversizedImage.SoftMaxPixels);
        Assert.False(captured.OversizedImage.AutoDownsample);
    }

    [Fact]
    public void Settings_validator_rejects_soft_max_pixels_below_minimum()
    {
        var settings = NanoPicSettings.Default with
        {
            OversizedImage = new OversizedImageSettings(
                SoftMaxPixels: OversizedImageSettings.MinSoftMaxPixels - 1,
                AutoDownsample: true)
        };

        var failure = SettingsValidator.Validate(settings);
        Assert.NotNull(failure);
        Assert.Equal(ImageFailureKind.InvalidConfiguration, failure.Kind);
    }

    [Fact]
    public void Settings_validator_rejects_soft_max_pixels_above_maximum()
    {
        var settings = NanoPicSettings.Default with
        {
            OversizedImage = new OversizedImageSettings(
                SoftMaxPixels: OversizedImageSettings.MaxSoftMaxPixels + 1,
                AutoDownsample: true)
        };

        var failure = SettingsValidator.Validate(settings);
        Assert.NotNull(failure);
        Assert.Equal(ImageFailureKind.InvalidConfiguration, failure.Kind);
    }

    [Fact]
    public void Settings_validator_accepts_valid_soft_max_pixels()
    {
        var settings = NanoPicSettings.Default with
        {
            OversizedImage = new OversizedImageSettings(
                SoftMaxPixels: 200_000_000,
                AutoDownsample: true)
        };

        var failure = SettingsValidator.Validate(settings);
        Assert.Null(failure);
    }
}
