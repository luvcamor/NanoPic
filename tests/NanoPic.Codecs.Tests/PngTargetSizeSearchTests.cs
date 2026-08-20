using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NanoPic.Codecs;
using NanoPic.Core;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class PngTargetSizeSearchTests
{
    [Fact]
    public async Task Search_Evaluates_MinQuality_When_Only_Q1_Reaches_Target()
    {
        var evaluated = new List<int>();
        var result = await SearchAsync(
            new TargetSizeOptions(
                TargetBytes: 600,
                AllowExceed: false,
                MinQuality: 1,
                MaxQuality: 100,
                AllowResizeForTarget: false),
            quality =>
            {
                evaluated.Add(quality);
                return quality == 1 ? 500L : 1_000L;
            });

        Assert.True(result.IsSuccess, result.Failure?.UserMessage);
        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value!.Selected.Quality);
        Assert.Contains(1, evaluated);
    }

    [Fact]
    public async Task Search_Evaluates_Exact_MaxQuality_Boundary()
    {
        var evaluated = new List<int>();
        var result = await SearchAsync(
            new TargetSizeOptions(
                TargetBytes: 600,
                AllowExceed: false,
                MinQuality: 1,
                MaxQuality: 99,
                AllowResizeForTarget: false),
            quality =>
            {
                evaluated.Add(quality);
                return quality == 99 ? 500L : 1_000L;
            });

        Assert.True(result.IsSuccess, result.Failure?.UserMessage);
        Assert.NotNull(result.Value);
        Assert.Equal(99, result.Value!.Selected.Quality);
        Assert.Contains(99, evaluated);
    }

    [Fact]
    public async Task Search_Encodes_Each_Size_And_Quality_At_Most_Once()
    {
        var counts = new Dictionary<int, int>();
        var result = await SearchAsync(
            new TargetSizeOptions(
                TargetBytes: 600,
                AllowExceed: false,
                MinQuality: 1,
                MaxQuality: 100,
                AllowResizeForTarget: false),
            quality =>
            {
                counts.TryGetValue(quality, out var count);
                counts[quality] = count + 1;
                return quality <= 80 ? 500L : 1_000L;
            });

        Assert.True(result.IsSuccess, result.Failure?.UserMessage);
        Assert.NotEmpty(counts);
        Assert.All(counts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task Search_Refines_To_Highest_Quality_That_Reaches_Target()
    {
        var result = await SearchAsync(
            new TargetSizeOptions(
                TargetBytes: 600,
                AllowExceed: false,
                MinQuality: 1,
                MaxQuality: 100,
                AllowResizeForTarget: false),
            quality => quality <= 82 ? 500L : 1_000L);

        Assert.True(result.IsSuccess, result.Failure?.UserMessage);
        Assert.NotNull(result.Value);
        Assert.Equal(82, result.Value!.Selected.Quality);
    }

    [Theory]
    [InlineData(0, 1, 100)]
    [InlineData(100, 0, 100)]
    [InlineData(100, 1, 101)]
    [InlineData(100, 80, 40)]
    public async Task Search_Rejects_Invalid_Target_Options(long targetBytes, int minQuality, int maxQuality)
    {
        var result = await SearchAsync(
            new TargetSizeOptions(
                TargetBytes: targetBytes,
                AllowExceed: false,
                MinQuality: minQuality,
                MaxQuality: maxQuality,
                AllowResizeForTarget: false),
            _ => 500L);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImageFailureKind.InvalidConfiguration, result.Failure?.Kind);
    }

    private static Task<ImageOperationResult<PngTargetSearchResult>> SearchAsync(
        TargetSizeOptions options,
        Func<int, long> sizeForQuality)
    {
        var frames = new[] { CreateFrame() };
        return PngTargetSizeSearch.SearchAsync(
            frames,
            "unused.png",
            options,
            (_, _, quality, _) => Task.FromResult(sizeForQuality(quality)),
            (currentFrames, _) => currentFrames,
            CancellationToken.None);
    }

    private static BitmapFrame CreateFrame()
    {
        var pixels = new byte[] { 0, 0, 0, 255 };
        var source = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            4);
        source.Freeze();
        var frame = BitmapFrame.Create(source);
        frame.Freeze();
        return frame;
    }
}
