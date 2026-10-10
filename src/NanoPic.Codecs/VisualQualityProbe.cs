using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NanoPic.Core;

namespace NanoPic.Codecs;

internal sealed record SampledQuality(double StructuralSimilarity, double EdgeColorError, double ColorError, bool Accepted);

// Bounded, deterministic screening, not a whole-image/perceptual-lossless guarantee.
// A reduced overview plus native-resolution tiles avoids averaging thin text away.
internal sealed class VisualQualityProbe
{
    private sealed record Patch(Int32Rect Region, int Width, int Height, byte[] Pixels, bool Overview);
    private readonly Patch[] _patches;
    private readonly int _width, _height;
    private VisualQualityProbe(int width, int height, Patch[] patches)
    { _width = width; _height = height; _patches = patches; }

    internal int SampledPixels => _patches.Sum(p => p.Width * p.Height);

    public static VisualQualityProbe Capture(BitmapSource source, CancellationToken token, bool applyColorProfile = true)
    {
        token.ThrowIfCancellationRequested();
        if (applyColorProfile) source = DisplayColors(source);
        var patches = new List<Patch>();
        var overview = Overview(source);
        patches.Add(new Patch(Int32Rect.Empty, overview.PixelWidth, overview.PixelHeight,
            Read(overview, new Int32Rect(0, 0, overview.PixelWidth, overview.PixelHeight)), true));
        var width = Math.Min(96, source.PixelWidth);
        var height = Math.Min(96, source.PixelHeight);
        var positions = new HashSet<(int, int)>();
        for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
        {
            token.ThrowIfCancellationRequested();
            var x = (source.PixelWidth - width) * column / 2;
            var y = (source.PixelHeight - height) * row / 2;
            if (!positions.Add((x, y))) continue;
            var region = new Int32Rect(x, y, width, height);
            patches.Add(new Patch(region, width, height, Read(source, region), false));
        }
        return new VisualQualityProbe(source.PixelWidth, source.PixelHeight, patches.ToArray());
    }

