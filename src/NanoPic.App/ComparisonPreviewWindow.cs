using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NanoPic.Codecs;
using NanoPic.Core;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace NanoPic.App;

public sealed class ComparisonPreviewWindow : Window
{
    private readonly string _sourcePath;
    private readonly string? _outputPath;
    private readonly ImageSafetyLimits _limits;
    private readonly Image _before = new() { Stretch = Stretch.Fill, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _after = new() { Stretch = Stretch.Fill, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _notice = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Grid _images = new();
    private readonly CancellationTokenSource _closed = new();
    private CancellationTokenSource? _loading;
    private readonly List<Task> _loads = new();
    private bool _isClosed;
    private ImagePreview? _left, _right;
    private bool _detail;
    private double _centerX = 0.5, _centerY = 0.5;
    private bool _syncScroll;
    internal bool HasComparison => _outputPath is not null;

    public ComparisonPreviewWindow(string sourcePath, string? outputPath, ImageSafetyLimits limits)
    {
        _sourcePath = sourcePath; _limits = limits;
        _outputPath = !string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath) &&
            !SameFile(sourcePath, outputPath!) ? outputPath : null;
        Title = Path.GetFileName(sourcePath) + (HasComparison ? " · 前后对比" : " · 图片预览");
        Width = HasComparison ? 1000 : 760; Height = 680; MinWidth = 640; MinHeight = 420;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/" + typeof(MainWindow).Assembly.GetName().Name + ";component/Themes/NanoPicTheme.xaml", UriKind.Relative)
        });
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        _notice.FontSize = 12;
        _notice.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        var layout = new DockPanel { Margin = new Thickness(16), Background = Background };
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var whole = new Button { Content = "看全图", Padding = new Thickness(12, 6, 12, 6) };
        var detail = new Button { Content = "看原尺寸细节", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        whole.Click += async (_, _) => await LoadAsync(false);
        detail.Click += async (_, _) => await LoadAsync(true);
        buttons.Children.Add(whole); buttons.Children.Add(detail);
        DockPanel.SetDock(buttons, Dock.Left); toolbar.Children.Add(buttons); toolbar.Children.Add(_notice);
        async void PickDetail(object sender, MouseButtonEventArgs e)
        {
            if (_detail || sender is not Image image || image.ActualWidth <= 0 || image.ActualHeight <= 0) return;
            var point = e.GetPosition(image);
            _centerX = Math.Max(0, Math.Min(1, point.X / image.ActualWidth));
            _centerY = Math.Max(0, Math.Min(1, point.Y / image.ActualHeight));
            await LoadAsync(true);
        }
        _before.MouseLeftButtonDown += PickDetail; _after.MouseLeftButtonDown += PickDetail;
        DockPanel.SetDock(toolbar, Dock.Top); layout.Children.Add(toolbar);
        _images.ColumnDefinitions.Add(new ColumnDefinition());
        if (HasComparison) _images.ColumnDefinitions.Add(new ColumnDefinition());
        var leftScroll = AddPane(0, HasComparison ? "原图" : "当前图片", _before);
        if (HasComparison)
        {
            var rightScroll = AddPane(1, "压缩后", _after);
            void Synchronize(ScrollViewer source, ScrollViewer target)
            {
                if (_syncScroll || !_detail) return;
                _syncScroll = true;
                target.ScrollToHorizontalOffset(source.HorizontalOffset); target.ScrollToVerticalOffset(source.VerticalOffset);
                _syncScroll = false;
            }
            leftScroll.ScrollChanged += (_, _) => Synchronize(leftScroll, rightScroll);
            rightScroll.ScrollChanged += (_, _) => Synchronize(rightScroll, leftScroll);
        }
        layout.Children.Add(_images); Content = layout;
        _images.SizeChanged += (_, _) => UpdateScale();
        Loaded += async (_, _) => await LoadAsync(false);
        Closed += (_, _) => { _isClosed = true; _closed.Cancel(); _loading?.Cancel(); _closed.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static bool SameFile(string left, string right)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(PortablePath.ForDisplay(left)), Path.GetFullPath(PortablePath.ForDisplay(right)), StringComparison.OrdinalIgnoreCase)) return true;
            using var a = new FileStream(PortablePath.ForFileSystem(left), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var b = new FileStream(PortablePath.ForFileSystem(right), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(a.SafeFileHandle, out var x) || !GetFileInformationByHandle(b.SafeFileHandle, out var y)) return true;
            return x.Volume == y.Volume && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity identity);

