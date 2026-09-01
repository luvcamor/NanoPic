using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using Microsoft.Win32;
using NanoPic.Codecs;
using NanoPic.Core;
using NanoPic.Infrastructure;

namespace NanoPic.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<QueueItem> _items = new();
    private readonly HashSet<string> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SupportedImageFileScanner _scanner = new();
    private readonly ImageFileProcessingService _processor = new(new WicImageCodec());
    private readonly WicImageCodec _previewCodec = new();
    private readonly BoundedImageBatchProcessor _batchProcessor;
    private readonly IImageProcessingCapabilityProvider _capabilities = new DefaultImageProcessingCapabilityProvider();
    private readonly JsonSettingsStore _settingsStore = new(JsonSettingsStore.GetDefaultSettingsPath());
    private readonly RedactingFileLogger _logger = new(GetDefaultLogPath());
    private readonly QueueImportCoordinator _importCoordinator;
    private CancellationTokenSource? _runCancellation;
    private NanoPicSettings _settings = NanoPicSettings.Default;
    private Task _settingsLoadTask = Task.CompletedTask;
    private bool _closeInProgress;
    private bool _closeCommitted;
    private readonly Dictionary<string, Window> _previewWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentQueue<ShellAddRequest> _pendingShellRequests = new();
    private readonly ShellIntegrationHost? _shellHost;
    private bool _shellIntegrationUiUpdating;
    private bool _shellRequestDrainScheduled;
    private ShellIntegrationState? _shellIntegrationState;

    public MainWindow()
        : this(null)
    {
    }

    internal MainWindow(ShellIntegrationHost? shellHost)
    {
        _shellHost = shellHost;
        _batchProcessor = new BoundedImageBatchProcessor(_processor);
        _importCoordinator = new QueueImportCoordinator(_scanner, new QueueSink(this));
        _items.CollectionChanged += Items_CollectionChanged;
        InitializeComponent();
        QueueGrid.ItemsSource = _items;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Activated += MainWindow_Activated;
        WatermarkColorBox.TextChanged += (_, _) => UpdateWatermarkColorSwatch();
        UpdateWatermarkColorSwatch();
        if (_shellHost is not null)
        {
            _shellHost.WindowReceiver = ReceiveShellRequest;
        }

        RefreshUi();
    }

    private void MainWindow_Activated(object? sender, EventArgs e) => _shellHost?.ReportActivated();

    /// <summary>队列的规范化路径索引：随集合增删同步，供导入协调器做 O(1) 去重判断。</summary>
    private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _queuedPaths.Clear();
            foreach (var item in _items)
            {
                if (QueueImportCoordinator.TryNormalize(item.Path, out var normalized))
                {
                    _queuedPaths.Add(normalized);
                }
            }

            return;
        }

        foreach (var removed in e.OldItems?.Cast<QueueItem>() ?? Enumerable.Empty<QueueItem>())
        {
            if (QueueImportCoordinator.TryNormalize(removed.Path, out var normalized))
            {
                _queuedPaths.Remove(normalized);
            }
        }

        foreach (var addedItem in e.NewItems?.Cast<QueueItem>() ?? Enumerable.Empty<QueueItem>())
        {
            if (QueueImportCoordinator.TryNormalize(addedItem.Path, out var normalized))
            {
                _queuedPaths.Add(normalized);
            }
        }
    }

    private sealed class QueueSink : IQueueImportSink
    {
        private readonly MainWindow _owner;

        public QueueSink(MainWindow owner) => _owner = owner;

        public bool Contains(string normalizedPath) => _owner._queuedPaths.Contains(normalizedPath);

        public void Add(string path, long bytes) => _owner._items.Add(new QueueItem(path, bytes));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settingsLoadTask = LoadSettingsAsync();
        await _settingsLoadTask;
        // 设置与窗口初始化完成后才处理 Shell 请求与注册状态，避免与设置加载竞争。
        await InitializeShellIntegrationAsync();
        DrainPendingShellRequests();
    }

    /// <summary>
    /// 由 broker 或本进程的 DropTarget 调用，可能来自管道线程。
    /// 请求在这里被复制进队列即视为已接收（ACK），不等待签名扫描与缩略图处理。
    /// </summary>
    internal bool ReceiveShellRequest(ShellAddRequest request)
    {
        if (request is null)
        {
            return false;
        }

        if (!Dispatcher.CheckAccess())
        {
            try
            {
                return Dispatcher.Invoke(new Func<bool>(() => EnqueueShellRequest(request)));
            }
            catch (Exception exception) when (exception is TaskCanceledException or InvalidOperationException)
            {
                return false;
            }
        }

        return EnqueueShellRequest(request);
    }

    /// <summary>在 Dispatcher 上原子判断窗口生命周期并入队，避免关闭期间先 ACK 后丢请求。</summary>
    private bool EnqueueShellRequest(ShellAddRequest request)
    {
        if (_closeInProgress || _closeCommitted || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        _pendingShellRequests.Enqueue(request);
        if (!_shellRequestDrainScheduled)
        {
            _shellRequestDrainScheduled = true;
            Dispatcher.BeginInvoke(new Action(DrainPendingShellRequests));
        }

        return true;
    }

    private async void DrainPendingShellRequests()
    {
        _shellRequestDrainScheduled = false;
        // 导入必须发生在设置加载之后，避免与 Loaded 阶段的初始化竞争。
        await _settingsLoadTask;
        while (_pendingShellRequests.TryDequeue(out var request))
        {
            if (request.ActivateWindow)
            {
                RestoreAndActivateWindow();
            }

            await ImportAsync(new ImportRequest(
                request.Paths,
                ImportSource.ShellContextMenu,
                AllowDirectories: false,
                request.UnavailableItemCount));
        }
    }

    /// <summary>收到显式 Shell 请求后恢复窗口；Windows 拒绝前台切换时退化为任务栏提示。</summary>
    private void RestoreAndActivateWindow()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (Activate())
        {
            return;
        }

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                NativeWindowAttention.FlashTaskbar(handle);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private async Task LoadSettingsAsync()
    {
        var load = await _settingsStore.LoadAsync(CancellationToken.None);
        _settings = load.Settings;
        ApplySettings(_settings);
        ConfigureGpuCapability(_settings.System.UseGpu);
        if (load.Failure is not null)
        {
            System.Windows.MessageBox.Show(this, load.Failure.UserMessage, "配置加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeCommitted)
        {
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            if (_runCancellation is not null)
            {
                _runCancellation.Cancel();
            }

            await _settingsLoadTask;
            var save = await _settingsStore.SaveAsync(CaptureSettings(), CancellationToken.None);
            if (!save.Saved && save.Failure is not null)
            {
                System.Windows.MessageBox.Show(this, save.Failure.UserMessage, "配置保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _closeCommitted = true;
            if (_shellHost is not null)
            {
                _shellHost.WindowReceiver = null;
            }
            Close();
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "图像文件|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.ico|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportAsync(new ImportRequest(dialog.FileNames, ImportSource.FilePicker));
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要导入的图片文件夹" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            await ImportAsync(new ImportRequest(new[] { dialog.SelectedPath }, ImportSource.FolderPicker));
        }
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        // 处理中也允许拖入：新文件排入队列等待下一批，与快捷键、按钮入口保持一致。
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            await ImportAsync(new ImportRequest(paths, ImportSource.DragDrop));
        }
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e) => e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;

    /// <summary>所有导入入口统一走这里：协调器负责去重与分类，窗口只负责反馈。</summary>
    private async Task<ImportSummary> ImportAsync(ImportRequest request)
    {
        ImportSummary summary;
        try
        {
            summary = await _importCoordinator.ImportAsync(request, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ProgressText.Text = "导入过程中发生读取错误，详情见日志。";
            await WriteLogAsync("ERROR", $"导入失败：{exception.Message}", exception);
            return new ImportSummary(0, 0, Array.Empty<ImportIssue>());
        }

        await ReportImportSummaryAsync(request, summary);
        RefreshUi();
        return summary;
    }

    private async Task ReportImportSummaryAsync(ImportRequest request, ImportSummary summary)
    {
        if (summary.Added == 0 && summary.Duplicated == 0 && summary.Issues.Count == 0)
        {
            return;
        }

        ProgressText.Text = summary.BuildStatusText();
        foreach (var issue in summary.Issues)
        {
            await WriteLogAsync(
                "WARN",
                $"导入跳过（{ImportSummary.DescribeKind(issue.Kind)}）：{issue.Path}。{issue.Message}",
                null);
        }

        await WriteLogAsync(
            "INFO",
            $"导入完成（来源 {request.Source}）：新增 {summary.Added}，重复 {summary.Duplicated}，跳过 {summary.Issues.Count}。",
            null);

        if (summary.IsCompleteFailure)
        {
            System.Windows.MessageBox.Show(
                this,
                $"没有文件被加入队列：{summary.BuildIssueSummary()}。\n\n详细原因已写入日志。",
                "导入未完成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var item in _items) item.IsSelected = true; RefreshUi(); }
    private void InvertSelection_Click(object sender, RoutedEventArgs e) { foreach (var item in _items) item.IsSelected = !item.IsSelected; RefreshUi(); }
    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is not null) return;
        foreach (var item in _items.Where(item => item.IsSelected).ToArray()) _items.Remove(item);
        RefreshUi();
    }
    private void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is not null) return;
        foreach (var item in _items.Where(item => item.HasFailure)) { item.MarkPending(); item.IsSelected = true; }
        RefreshUi();
    }

    private void QueueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PreviewHighlighted();

    private void QueueGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(QueueGrid, e.OriginalSource as DependencyObject) is DataGridRow row && !row.IsSelected)
        {
            QueueGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    private void PreviewHighlighted_Click(object sender, RoutedEventArgs e) => PreviewHighlighted();
    private void CheckHighlighted_Click(object sender, RoutedEventArgs e) => SetHighlightedSelection(true);
    private void UncheckHighlighted_Click(object sender, RoutedEventArgs e) => SetHighlightedSelection(false);
    private void RemoveHighlighted_Click(object sender, RoutedEventArgs e) => RemoveHighlighted();
    private void RetryHighlighted_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is not null) return;
        foreach (var item in GetHighlightedItems().Where(item => item.HasFailure)) { item.MarkPending(); item.IsSelected = true; }
        RefreshUi();
    }

    private void OpenContainingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not QueueItem item || !File.Exists(item.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            System.Windows.MessageBox.Show(this, "打开所在文件夹失败。", "无法打开文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetHighlightedSelection(bool isSelected)
    {
        if (_runCancellation is not null) return;
        foreach (var item in GetHighlightedItems()) item.IsSelected = isSelected;
        RefreshUi();
    }

    private void RemoveHighlighted()
    {
        if (_runCancellation is not null) return;
        foreach (var item in GetHighlightedItems()) _items.Remove(item);
        RefreshUi();
    }

    private QueueItem[] GetHighlightedItems() => QueueGrid.SelectedItems.Cast<QueueItem>().ToArray();

    private async void PreviewHighlighted()
    {
        if (QueueGrid.SelectedItem is not QueueItem item || !File.Exists(item.Path)) return;
        if (_previewWindows.TryGetValue(item.Path, out var existingPreview))
        {
            existingPreview.Activate();
            return;
        }
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"nanopic-preview-{Guid.NewGuid():N}.png");
        try
        {
            var effectiveSettings = CaptureSettings();
            var safetyLimits = ImageSafetyLimits.Default with
            {
                MaxPixels = effectiveSettings.OversizedImage.SoftMaxPixels,
                AutoDownscaleOnExceed = effectiveSettings.System.AutoDownscaleOnExceed
            };

            ImageFormat sourceFormat;
            ImageHeaderInfo headerInfo;

            using (var stream = new FileStream(
                PortablePath.ForFileSystem(item.Path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65_536,
                useAsync: false))
            {
                if (stream.Length > safetyLimits.MaxSourceBytes)
                {
                    System.Windows.MessageBox.Show(this, "图像文件大小超过安全处理上限，无法预览。", "预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var detected = await ImageFileSignatureInspector.DetectAsync(stream, CancellationToken.None).ConfigureAwait(true);
                if (!detected.IsSuccess || detected.Value == ImageFormat.Unknown)
                {
                    System.Windows.MessageBox.Show(this, "无法识别所选图片的格式。", "预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                sourceFormat = detected.Value;
                var probeResult = ImageDimensionProbe.Probe(stream, sourceFormat);
                if (!probeResult.IsSuccess || probeResult.Value is null)
                {
                    System.Windows.MessageBox.Show(this, "未能解析图像头部尺寸，无法安全预览。", "预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                headerInfo = probeResult.Value;
            }

            var swap = headerInfo.ExifOrientation is >= 5 and <= 8;
            var plannedWidth = swap ? headerInfo.Height : headerInfo.Width;
            var plannedHeight = swap ? headerInfo.Width : headerInfo.Height;

            var preMetadata = new NanoPic.Core.ImageMetadata(
                sourceFormat,
                plannedWidth,
                plannedHeight,
                headerInfo.FrameCount ?? 1,
                HasAlpha: false,
                SourceBytes: item.Bytes,
                ExifOrientation: headerInfo.ExifOrientation);

            var safetyResult = ImageSafetyValidator.ValidateWithAction(preMetadata, safetyLimits);
            if (safetyResult.Action == SafetyAction.Reject)
            {
                var msg = safetyResult.Failure?.UserMessage ?? "图像尺寸超过安全上限，无法预览。";
                System.Windows.MessageBox.Show(this, msg, "预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var safeWidth = safetyResult.TargetWidth ?? plannedWidth;
            var safeHeight = safetyResult.TargetHeight ?? plannedHeight;

            const int previewMaxEdge = 2048;
            int previewTargetWidth = safeWidth;
            int previewTargetHeight = safeHeight;
            if (previewTargetWidth > previewMaxEdge || previewTargetHeight > previewMaxEdge)
            {
                var ratio = Math.Min((double)previewMaxEdge / previewTargetWidth, (double)previewMaxEdge / previewTargetHeight);
                previewTargetWidth = Math.Max(1, (int)Math.Round(previewTargetWidth * ratio));
                previewTargetHeight = Math.Max(1, (int)Math.Round(previewTargetHeight * ratio));
            }

            var request = new ImageEncodeRequest(
                item.Path,
                temporaryPath,
                sourceFormat,
                ImageFormat.Png,
                new ImageTransformOptions(
                    AutoOrient: true,
                    Resize: new ImageResizeOptions(
                        Enabled: true,
                        Width: previewTargetWidth,
                        Height: previewTargetHeight,
                        PreserveAspectRatio: true)),
                new ImageEncodingOptions(ImageOutputFormat.Png),
                ImageSafetyLimits.Default with
                {
                    MaxWidth = int.MaxValue,
                    MaxHeight = int.MaxValue,
                    MaxPixels = long.MaxValue,
                    AutoDownscaleOnExceed = true
                });

            var encoded = await Task.Run(() => _previewCodec.TransformAndEncodeAsync(request, CancellationToken.None))
                .ConfigureAwait(true);
            if (!encoded.IsSuccess)
            {
                System.Windows.MessageBox.Show(this, "无法预览所选图片。", "预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (encoded.Value?.MetadataFallbackLevel > ImageMetadataFallbackLevel.Full)
            {
                await WriteLogAsync(
                    "WARN",
                    $"预览已自动清理不兼容元数据（{encoded.Value.MetadataFallbackLevel}）。{encoded.Value.MetadataFallbackNotice}",
                    null);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(temporaryPath);
            image.EndInit();
            image.Freeze();
            var preview = new Window
            {
                Title = item.FileName,
                Width = 800,
                Height = 600,
                MinWidth = 480,
                MinHeight = 360,
                Owner = this,
                Background = System.Windows.Media.Brushes.White,
                Content = new System.Windows.Controls.Image { Source = image, Stretch = Stretch.Uniform }
            };
            System.Windows.Automation.AutomationProperties.SetName(preview, $"图片预览：{item.FileName}");
            preview.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    preview.Close();
                }
            };
            _previewWindows[item.Path] = preview;
            preview.Closed += (_, _) => _previewWindows.Remove(item.Path);
            preview.Show();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Windows.MessageBox.Show(this, "无法预览所选图片。", "预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var action = KeyboardShortcutRouter.Resolve(new ApplicationShortcutContext(
            MapShortcutKey(e.Key),
            (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
            Keyboard.FocusedElement is WpfTextBoxBase or WpfComboBox,
            QueueGrid.IsKeyboardFocusWithin,
            _runCancellation is not null,
            StartButton.IsEnabled));

        switch (action)
        {
            case ApplicationShortcutAction.Cancel:
                _runCancellation?.Cancel();
                break;
            case ApplicationShortcutAction.Start:
                Start_Click(StartButton, new RoutedEventArgs());
                break;
            case ApplicationShortcutAction.AddFiles:
                AddFiles_Click(this, new RoutedEventArgs());
                break;
            case ApplicationShortcutAction.AddFolder:
                AddFolder_Click(this, new RoutedEventArgs());
                break;
            case ApplicationShortcutAction.SelectAll:
                SelectAll_Click(this, new RoutedEventArgs());
                break;
            case ApplicationShortcutAction.InvertSelection:
                InvertSelection_Click(this, new RoutedEventArgs());
                break;
            case ApplicationShortcutAction.RemoveHighlighted:
                RemoveHighlighted();
                break;
            case ApplicationShortcutAction.PreviewHighlighted:
                PreviewHighlighted();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static ApplicationShortcutKey MapShortcutKey(Key key) => key switch
    {
        Key.O => ApplicationShortcutKey.O,
        Key.A => ApplicationShortcutKey.A,
        Key.I => ApplicationShortcutKey.I,
        Key.Delete => ApplicationShortcutKey.Delete,
        Key.Enter => ApplicationShortcutKey.Enter,
        Key.F5 => ApplicationShortcutKey.F5,
        Key.Escape => ApplicationShortcutKey.Escape,
        _ => ApplicationShortcutKey.None
    };

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0 && _items.Count > 0)
        {
            System.Windows.MessageBox.Show(this, "请至少勾选一个要处理的文件。", "NanoPic", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selected.Length == 0) { System.Windows.MessageBox.Show(this, "请先添加图片文件。", "NanoPic", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!TryBuildOptions(out var encoding, out var transforms, out var failure)) { System.Windows.MessageBox.Show(this, failure, "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var outputRoot = OutputDirectoryBox.Text.Trim();
        if (!OverwriteOutput.IsChecked.GetValueOrDefault() && string.IsNullOrWhiteSpace(outputRoot)) { System.Windows.MessageBox.Show(this, "请选择输出目录。", "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var effectiveSettings = CaptureSettings();
        var settingsFailure = SettingsValidator.Validate(effectiveSettings);
        if (settingsFailure is not null)
        {
            System.Windows.MessageBox.Show(this, settingsFailure.UserMessage, "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var inputRoot = PreserveOutput.IsChecked.GetValueOrDefault()
            ? InputRootDirectoryResolver.FindCommonDirectory(selected.Select(item => item.Path))
            : null;
        if (PreserveOutput.IsChecked.GetValueOrDefault() && inputRoot is null)
        {
            System.Windows.MessageBox.Show(this, "所选文件没有可用的公共输入目录，无法保留文件夹结构。", "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var jobs = new List<(QueueItem Item, ImageFileProcessRequest Request)>();
        var planningFailures = 0;
        for (var i = 0; i < selected.Length; i++)
        {
            var item = selected[i];
            var output = BuildOutputPath(
                item.Path,
                inputRoot,
                outputRoot,
                encoding.OutputFormat,
                effectiveSettings.Compress.OutputFilenameTemplate,
                effectiveSettings.Compress.OutputIndex + i);
            if (!output.IsSuccess || output.Value is null)
            {
                item.MarkFailure(output.Failure?.Kind ?? ImageFailureKind.InvalidConfiguration, output.Failure?.UserMessage ?? "无法规划输出路径。");
                planningFailures++;
                await WriteLogAsync("ERROR", $"输出路径规划失败：{item.Path}。{item.Detail}", output.Failure?.Exception);
                continue;
            }

            item.MarkProcessing();
            jobs.Add((item, new ImageFileProcessRequest(
                item.Path,
                output.Value,
                encoding,
                transforms,
                ImageSafetyLimits.Default with
                {
                    MaxPixels = effectiveSettings.OversizedImage.SoftMaxPixels,
                    AutoDownscaleOnExceed = effectiveSettings.System.AutoDownscaleOnExceed
                },
                OverwriteOutput.IsChecked.GetValueOrDefault() ? OutputConflictPolicy.Overwrite : effectiveSettings.Compress.ConflictPolicy)));
        }

        _runCancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false; CancelButton.IsEnabled = true; QueueEditPanel.IsEnabled = false; Progress.Visibility = Visibility.Visible; Progress.Value = 0;
        try
        {
            // Pre-scan image metadata using header probe to compute oversized-image concurrency limits.
            var pixelCounts = await PreScanPixelCountsAsync(jobs, _runCancellation.Token);

            var progress = new Progress<ImageBatchProgress>(value =>
            {
                Progress.Value = selected.Length == 0 ? 0 : (value.Completed + planningFailures) * 100d / selected.Length;
                ProgressText.Text = $"成功 {value.Succeeded}，失败 {value.Failed + planningFailures}，取消 {value.Canceled}，最大并发 {effectiveSettings.System.MaxThreads}";
            });

            var batch = await _batchProcessor.ProcessAsync(
                jobs.Select(job => job.Request).ToArray(),
                effectiveSettings.System.MaxThreads,
                pixelCounts,
                progress,
                _runCancellation.Token);

            for (var i = 0; i < jobs.Count; i++)
            {
                var item = jobs[i].Item;
                var result = batch.Items[i];
                if (result.IsSuccess)
                {
                    var processResult = result.Value;
                    item.MarkSuccess(
                        processResult?.Output?.ExceededTarget == true,
                        processResult?.OutputPath,
                        processResult?.SkippedExistingOutput == true,
                        processResult?.FrameNotice,
                        processResult?.Output?.MetadataFallbackNotice);
                    if (processResult is not null)
                    {
                        item.Detail = BuildSuccessDetail(processResult);
                        if (processResult.Output?.MetadataFallbackLevel > ImageMetadataFallbackLevel.Full)
                        {
                            await WriteLogAsync(
                                "WARN",
                                $"图像编码已自动清理不兼容元数据（{processResult.Output.MetadataFallbackLevel}）。{processResult.Output.MetadataFallbackNotice}",
                                null);
                        }
                        if (string.Equals(processResult.OutputPath, item.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            // 覆盖原文件模式：输出写回了源文件本身，刷新队列中的大小显示。
                            try
                            {
                                item.UpdateBytes(new FileInfo(processResult.OutputPath).Length);
                            }
                            catch (IOException)
                            {
                            }
                            catch (UnauthorizedAccessException)
                            {
                            }
                        }
                    }
                }
                else
                {
                    var failureResult = result.Failure ?? new ImageOperationFailure(ImageFailureKind.DecodeFailed, "处理失败，但没有返回详细原因。");
                    item.MarkFailure(failureResult.Kind, failureResult.UserMessage);
                    await WriteLogAsync("ERROR", $"图像处理失败：{item.Path}。{failureResult.UserMessage}", failureResult.Exception);
                }
            }

            var metadataFallbacks = batch.Items
                .Where(result => result.IsSuccess && result.Value?.Output?.MetadataFallbackLevel > ImageMetadataFallbackLevel.Full)
                .Select(result => result.Value!.Output!.MetadataFallbackLevel)
                .ToArray();
            var metadataFallbackSummary = metadataFallbacks.Length == 0
                ? string.Empty
                : $"，元数据清理 {metadataFallbacks.Length}";
            Progress.Value = 100;
            ProgressText.Text = $"成功 {batch.Progress.Succeeded}，失败 {batch.Progress.Failed + planningFailures}，取消 {batch.Progress.Canceled}{metadataFallbackSummary}，共 {selected.Length}";
            var levelSummary = metadataFallbacks.Length == 0
                ? ""
                : "，降级分布 " + string.Join(", ", metadataFallbacks
                    .GroupBy(level => level)
                    .OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}={group.Count()}"));
            await WriteLogAsync("INFO", $"批处理完成：总数 {selected.Length}，成功 {batch.Progress.Succeeded}，失败 {batch.Progress.Failed + planningFailures}，取消 {batch.Progress.Canceled}{levelSummary}。", null);
        }
        catch (OperationCanceledException)
        {
            foreach (var item in selected.Where(i => i.IsProcessing || i.IsPending))
            {
                item.MarkCanceled();
            }
            ProgressText.Text = "批处理已取消。";
            await WriteLogAsync("INFO", "批处理已被用户取消。", null);
        }
        catch (OutOfMemoryException oom)
        {
            foreach (var item in selected.Where(i => i.IsProcessing || i.IsPending))
            {
                item.MarkFailure(ImageFailureKind.PixelBudgetExceeded, "系统内存不足，已停止后续处理。");
            }
            ProgressText.Text = "系统内存不足，批处理已停止。";
            await WriteLogAsync("ERROR", "批处理因系统内存不足停止。", oom);
            System.Windows.MessageBox.Show(this, "系统可用内存不足，已中止批处理操作。", "内存不足", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception)
        {
            foreach (var item in selected.Where(i => i.IsProcessing || i.IsPending))
            {
                item.MarkFailure(ImageFailureKind.Unknown, "批处理发生未预期错误。");
            }
            ProgressText.Text = "批处理发生错误。";
            await WriteLogAsync("ERROR", "批处理发生未捕获异常。", exception);
            System.Windows.MessageBox.Show(this, "处理过程中发生未预期错误，请查看日志了解详情。", "处理失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            QueueEditPanel.IsEnabled = true;
            RefreshUi();
        }
    }

    private async Task WriteLogAsync(string level, string message, Exception? exception)
    {
        try
        {
            await _logger.WriteAsync(level, message, exception, CancellationToken.None);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ConfigureGpuCapability(bool requested)
    {
        var gpu = _capabilities.Get(ImageProcessingCapability.GpuAcceleration);
        GpuCheck.IsEnabled = gpu.IsAvailable;
        GpuCheck.IsChecked = gpu.IsAvailable && requested;
        GpuCheck.Content = gpu.IsAvailable ? "GPU 加速" : "GPU 当前不可用（使用 CPU）";
        GpuCheck.ToolTip = gpu.Message;
        if (requested && !gpu.IsAvailable)
        {
            ProgressText.Text = gpu.Message;
        }
    }

    private static string BuildSuccessDetail(ImageFileProcessResult result)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.FrameNotice))
        {
            details.Add(result.FrameNotice!);
        }
        if (result.AutoDownsampled && !string.IsNullOrWhiteSpace(result.ResizeNotice))
        {
            details.Add(result.ResizeNotice!);
        }
        if (result.TargetSizeResized && !string.IsNullOrWhiteSpace(result.TargetSizeNotice))
        {
            details.Add(result.TargetSizeNotice!);
        }
        if (result.Output?.MetadataFallbackNotice is { } metadataFallbackNotice &&
            !string.IsNullOrWhiteSpace(metadataFallbackNotice))
        {
            details.Add(metadataFallbackNotice);
        }
        if (details.Count == 0)
        {
            details.Add(result.OutputPath);
        }
        return string.Join(Environment.NewLine, details);
    }

    private static string GetDefaultLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NanoPic",
        "logs",
        "NanoPic.log");

    private async Task<IReadOnlyList<long>> PreScanPixelCountsAsync(
        IReadOnlyList<(QueueItem Item, ImageFileProcessRequest Request)> jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0) return Array.Empty<long>();

        var pixelCounts = new long[jobs.Count];
        var semaphore = new SemaphoreSlim(4);
        var tasks = new List<Task>(jobs.Count);

        for (var i = 0; i < jobs.Count; i++)
        {
            var index = i;
            var path = jobs[index].Item.Path;
            var format = jobs[index].Request.Encoding.OutputFormat.ToImageFormat();

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var stream = new FileStream(
                        PortablePath.ForFileSystem(path),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 65_536,
                        useAsync: false);

                    var detected = await ImageFileSignatureInspector.DetectAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (detected.IsSuccess && detected.Value != ImageFormat.Unknown)
                    {
                        var probeResult = ImageDimensionProbe.Probe(stream, detected.Value);
                        if (probeResult.IsSuccess && probeResult.Value is { } info)
                        {
                            pixelCounts[index] = (long)info.Width * info.Height;
                            return;
                        }
                    }

                    // Probe failed or unsupported: treat as highest risk
                    pixelCounts[index] = long.MaxValue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Fallback to max risk to ensure safe conservative concurrency
                    pixelCounts[index] = long.MaxValue;
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return pixelCounts;
    }

    private ImageOperationResult<string> BuildOutputPath(
        string sourcePath,
        string? inputRoot,
        string outputRoot,
        ImageOutputFormat requested,
        string filenameTemplate,
        int index)
    {
        var format = requested == ImageOutputFormat.Original ? DetectFormatForPath(sourcePath) : requested.ToImageFormat();
        if (format == ImageFormat.Unknown) return ImageOperationResult<string>.Failed(ImageFailureKind.UnsupportedFormat, "无法识别输入文件格式。");
        var overwrite = OverwriteOutput.IsChecked.GetValueOrDefault();
        var mode = overwrite ? OutputDirectoryMode.SourceDirectory : PreserveOutput.IsChecked.GetValueOrDefault() ? OutputDirectoryMode.PreserveDirectoryStructure : OutputDirectoryMode.SeparateDirectory;
        return OutputPathPlanner.Plan(new OutputPathPlanRequest(
            sourcePath, inputRoot, outputRoot, mode, overwrite ? "{name}" : filenameTemplate, format, index,
            PreserveSourceExtension: requested == ImageOutputFormat.Original));
    }

    private static ImageFormat DetectFormatForPath(string path)
    {
        // 同步读取 32 字节文件头做签名识别：此前对 UI 线程做异步阻塞（GetAwaiter().GetResult()），
        // 在慢速网络路径上会冻结界面；签名检测本身有同步 API，直接调用即可。
        using var stream = File.OpenRead(path);
        var header = new byte[32];
        var read = 0;
        while (read < header.Length)
        {
            var count = stream.Read(header, read, header.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read < header.Length)
        {
            Array.Resize(ref header, read);
        }

        var detected = ImageFileSignatureInspector.Detect(header);
        return detected.IsSuccess ? detected.Value : ImageFormat.Unknown;
    }

    private bool TryBuildOptions(out ImageEncodingOptions encoding, out ImageTransformOptions transforms, out string failure)
    {
        failure = string.Empty; encoding = default!; transforms = default!;
        var useTargetSize = TargetSizeMode.IsChecked == true;
        if (!useTargetSize && (!int.TryParse(QualityBox.Text, out var quality) || quality is < 1 or > 100))
        { failure = "质量必须是 1 到 100 之间的整数。"; return false; }
        var targetValue = 1L;
        var targetUnitBytes = TargetSizeUnitBox.SelectedIndex == 1 ? 1024L * 1024 : 1024L;
        if (useTargetSize && (!long.TryParse(TargetSizeBox.Text, out targetValue) || targetValue <= 0 || targetValue > long.MaxValue / targetUnitBytes))
        { failure = "目标大小必须是大于 0 的整数。"; return false; }
        var width = 1; var height = 1;
        if (ResizeCheck.IsChecked == true && (!int.TryParse(WidthBox.Text, out width) || width < 1 || !int.TryParse(HeightBox.Text, out height) || height < 1))
        { failure = "启用缩放时，宽度和高度必须是大于 0 的整数。"; return false; }
        var brightness = 100;
        if (BrightnessCheck.IsChecked == true && (!int.TryParse(BrightnessBox.Text, out brightness) || brightness is < 0 or > 200))
        { failure = "启用亮度调整时，亮度必须是 0 到 200 之间的整数。"; return false; }
        if (WatermarkCheck.IsChecked == true && string.IsNullOrWhiteSpace(WatermarkBox.Text))
        { failure = "启用文字水印时，请输入水印文字。"; return false; }
        if (!int.TryParse(OutputIndexBox.Text, out var outputIndex) || outputIndex < 1)
        { failure = "输出起始序号必须是大于 0 的整数。"; return false; }
        var filenameTemplate = string.IsNullOrWhiteSpace(FilenameTemplateBox.Text)
            ? "{name}"
            : FilenameTemplateBox.Text.Trim();
        if (filenameTemplate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || filenameTemplate.Contains(Path.DirectorySeparatorChar) || filenameTemplate.Contains(Path.AltDirectorySeparatorChar))
        { failure = "输出命名模板不能为空，也不能包含路径或无效文件名字符。"; return false; }
        if (!IsRgbHex(BackgroundColorBox.Text))
        { failure = "透明背景颜色必须使用 #RRGGBB 格式。"; return false; }
        if (!IsRgbHex(WatermarkColorBox.Text))
        { failure = "水印颜色必须使用 #RRGGBB 格式。"; return false; }
        if (!int.TryParse(WatermarkOpacityBox.Text, out var watermarkOpacity) || watermarkOpacity is < 0 or > 100)
        { failure = "水印透明度必须是 0 到 100 之间的整数。"; return false; }
        if (!int.TryParse(WatermarkFontSizeBox.Text, out var watermarkFontSize) || watermarkFontSize is < 4 or > 256)
        { failure = "水印字号必须是 4 到 256 之间的整数。"; return false; }
        if (!int.TryParse(WatermarkMarginBox.Text, out var watermarkMargin) || watermarkMargin is < 0 or > 4096)
        { failure = "水印边距必须是 0 到 4096 之间的整数。"; return false; }
        var options = ImageProcessingOptionsMapper.FromSettings(CaptureSettings());
        encoding = options.Encoding;
        transforms = options.Transform;
        return true;
    }

    private static bool IsRgbHex(string value) =>
        value.Length == 7 && value[0] == '#' && value.Substring(1).All(Uri.IsHexDigit);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _runCancellation?.Cancel();
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) => ChooseOutputDirectory();
    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var candidate = OutputDirectoryBox.Text.Trim();
        string? fullPath = null;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
        }

        if (fullPath is null || !Directory.Exists(fullPath))
        {
            System.Windows.MessageBox.Show(this, "当前输出目录不存在，请先选择有效目录。", "无法打开输出目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            System.Windows.MessageBox.Show(this, "打开输出目录失败，请确认目录可以访问。", "无法打开输出目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = CreateAboutWindow();
        about.Owner = this;
        about.ShowDialog();
    }

    private Window CreateAboutWindow()
    {
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
            ?? "未知";
        var link = new Hyperlink(new Run("GitHub Issues（github.com/luvcamor/NanoPic/issues）"))
        {
            NavigateUri = new Uri("https://github.com/luvcamor/NanoPic/issues")
        };
        link.RequestNavigate += (senderLink, eventArgs) =>
        {
            eventArgs.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(eventArgs.Uri.ToString()) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
            {
                System.Windows.MessageBox.Show(this, "打开链接失败，请手动访问 GitHub Issues。", "无法打开链接", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        var content = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
        content.Children.Add(new TextBlock
        {
            Text = "NanoPic",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
        });
        content.Children.Add(new TextBlock
        {
            Text = $"版本 {version}",
            Margin = new Thickness(0, 6, 0, 14),
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
        });
        content.Children.Add(new TextBlock
        {
            Text = "轻量便携的图片压缩与格式转换工具",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
        });
        content.Children.Add(new TextBlock
        {
            Text = "• 批量压缩与转换，支持拖入图片和文件夹\n"
                + "• 按质量或目标大小压缩，支持保留原格式\n"
                + "• 支持 JPEG、PNG、WebP、GIF、BMP、TIFF、ICO\n"
                + "• 可调整尺寸、亮度、水印与输出文件名\n"
                + "• 单文件便携运行，图片全程在本地处理",
            Margin = new Thickness(0, 10, 0, 14),
            FontSize = 12,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
        });
        var feedback = new TextBlock
        {
            FontSize = 12,
            Text = "问题反馈：",
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
        };
        feedback.Inlines.Add(new LineBreak());
        feedback.Inlines.Add(link);
        content.Children.Add(feedback);
        var okButton = new System.Windows.Controls.Button { Content = "确定", Width = 88, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var about = new Window
        {
            Title = "关于 NanoPic",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
            Content = content
        };
        okButton.Click += (_, _) => about.Close();
        content.Children.Add(okButton);
        return about;
    }

    private void OutputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (OutputDirectoryBox is null || OverwriteOutput is null ||
            FilenamePresetBox is null || FilenameTemplateBox is null || OutputIndexBox is null || ConflictPolicyBox is null)
        {
            return;
        }

        // 覆盖原文件模式下输出固定为“原文件名写回源目录”，以下设置不会生效，禁用并说明原因。
        var overwrite = OverwriteOutput.IsChecked.GetValueOrDefault();
        OutputDirectoryBox.IsEnabled = !overwrite;
        FilenamePresetBox.IsEnabled = !overwrite;
        FilenameTemplateBox.IsEnabled = !overwrite;
        OutputIndexBox.IsEnabled = !overwrite;
        ConflictPolicyBox.IsEnabled = !overwrite;

        var overwriteHint = "覆盖原文件模式下不生效：输出固定使用原文件名写入源文件所在目录。";
        FilenamePresetBox.ToolTip = overwrite ? overwriteHint : "选择输出文件的命名方式";
        FilenameTemplateBox.ToolTip = overwrite
            ? overwriteHint
            : "可用占位符：{name} 原文件名、{index} 序号、{ext} 格式、{date} 日期、{time} 时间；日期时间取文件的修改时间";
        OutputIndexBox.ToolTip = overwrite ? overwriteHint : null;
        ConflictPolicyBox.ToolTip = overwrite ? overwriteHint : "重复压缩同一文件时如何处理已有输出";
        RefreshUi();
    }

    private void TopMostChanged(object sender, RoutedEventArgs e) => Topmost = TopMostCheck.IsChecked == true;

    private void MaxThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxThreadsValueText is null) return;
        MaxThreadsValueText.Text = ((int)e.NewValue).ToString();
        RefreshSettingsUi();
    }

    private void CompressTab_Checked(object sender, RoutedEventArgs e)
    {
        if (CompressTabPanel is null || ImageTabPanel is null) return;
        CompressTabPanel.Visibility = Visibility.Visible;
        ImageTabPanel.Visibility = Visibility.Collapsed;
    }

    private void ImageTab_Checked(object sender, RoutedEventArgs e)
    {
        if (CompressTabPanel is null || ImageTabPanel is null) return;
        CompressTabPanel.Visibility = Visibility.Collapsed;
        ImageTabPanel.Visibility = Visibility.Visible;
    }

    private void SettingsChanged(object sender, RoutedEventArgs e) => RefreshSettingsUi();
    private void SettingsChanged(object sender, TextChangedEventArgs e) => RefreshSettingsUi();

    private static readonly string[] FilenamePresetTemplates = { "", "{name}_{index}", "{index}", "{date}_{time}" };

    private void FilenamePreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FilenamePresetBox is null || FilenameTemplateBox is null || CustomTemplateRow is null) return;
        var preset = FilenamePresetBox.SelectedIndex;
        var isCustom = preset < 0 || preset >= FilenamePresetTemplates.Length;
        CustomTemplateRow.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (!isCustom)
        {
            FilenameTemplateBox.Text = FilenamePresetTemplates[preset];
        }

        RefreshSettingsUi();
    }

    private static int ResolveFilenamePresetIndex(string? template)
    {
        var normalized = template?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(normalized))
        {
            return 0;
        }

        if (normalized.Equals("{name}", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        for (var i = 1; i < FilenamePresetTemplates.Length; i++)
        {
            if (normalized.Equals(FilenamePresetTemplates[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return FilenamePresetTemplates.Length;
    }

    private void ChooseOutputDirectory() { var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择输出目录" }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) OutputDirectoryBox.Text = dialog.SelectedPath; }

    private static readonly string[] WatermarkPaletteColors =
    {
        "#000000", "#1F2937", "#374151", "#6B7280", "#9CA3AF", "#D1D5DB", "#E5E7EB", "#F9FAFB",
        "#DC2626", "#EF4444", "#F97316", "#F59E0B", "#FACC15", "#65A30D", "#22C55E", "#10B981",
        "#06B6D4", "#0EA5E9", "#3B82F6", "#6366F1", "#8B5CF6", "#A855F7", "#D946EF", "#EC4899"
    };

    private void WatermarkColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (WatermarkColorPopup is null || WatermarkColorPalette is null)
        {
            return;
        }

        UpdateWatermarkColorSwatch();
        if (WatermarkColorPalette.Children.Count == 0)
        {
            foreach (var hex in WatermarkPaletteColors)
            {
                var swatch = new System.Windows.Controls.Button
                {
                    Width = 26,
                    Height = 22,
                    MinHeight = 20,
                    Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BorderStrongBrush"),
                    Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                    ToolTip = hex,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var color = hex;
                swatch.Click += (_, _) => SelectWatermarkColor(color);
                WatermarkColorPalette.Children.Add(swatch);
            }
        }

        WatermarkColorPopup.IsOpen = !WatermarkColorPopup.IsOpen;
    }

    private void WatermarkColorMore_Click(object sender, RoutedEventArgs e)
    {
        if (WatermarkColorPopup is not null)
        {
            WatermarkColorPopup.IsOpen = false;
        }

        var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (IsRgbHex(WatermarkColorBox.Text))
        {
            try
            {
                dialog.Color = System.Drawing.ColorTranslator.FromHtml(WatermarkColorBox.Text);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
            }
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SelectWatermarkColor("#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2"));
        }
    }

    private void SelectWatermarkColor(string hex)
    {
        WatermarkColorBox.Text = hex;
        WatermarkColorPopup.IsOpen = false;
        UpdateWatermarkColorSwatch();
    }

    private void UpdateWatermarkColorSwatch()
    {
        if (WatermarkColorSwatch is null || WatermarkColorBox is null)
        {
            return;
        }

        var colorText = IsRgbHex(WatermarkColorBox.Text) ? WatermarkColorBox.Text : "#000000";
        WatermarkColorSwatch.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorText));
    }

    private async Task InitializeShellIntegrationAsync()
    {
        if (_shellHost is null)
        {
            ApplyShellIntegrationUnavailable("当前环境不支持右键菜单集成。");
            return;
        }

        try
        {
            if (!_shellHost.AllowStartupReconcile)
            {
                // Explorer 激活出来的进程只读取状态，不执行注册表恢复。
                RefreshShellIntegrationUi(await Task.Run(() => _shellHost.Registry.Detect()));
                return;
            }

            var reconcile = await Task.Run(() => _shellHost.Registry.ReconcileOnStartup());
            _shellHost.SyncComRegistration();
            var transientHint = reconcile.Action switch
            {
                ShellIntegrationReconcileAction.PathAutoUpdated => "已自动更新右键菜单路径。",
                ShellIntegrationReconcileAction.InterruptedOperationCompleted => "已完成上次未结束的右键菜单操作。",
                _ => null
            };
            RefreshShellIntegrationUi(reconcile.State, transientHint);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            await WriteLogAsync("WARN", $"读取右键菜单注册状态失败：{exception.Message}", exception);
            ApplyShellIntegrationUnavailable("无法读取右键菜单注册状态。");
        }
    }

    private void ApplyShellIntegrationUnavailable(string hint)
    {
        if (ShellIntegrationCheck is null)
        {
            return;
        }

        _shellIntegrationUiUpdating = true;
        ShellIntegrationCheck.IsChecked = false;
        ShellIntegrationCheck.IsEnabled = false;
        ShellIntegrationHint.Text = hint;
        ShellIntegrationActions.Visibility = Visibility.Collapsed;
        _shellIntegrationUiUpdating = false;
    }

    /// <summary>正常状态只显示紧凑开关与一行说明；异常时才展开修复/移除/诊断操作。</summary>
    private void RefreshShellIntegrationUi(ShellIntegrationState state, string? transientHint = null)
    {
        if (ShellIntegrationCheck is null)
        {
            return;
        }

        _shellIntegrationState = state;
        _shellIntegrationUiUpdating = true;
        try
        {
            ShellIntegrationCheck.IsEnabled = state.Status != ShellIntegrationStatus.Conflict;
            ShellIntegrationCheck.IsChecked = state.Status switch
            {
                ShellIntegrationStatus.InstalledCurrent => true,
                ShellIntegrationStatus.NotInstalled => false,
                _ => null
            };

            ShellIntegrationHint.Text = transientHint ?? ShellIntegrationPresentation.BuildHint(
                state,
                ShellIntegrationPresentation.IsWindows11OrLater);

            var showAdopt = state.Status == ShellIntegrationStatus.InstalledStale;
            var showRepair = state.Status is ShellIntegrationStatus.Partial or ShellIntegrationStatus.RecoveryPending;
            var showConflict = state.Status == ShellIntegrationStatus.Conflict;
            ShellIntegrationAdoptButton.Visibility = showAdopt ? Visibility.Visible : Visibility.Collapsed;
            ShellIntegrationRepairButton.Visibility = showRepair ? Visibility.Visible : Visibility.Collapsed;
            ShellIntegrationRemoveButton.Visibility = showAdopt || showRepair ? Visibility.Visible : Visibility.Collapsed;
            ShellIntegrationDiagnosticsButton.Visibility = showConflict ? Visibility.Visible : Visibility.Collapsed;
            ShellIntegrationActions.Visibility = showAdopt || showRepair || showConflict
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _shellIntegrationUiUpdating = false;
        }
    }

    private async void ShellIntegrationCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_shellIntegrationUiUpdating || _shellHost is null)
        {
            return;
        }

        // 异常状态不通过三态循环隐式决定“修复还是删除”，必须点击明确操作。
        if (_shellIntegrationState is { AllowsDirectToggle: false })
        {
            RefreshShellIntegrationUi(_shellIntegrationState);
            return;
        }

        var install = _shellIntegrationState?.Status != ShellIntegrationStatus.InstalledCurrent;
        await RunShellIntegrationOperationAsync(
            install ? "正在启用右键菜单…" : "正在移除右键菜单…",
            service => install ? service.Install() : service.Remove());
    }

    private async void ShellIntegrationRepair_Click(object sender, RoutedEventArgs e) =>
        await RunShellIntegrationOperationAsync("正在修复右键菜单…", service => service.Repair());

    private async void ShellIntegrationAdopt_Click(object sender, RoutedEventArgs e) =>
        await RunShellIntegrationOperationAsync("正在切换到当前版本…", service => service.Repair());

    private async void ShellIntegrationRemove_Click(object sender, RoutedEventArgs e) =>
        await RunShellIntegrationOperationAsync("正在移除右键菜单…", service => service.Remove());

    private void ShellIntegrationDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var report = _shellIntegrationState?.BuildDiagnosticReport() ?? "没有可用的诊断信息。";
        try
        {
            System.Windows.Clipboard.SetText(report);
            ProgressText.Text = "右键菜单诊断信息已复制到剪贴板。";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(this, report, "右键菜单诊断信息", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task RunShellIntegrationOperationAsync(
        string busyText,
        Func<ShellContextMenuIntegrationService, ShellIntegrationOperationResult> operation)
    {
        if (_shellHost is null)
        {
            return;
        }

        var previousHint = ShellIntegrationHint.Text;
        ShellIntegrationCheck.IsEnabled = false;
        ShellIntegrationActions.IsEnabled = false;
        ShellIntegrationHint.Text = "处理中…";
        ProgressText.Text = busyText;
        try
        {
            var result = await Task.Run(() => operation(_shellHost.Registry));
            _shellHost.SyncComRegistration();
            // 不依赖写入方法的返回值：重新读取注册表真实状态。
            var state = await Task.Run(() => _shellHost.Registry.Detect());
            RefreshShellIntegrationUi(state);
            if (!result.Succeeded && result.UserMessage is not null)
            {
                System.Windows.MessageBox.Show(this, result.UserMessage, "右键菜单集成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ProgressText.Text = result.Succeeded
                ? state.Status == ShellIntegrationStatus.InstalledCurrent
                    ? ShellIntegrationPresentation.IsWindows11OrLater
                        ? "右键菜单已启用。" + ShellIntegrationPresentation.Windows11EntryNote
                        : "右键菜单已启用。"
                    : "右键菜单已移除。"
                : "右键菜单操作未完成。";
            await WriteLogAsync(
                result.Succeeded ? "INFO" : "WARN",
                $"右键菜单集成操作完成：状态 {state.Status}，扩展 {state.RegisteredExtensionCount}/{state.ExpectedExtensionCount}。{result.UserMessage}",
                null);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            await WriteLogAsync("ERROR", $"右键菜单集成操作失败：{exception.Message}", exception);
            ShellIntegrationHint.Text = previousHint;
            System.Windows.MessageBox.Show(this, "右键菜单操作失败，详细信息已写入日志。", "右键菜单集成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ShellIntegrationActions.IsEnabled = true;
            if (_shellIntegrationState is null || _shellIntegrationState.Status != ShellIntegrationStatus.Conflict)
            {
                ShellIntegrationCheck.IsEnabled = true;
            }
        }
    }

    private void RefreshUi()
    {
        if (EmptyState is null || SummaryText is null)
        {
            return;
        }

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = $"已选 {_items.Count(item => item.IsSelected)} / 共 {_items.Count}";
    }

    private void RefreshSettingsUi()
    {
        if (QualityMode is null || TargetSizeMode is null || QualityBox is null || TargetSizeBox is null || AllowExceedCheck is null || WidthBox is null || HeightBox is null || PreserveAspectCheck is null || BrightnessBox is null || WatermarkBox is null || WatermarkColorBox is null || WatermarkColorSwatch is null || WatermarkColorPopup is null || WatermarkPositionBox is null || WatermarkOpacityBox is null || WatermarkFontSizeBox is null || WatermarkMarginBox is null)
        {
            return;
        }

        var useTargetSize = TargetSizeMode.IsChecked == true;
        var formatIndex = FormatBox?.SelectedIndex ?? 0;

        if (useTargetSize)
        {
            QualityBox.IsEnabled = false;
            QualityBox.ToolTip = "按目标大小模式下由系统自动确定最佳压缩策略。";
            TargetSizeBox.IsEnabled = true;
            TargetSizeUnitBox.IsEnabled = true;
            AllowExceedCheck.IsEnabled = true;
            if (AllowResizeForTargetCheck is not null)
            {
                AllowResizeForTargetCheck.IsEnabled = true;
            }
        }
        else
        {
            TargetSizeBox.IsEnabled = false;
            TargetSizeUnitBox.IsEnabled = false;
            AllowExceedCheck.IsEnabled = false;
            if (AllowResizeForTargetCheck is not null)
            {
                AllowResizeForTargetCheck.IsEnabled = false;
            }

            // 按输出格式定制 QualityBox 状态与提示
            if (formatIndex is 0 or 2) // JPEG, WebP
            {
                QualityBox.IsEnabled = true;
                QualityBox.ToolTip = "质量越大图片越清晰，文件体积越大（推荐 75–85）。";
            }
            else if (formatIndex == 1) // PNG
            {
                QualityBox.IsEnabled = true;
                QualityBox.ToolTip = "PNG 质量用于颜色优化；100 为无损，数值越低通常体积越小，图片宽高不变。";
            }
            else if (formatIndex is 3 or 4 or 5 or 6) // GIF, BMP, TIFF, ICO
            {
                QualityBox.IsEnabled = false;
                QualityBox.ToolTip = "所选输出格式不支持质量调节。";
            }
            else // 原格式 (7)
            {
                QualityBox.IsEnabled = true;
                QualityBox.ToolTip = "质量设置仅对 JPEG/WebP/PNG 生效。";
            }
        }

        WidthBox.IsEnabled = ResizeCheck.IsChecked == true;
        HeightBox.IsEnabled = ResizeCheck.IsChecked == true;
        PreserveAspectCheck.IsEnabled = ResizeCheck.IsChecked == true;
        BrightnessBox.IsEnabled = BrightnessCheck.IsChecked == true;
        WatermarkBox.IsEnabled = WatermarkCheck.IsChecked == true;
        WatermarkColorBox.IsEnabled = WatermarkCheck.IsChecked == true;
        WatermarkColorSwatch.IsEnabled = WatermarkCheck.IsChecked == true;
        WatermarkPositionBox.IsEnabled = WatermarkCheck.IsChecked == true;
        WatermarkOpacityBox.IsEnabled = WatermarkCheck.IsChecked == true;
        WatermarkFontSizeBox.IsEnabled = WatermarkCheck.IsChecked == true;
        WatermarkMarginBox.IsEnabled = WatermarkCheck.IsChecked == true;
        MetadataNoteBox.IsEnabled = MetadataNoteCheck.IsChecked == true;
        if (!WatermarkCheck.IsChecked.GetValueOrDefault() && WatermarkColorPopup.IsOpen)
        {
            WatermarkColorPopup.IsOpen = false;
        }

        UpdateWatermarkColorSwatch();
    }

    private void ApplySettings(NanoPicSettings settings)
    {
        var form = SettingsFormMapper.ToForm(settings);
        FormatBox.SelectedIndex = form.OutputFormatIndex;
        QualityBox.Text = form.Quality;
        QualityMode.IsChecked = !form.UseTargetSize;
        TargetSizeMode.IsChecked = form.UseTargetSize;
        TargetSizeBox.Text = form.TargetSizeKilobytes;
        TargetSizeUnitBox.SelectedIndex = form.TargetSizeInMegabytes ? 1 : 0;
        AllowExceedCheck.IsChecked = form.AllowExceedTarget;
        if (AllowResizeForTargetCheck is not null)
        {
            AllowResizeForTargetCheck.IsChecked = form.AllowResizeForTarget;
        }
        ResizeCheck.IsChecked = form.ResizeEnabled;
        WidthBox.Text = form.Width;
        HeightBox.Text = form.Height;
        PreserveAspectCheck.IsChecked = form.PreserveAspectRatio;
        AutoOrientCheck.IsChecked = form.AutoOrient;
        StripMetadataCheck.IsChecked = form.StripMetadata;
        MetadataNoteCheck.IsChecked = form.MetadataNoteEnabled;
        MetadataNoteBox.Text = form.MetadataNoteText;
        BrightnessCheck.IsChecked = form.BrightnessEnabled;
        BrightnessBox.Text = form.BrightnessPercent;
        BackgroundColorBox.Text = form.BackgroundColorHex;
        WatermarkCheck.IsChecked = form.WatermarkEnabled;
        WatermarkBox.Text = form.WatermarkText;
        WatermarkColorBox.Text = form.WatermarkColorHex;
        WatermarkPositionBox.SelectedIndex = form.WatermarkPositionIndex;
        WatermarkOpacityBox.Text = form.WatermarkOpacityPercent;
        WatermarkFontSizeBox.Text = form.WatermarkFontSize;
        WatermarkMarginBox.Text = form.WatermarkMargin;
        FilenameTemplateBox.Text = form.OutputFilenameTemplate;
        FilenamePresetBox.SelectedIndex = ResolveFilenamePresetIndex(form.OutputFilenameTemplate);
        OutputIndexBox.Text = form.OutputIndex;
        ConflictPolicyBox.SelectedIndex = form.ConflictPolicyIndex;
        MaxThreadsSlider.Value = int.TryParse(form.MaxThreads, out var persistedThreads) && persistedThreads >= 1 && persistedThreads <= 64
            ? persistedThreads
            : 2;
        OutputDirectoryBox.Text = form.OutputDirectory;
        GpuCheck.IsChecked = form.UseGpu;
        TopMostCheck.IsChecked = form.TopMost;
        if (AutoDownscaleCheck is not null)
        {
            AutoDownscaleCheck.IsChecked = form.AutoDownscaleOnExceed;
        }
        if (SoftMaxPixelsBox is not null)
        {
            var mpValue = int.TryParse(form.SoftMaxPixels, out var mp) ? mp : 200;
            var foundIndex = -1;
            for (var i = 0; i < SoftMaxPixelsBox.Items.Count; i++)
            {
                if (SoftMaxPixelsBox.Items[i] is ComboBoxItem item &&
                    item.Tag is string tag &&
                    int.TryParse(tag, out var tagValue) &&
                    tagValue == mpValue)
                {
                    foundIndex = i;
                    break;
                }
            }
            if (foundIndex >= 0)
            {
                SoftMaxPixelsBox.SelectedIndex = foundIndex;
            }
            else
            {
                var customItem = new ComboBoxItem
                {
                    Content = $"{mpValue} MP (自定义)",
                    Tag = mpValue.ToString()
                };
                SoftMaxPixelsBox.Items.Add(customItem);
                SoftMaxPixelsBox.SelectedItem = customItem;
            }
        }
        Topmost = form.TopMost;
        RefreshSettingsUi();
    }

    private NanoPicSettings CaptureSettings() => SettingsFormMapper.Capture(_settings, new SettingsFormState(
        FormatBox.SelectedIndex,
        QualityBox.Text,
        TargetSizeMode.IsChecked == true,
        TargetSizeBox.Text,
        TargetSizeUnitBox.SelectedIndex == 1,
        AllowExceedCheck.IsChecked == true,
        ResizeCheck.IsChecked == true,
        WidthBox.Text,
        HeightBox.Text,
        PreserveAspectCheck.IsChecked == true,
        AutoOrientCheck.IsChecked == true,
        StripMetadataCheck.IsChecked == true,
        BrightnessCheck.IsChecked == true,
        BrightnessBox.Text,
        BackgroundColorBox.Text,
        WatermarkCheck.IsChecked == true,
        WatermarkBox.Text,
        WatermarkColorBox.Text,
        WatermarkOpacityBox.Text,
        WatermarkFontSizeBox.Text,
        WatermarkMarginBox.Text,
        FilenameTemplateBox.Text,
        OutputIndexBox.Text,
        ConflictPolicyBox.SelectedIndex,
        ((int)MaxThreadsSlider.Value).ToString(),
        GpuCheck.IsChecked == true,
        TopMostCheck.IsChecked == true,
        OutputDirectoryBox.Text,
        WatermarkPositionBox.SelectedIndex,
        MetadataNoteCheck.IsChecked == true,
        MetadataNoteBox.Text,
        AutoDownscaleCheck.IsChecked == true,
        SoftMaxPixelsBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string tag ? tag : "200",
        AllowResizeForTargetCheck?.IsChecked == true));
}

public sealed class QueueItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _status = "等待";
    private string _detail = string.Empty;
    private ImageFailureKind? _failureKind;
    public QueueItem(string path, long bytes) { Path = path; Bytes = bytes; }
    public string Path { get; }
    public long Bytes { get; private set; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string SizeText => $"{Bytes / 1024d:N1} KB";
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public string Detail { get => _detail; set { _detail = value; OnPropertyChanged(); } }
    public bool HasFailure => _failureKind is not null && _failureKind != ImageFailureKind.TaskCanceled;
    public bool IsProcessing => string.Equals(_status, "处理中", StringComparison.Ordinal);
    public bool IsPending => string.Equals(_status, "等待", StringComparison.Ordinal);

    public void UpdateBytes(long bytes)
    {
        if (bytes <= 0 || Bytes == bytes) return;
        Bytes = bytes;
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(SizeText));
    }

    public void MarkPending() { _failureKind = null; Status = "等待"; Detail = string.Empty; OnPropertyChanged(nameof(HasFailure)); }
    public void MarkProcessing() { _failureKind = null; Status = "处理中"; Detail = string.Empty; OnPropertyChanged(nameof(HasFailure)); }
    public void MarkSuccess(
        bool exceededTarget,
        string? outputPath,
        bool skipped,
        string? frameNotice = null,
        string? metadataFallbackNotice = null)
    {
        _failureKind = null;
        Status = skipped ? "已跳过"
            : exceededTarget && metadataFallbackNotice is not null ? "完成（已超限，已清理元数据）"
            : exceededTarget ? "完成（已超限）"
            : metadataFallbackNotice is not null ? "完成（已清理元数据）"
            : frameNotice is null ? "完成"
            : "完成（保留首帧）";
        Detail = outputPath ?? string.Empty;
        OnPropertyChanged(nameof(HasFailure));
    }
    public void MarkFailure(ImageFailureKind kind, string message) { _failureKind = kind; Status = kind == ImageFailureKind.TaskCanceled ? "已取消" : "失败"; Detail = message; OnPropertyChanged(nameof(HasFailure)); }
    public void MarkCanceled() => MarkFailure(ImageFailureKind.TaskCanceled, "已取消");
    public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class ImageOutputFormatExtensions { public static ImageFormat ToImageFormat(this ImageOutputFormat format) => format switch { ImageOutputFormat.Jpeg => ImageFormat.Jpeg, ImageOutputFormat.Png => ImageFormat.Png, ImageOutputFormat.Webp => ImageFormat.Webp, ImageOutputFormat.Gif => ImageFormat.Gif, ImageOutputFormat.Bmp => ImageFormat.Bmp, ImageOutputFormat.Tiff => ImageFormat.Tiff, ImageOutputFormat.Ico => ImageFormat.Ico, _ => ImageFormat.Unknown }; }