    public ImageContentKind Classify()
    {
        // Text/screenshots: large flat regions alongside sharp, sparse strokes.
        // Inspect native tiles too: resized overviews can erase small writing.
        foreach (var patch in _patches)
        {
            var pixels = patch.Pixels;
            var flat = 0; var edges = 0; var pairs = 0; var bright = 0; var dark = 0;
            var histogram = new int[4096];
            for (var y = 0; y < patch.Height; y++) for (var x = 0; x < patch.Width; x++)
            {
                var offset = (y * patch.Width + x) * 4;
                var b = Composite(pixels[offset], pixels[offset + 3]);
                var g = Composite(pixels[offset + 1], pixels[offset + 3]);
                var r = Composite(pixels[offset + 2], pixels[offset + 3]);
                histogram[((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4)]++;
                var luma = Luma(r, g, b);
                if (luma > 225) bright++;
                if (luma < 180) dark++;
                if (x == 0 && y == 0) continue;
                var delta = Edge(pixels, offset, x, y, patch.Width);
                if (delta <= 3) flat++;
                if (delta >= 35) edges++;
                pairs++;
            }
            var total = patch.Width * patch.Height;
            Array.Sort(histogram);
            var dominant = histogram.Skip(histogram.Length - 8).Sum() / (double)total;
            var flatRatio = flat / (double)Math.Max(1, pairs);
            var edgeRatio = edges / (double)Math.Max(1, pairs);
            var paper = bright > total * 0.55 && dark > total * 0.005;
            if ((paper && flatRatio > 0.42 && edgeRatio > 0.008) ||
                (dominant > 0.82 && flatRatio > 0.60 && edgeRatio > 0.008))
                return ImageContentKind.TextOrScreenshot;
        }
        return ImageContentKind.Photo;
    }

    public SampledQuality Compare(BitmapSource candidate, ImageContentKind content, CancellationToken token)
    {
        if (candidate.PixelWidth != _width || candidate.PixelHeight != _height)
            return new SampledQuality(0, 255, 255, false);
        candidate = DisplayColors(candidate);
        var minSimilarity = 1.0;
        var maxColorError = 0.0;
        var maxEdgeError = 0.0;
        foreach (var reference in _patches)
        {
            token.ThrowIfCancellationRequested();
            var sampled = reference.Overview ? Overview(candidate) : candidate;
            var region = reference.Overview ? new Int32Rect(0, 0, sampled.PixelWidth, sampled.PixelHeight) : reference.Region;
            var pixels = Read(sampled, region);
            // Checking each tile separately prevents large blank areas from hiding
            // local damage. Alpha and both light/dark backgrounds are represented.
            var quality = Measure(reference.Pixels, pixels, reference.Width, reference.Height, token);
            minSimilarity = Math.Min(minSimilarity, quality.StructuralSimilarity);
            maxColorError = Math.Max(maxColorError, quality.ColorError);
            maxEdgeError = Math.Max(maxEdgeError, quality.EdgeColorError);
        }
        var text = content == ImageContentKind.TextOrScreenshot;
        var accepted = minSimilarity >= (text ? 0.975 : 0.955) &&
            maxColorError <= (text ? 5.0 : 7.0) && maxEdgeError <= (text ? 11.0 : 17.0);
        return new SampledQuality(minSimilarity, maxEdgeError, maxColorError, accepted);
    }

    private static SampledQuality Measure(byte[] a, byte[] b, int width, int height, CancellationToken token)
    {
        double colorError = 0, edgeError = 0, similarity = 0;
        var edgeCount = 0; var blocks = 0;
        for (var y0 = 0; y0 < height; y0 += 8)
        {
            token.ThrowIfCancellationRequested();
            for (var x0 = 0; x0 < width; x0 += 8)
            {
                double sumA = 0, sumB = 0, squaresA = 0, squaresB = 0, products = 0;
                var count = 0;
                for (var y = y0; y < Math.Min(y0 + 8, height); y++)
                for (var x = x0; x < Math.Min(x0 + 8, width); x++)
                {
                    var i = (y * width + x) * 4;
                    var aa = a[i + 3]; var ab = b[i + 3];
                    var rA = Composite(a[i + 2], aa); var gA = Composite(a[i + 1], aa); var bA = Composite(a[i], aa);
                    var rB = Composite(b[i + 2], ab); var gB = Composite(b[i + 1], ab); var bB = Composite(b[i], ab);
                    var lA = Luma(rA, gA, bA); var lB = Luma(rB, gB, bB);
                    sumA += lA; sumB += lB; squaresA += lA * lA; squaresB += lB * lB; products += lA * lB; count++;
                    double error = Math.Max(Math.Abs(rA - rB), Math.Max(Math.Abs(gA - gB), Math.Abs(bA - bB)));
                    // Also compare on black, without treating RGB beneath alpha=0 as visible.
                    for (var c = 0; c < 3; c++) error = Math.Max(error, Math.Abs((a[i + c] * aa - b[i + c] * ab) / 255.0));
                    colorError += error;
                    if (Edge(a, i, x, y, width) >= 20) { edgeError += error; edgeCount++; }
                }
                var meanA = sumA / count; var meanB = sumB / count;
                var varianceA = Math.Max(0, squaresA / count - meanA * meanA);
                var varianceB = Math.Max(0, squaresB / count - meanB * meanB);
                var covariance = products / count - meanA * meanB;
                // Local uniform-window SSIM, constants (0.01*255)^2 and (0.03*255)^2.
                similarity += ((2 * meanA * meanB + 6.5025) * (2 * covariance + 58.5225)) /
                    ((meanA * meanA + meanB * meanB + 6.5025) * (varianceA + varianceB + 58.5225));
                blocks++;
            }
        }
        return new SampledQuality(similarity / blocks, edgeCount == 0 ? 0 : edgeError / edgeCount,
            colorError / (width * height), true);
    }

    private static int Edge(byte[] pixels, int offset, int x, int y, int width)
    {
        var maximum = 0;
        for (var c = 0; c < 3; c++)
        {
            var value = Composite(pixels[offset + c], pixels[offset + 3]);
            if (x > 0) maximum = Math.Max(maximum, Math.Abs(value - Composite(pixels[offset - 4 + c], pixels[offset - 1])));
            if (y > 0) maximum = Math.Max(maximum, Math.Abs(value - Composite(pixels[offset - width * 4 + c], pixels[offset - width * 4 + 3])));
        }
        return maximum;
    }

    private static double Luma(int r, int g, int b) => (77 * r + 150 * g + 29 * b) / 256.0;
    private static int Composite(int channel, int alpha) => (channel * alpha + 255 * (255 - alpha) + 127) / 255;
    private static BitmapSource DisplayColors(BitmapSource source)
    {
        if (source is BitmapFrame { ColorContexts: { Count: > 0 } } frame)
            return new ColorConvertedBitmap(frame, frame.ColorContexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
        return source;
    }

    private static BitmapSource Overview(BitmapSource source)
    {
        var scale = Math.Min(1, 256.0 / Math.Max(source.PixelWidth, source.PixelHeight));
        return scale == 1 ? source : new TransformedBitmap(source, new ScaleTransform(scale, scale));
    }
    private static byte[] Read(BitmapSource source, Int32Rect region)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(region.Width * region.Height * 4)];
        converted.CopyPixels(region, pixels, region.Width * 4, 0);
        return pixels;
    }
}