    private ScrollViewer AddPane(int column, string caption, Image image)
    {
        var panel = new DockPanel { Margin = new Thickness(6) };
        var label = new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 242, 245)), Content = image };
        panel.Children.Add(scroll); Grid.SetColumn(panel, column); _images.Children.Add(panel); return scroll;
    }

    public async Task CloseAndWaitAsync()
    {
        if (!_isClosed) Close();
        await Task.WhenAll(_loads.ToArray());
    }

    private Task LoadAsync(bool detail)
    {
        if (_isClosed) return Task.CompletedTask;
        _loads.RemoveAll(task => task.IsCompleted);
        var task = LoadCoreAsync(detail);
        _loads.Add(task);
        return task;
    }

    private async Task LoadCoreAsync(bool detail)
    {
        _loading?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _loading = cancellation;
        _notice.Text = "正在读取…";
        try
        {
            var left = await ImagePreviewReader.ReadAsync(_sourcePath, detail, _limits, cancellation.Token, _centerX, _centerY);
            if (cancellation.IsCancellationRequested) return;
            if (!left.IsSuccess || left.Value is null) { _notice.Text = left.Failure?.UserMessage; return; }
            ImagePreview? right = null;
            if (_outputPath is not null)
            {
                var result = await ImagePreviewReader.ReadAsync(_outputPath, detail, _limits, cancellation.Token, _centerX, _centerY);
                if (cancellation.IsCancellationRequested) return;
                if (!result.IsSuccess || result.Value is null) { _notice.Text = "结果文件无法读取，请重新打开预览。"; return; }
                right = result.Value;
            }
            _detail = detail; _left = left.Value; _right = right;
            _before.Source = _left.Image; _after.Source = _right?.Image;
            _notice.Text = detail ? "所选局部 · 同倍率" : "同倍率全图 · 点击图片看细节";
            _before.ToolTip = $"原图 {_left.OriginalWidth} × {_left.OriginalHeight}";
            if (_right is not null) _after.ToolTip = $"结果 {_right.OriginalWidth} × {_right.OriginalHeight}";
            UpdateScale();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_loading, cancellation)) _loading = null; cancellation.Dispose(); }
    }

    private void UpdateScale()
    {
        if (_left is null) return;
        var maxWidth = Math.Max(_left.OriginalWidth, _right?.OriginalWidth ?? 0);
        var maxHeight = Math.Max(_left.OriginalHeight, _right?.OriginalHeight ?? 0);
        var scale = _detail ? 1.0 / VisualTreeHelper.GetDpi(this).DpiScaleX : Math.Min(1,
            Math.Min(Math.Max(1, _images.ActualWidth / (HasComparison ? 2 : 1) - 36) / maxWidth,
                Math.Max(1, _images.ActualHeight - 50) / maxHeight));
        _before.Width = (_detail ? _left.Image.PixelWidth : _left.OriginalWidth) * scale;
        _before.Height = (_detail ? _left.Image.PixelHeight : _left.OriginalHeight) * scale;
        if (_right is not null)
        {
            _after.Width = (_detail ? _right.Image.PixelWidth : _right.OriginalWidth) * scale;
            _after.Height = (_detail ? _right.Image.PixelHeight : _right.OriginalHeight) * scale;
        }
    }
}
