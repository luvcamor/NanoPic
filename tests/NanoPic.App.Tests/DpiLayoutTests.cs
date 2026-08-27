using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace NanoPic.App.Tests;

public sealed class DpiLayoutTests
{
    [Theory]
    [InlineData(144, 1230, 810)]
    [InlineData(192, 1640, 1080)]
    public void MinimumWindow_RendersWithoutCriticalControlClipping(int dpi, int expectedPixelWidth, int expectedPixelHeight)
    {
        RunOnSta(() => VerifyMinimumWindowAtDpi(dpi, expectedPixelWidth, expectedPixelHeight));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void AboutWindow_FeaturesAndFeedbackRenderWithoutClipping(int dpi)
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var factory = typeof(MainWindow).GetMethod("CreateAboutWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(factory);
            var about = Assert.IsType<Window>(factory.Invoke(window, null));
            var content = Assert.IsAssignableFrom<FrameworkElement>(about.Content);
            about.Content = null;
            var surface = new Border { Background = about.Background, Child = content };
            var logicalWidth = about.Width - 2 * SystemParameters.ResizeFrameVerticalBorderWidth;
            surface.Measure(new Size(logicalWidth, double.PositiveInfinity));
            var logicalHeight = surface.DesiredSize.Height;
            surface.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
            surface.UpdateLayout();

            var textBlocks = FindVisualDescendants<TextBlock>(surface).ToArray();
            foreach (var textBlock in textBlocks)
            {
                AssertCriticalElementWithin(surface, textBlock, logicalWidth, logicalHeight);
            }
            var link = Assert.Single(textBlocks.SelectMany(block => block.Inlines.OfType<Hyperlink>()));
            Assert.Equal("https://github.com/luvcamor/NanoPic/issues", link.NavigateUri.AbsoluteUri);
            Assert.True(link.Focusable);
            Assert.True(link.IsEnabled);
            AssertCriticalElementWithin(surface, FindVisualDescendants<Button>(surface).Single(), logicalWidth, logicalHeight);

            var evidenceDirectory = Environment.GetEnvironmentVariable("NANOPIC_DPI_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidenceDirectory))
            {
                Directory.CreateDirectory(evidenceDirectory);
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(logicalWidth * dpi / 96d),
                    (int)Math.Ceiling(logicalHeight * dpi / 96d),
                    dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(evidenceDirectory, $"about-features-dpi-{dpi * 100 / 96}.png"));
                encoder.Save(stream);
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static void VerifyMinimumWindowAtDpi(int dpi, int expectedPixelWidth, int expectedPixelHeight)
    {
        const double logicalWidth = 820;
        const double logicalHeight = 540;
        var window = new MainWindow();
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var settingsPanel = FindVisualDescendants<Border>(root)
            .Single(element => AutomationProperties.GetName(element) == "压缩与图像处理设置");

        root.Measure(new Size(logicalWidth, logicalHeight));
        root.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
        root.UpdateLayout();

        AssertCriticalElementWithin(root, FindNamedElement(root, "QueueGrid"), logicalWidth, logicalHeight);
        AssertCriticalElementWithin(root, FindNamedElement(root, "OutputDirectoryBox"), logicalWidth, logicalHeight);
        AssertCriticalElementWithin(root, FindNamedElement(root, "StartButton"), logicalWidth, logicalHeight);
        AssertCriticalElementWithin(root, settingsPanel, logicalWidth, logicalHeight);
        var settingsScroll = FindVisualDescendants<ScrollViewer>(settingsPanel).First();
        AssertCriticalElementWithin(root, settingsScroll, logicalWidth, logicalHeight);
        Assert.True(settingsScroll.ScrollableHeight > 0, "Settings panel must remain vertically scrollable at the minimum window size.");

        var bitmap = new RenderTargetBitmap(expectedPixelWidth, expectedPixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        Assert.Equal(expectedPixelWidth, bitmap.PixelWidth);
        Assert.Equal(expectedPixelHeight, bitmap.PixelHeight);

        var evidenceDirectory = Environment.GetEnvironmentVariable("NANOPIC_DPI_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Directory.CreateDirectory(evidenceDirectory);
            var path = Path.Combine(evidenceDirectory, $"new-ui-stage4-dpi-{dpi * 100 / 96}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
        }
    }

    private static FrameworkElement FindNamedElement(DependencyObject root, string name) =>
        FindVisualDescendants<FrameworkElement>(root).Single(element => element.Name == name);

    private static void AssertCriticalElementWithin(
        FrameworkElement root,
        FrameworkElement element,
        double logicalWidth,
        double logicalHeight)
    {
        Assert.True(element.ActualWidth > 0, $"{element.Name} has no rendered width.");
        Assert.True(element.ActualHeight > 0, $"{element.Name} has no rendered height.");
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -0.5, $"{element.Name} extends past the left edge: {bounds}.");
        Assert.True(bounds.Top >= -0.5, $"{element.Name} extends past the top edge: {bounds}.");
        Assert.True(bounds.Right <= logicalWidth + 0.5, $"{element.Name} extends past the right edge: {bounds}.");
        Assert.True(bounds.Bottom <= logicalHeight + 0.5, $"{element.Name} extends past the bottom edge: {bounds}.");
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var descendant in FindVisualDescendants<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return descendant;
            }
        }
    }
}
