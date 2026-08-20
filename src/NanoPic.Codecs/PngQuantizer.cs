using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoPic.Codecs;

public sealed record PngQuantizationInfo(
    bool WasLossy,
    int PaletteSize,
    bool UsedDithering,
    int UniqueColorCount);

public static class PngQuantizer
{
    private struct ColorBucket
    {
        public int Count;
        public long SumB;
        public long SumG;
        public long SumR;
        public long SumA;

        public byte AvgB => (byte)(Count > 0 ? SumB / Count : 0);
        public byte AvgG => (byte)(Count > 0 ? SumG / Count : 0);
        public byte AvgR => (byte)(Count > 0 ? SumR / Count : 0);
        public byte AvgA => (byte)(Count > 0 ? SumA / Count : 0);
    }

    private sealed class ColorBox
    {
        public int Start;
        public int End; // exclusive
        public int TotalCount;
        public byte MinR, MaxR;
        public byte MinG, MaxG;
        public byte MinB, MaxB;
        public byte MinA, MaxA;

        public int Volume => Math.Max(1, (MaxR - MinR + 1) * (MaxG - MinG + 1) * (MaxB - MinB + 1) * (MaxA - MinA + 1));
        public int VarianceScore
        {
            get
            {
                var dr = MaxR - MinR;
                var dg = MaxG - MinG;
                var db = MaxB - MinB;
                var da = MaxA - MinA;
                return (dr * dr * 3 + dg * dg * 4 + db * db * 2 + da * da * 4) * Math.Min(1000, TotalCount);
            }
        }
    }

    public static BitmapSource Quantize(
        BitmapSource source,
        int quality,
        CancellationToken cancellationToken,
        out PngQuantizationInfo info)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            throw new ArgumentException("图像尺寸必须大于0。", nameof(source));
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var dpiX = source.DpiX > 0 ? source.DpiX : 96.0;
        var dpiY = source.DpiY > 0 ? source.DpiY : 96.0;

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = checked(width * 4);
        var totalBytes = checked(stride * height);
        var pixelBuffer = new byte[totalBytes];
        converted.CopyPixels(new Int32Rect(0, 0, width, height), pixelBuffer, stride, 0);

        cancellationToken.ThrowIfCancellationRequested();

        // 阶段 1：精确颜色统计与快速路径
        var exactColors = new Dictionary<int, int>(capacity: Math.Min(1024, width * height));
        var hasTransparentPixel = false;

        for (var offset = 0; offset < totalBytes; offset += 4)
        {
            var b = pixelBuffer[offset];
            var g = pixelBuffer[offset + 1];
            var r = pixelBuffer[offset + 2];
            var a = pixelBuffer[offset + 3];

            if (a == 0)
            {
                hasTransparentPixel = true;
                b = 0;
                g = 0;
                r = 0;
            }
            else if (a < 255)
            {
                hasTransparentPixel = true;
            }

            var packed = (a << 24) | (r << 16) | (g << 8) | b;
            if (exactColors.TryGetValue(packed, out var count))
            {
                exactColors[packed] = count + 1;
            }
            else
            {
                exactColors[packed] = 1;
            }

            // 如果已经确认不是少色图且 quality 明显需要量化，可以在超过 2048 种颜色时提前停止精确统计
            if (exactColors.Count > 2048 && quality < 100)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 若全图唯一颜色 <= 256：无损 Palette 快速路径
        if (exactColors.Count <= 256)
        {
            var paletteColors = BuildDeterministicExactPalette(exactColors.Keys, hasTransparentPixel);
            var palette = new BitmapPalette(paletteColors);
            var colorToIndex = new Dictionary<int, byte>(paletteColors.Count);
            for (var i = 0; i < paletteColors.Count; i++)
            {
                var c = paletteColors[i];
                var packed = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
                colorToIndex[packed] = (byte)i;
            }

            var indexedPixels = new byte[width * height];
            var pixelIdx = 0;
            for (var offset = 0; offset < totalBytes; offset += 4)
            {
                var b = pixelBuffer[offset];
                var g = pixelBuffer[offset + 1];
                var r = pixelBuffer[offset + 2];
                var a = pixelBuffer[offset + 3];
                if (a == 0)
                {
                    b = 0;
                    g = 0;
                    r = 0;
                }

                var packed = (a << 24) | (r << 16) | (g << 8) | b;
                if (!colorToIndex.TryGetValue(packed, out var idx))
                {
                    idx = 0;
                }

                indexedPixels[pixelIdx++] = idx;
            }

            var indexedSource = BitmapSource.Create(
                width,
                height,
                dpiX,
                dpiY,
                PixelFormats.Indexed8,
                palette,
                indexedPixels,
                width);
            indexedSource.Freeze();

            info = new PngQuantizationInfo(
                WasLossy: false,
                PaletteSize: paletteColors.Count,
                UsedDithering: false,
                UniqueColorCount: paletteColors.Count);
            return indexedSource;
        }

        // Quality 100 且颜色数 > 256：保持完全无损（不进行有损量化）
        if (quality >= 100)
        {
            info = new PngQuantizationInfo(
                WasLossy: false,
                PaletteSize: 0,
                UsedDithering: false,
                UniqueColorCount: exactColors.Count);
            return converted;
        }

        // 阶段 2：有损调色板量化
        var policy = PngCompressionPolicy.GetParameters(quality);
        var targetColors = Math.Min(policy.MaxColors, 256);

        var paletteList = BuildQuantizedPalette(
            pixelBuffer,
            totalBytes,
            targetColors,
            hasTransparentPixel,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var lossyPalette = new BitmapPalette(paletteList);
        var outputIndexedPixels = MapPixelsToIndexed(
            pixelBuffer,
            width,
            height,
            paletteList,
            policy,
            cancellationToken);

        var result = BitmapSource.Create(
            width,
            height,
            dpiX,
            dpiY,
            PixelFormats.Indexed8,
            lossyPalette,
            outputIndexedPixels,
            width);
        result.Freeze();

        info = new PngQuantizationInfo(
            WasLossy: true,
            PaletteSize: paletteList.Count,
            UsedDithering: policy.EnableDithering,
            UniqueColorCount: exactColors.Count);
        return result;
    }

    private static List<Color> BuildDeterministicExactPalette(IEnumerable<int> packedColors, bool hasAlpha)
    {
        var list = packedColors
            .Select(packed => Color.FromArgb(
                (byte)((packed >> 24) & 0xFF),
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)(packed & 0xFF)))
            .OrderBy(c => c.A)
            .ThenBy(c => c.R)
            .ThenBy(c => c.G)
            .ThenBy(c => c.B)
            .ToList();

        if (hasAlpha && list.Count > 0 && list[0].A > 0)
        {
            // 如果存在半透明但没有纯透明，且列表不满 256，可以保留稳定顺序
        }

        return list;
    }

    private static List<Color> BuildQuantizedPalette(
        byte[] pixelBuffer,
        int totalBytes,
        int maxColors,
        bool hasTransparentPixel,
        CancellationToken cancellationToken)
    {
        // 5-5-5-4 降采样桶 (A:4-bit, R:5-bit, G:5-bit, B:5-bit = 19-bit index -> 524288 slots)
        // 为内存效率，使用 4-4-4-4 (16-bit, 65536 slots) 建立初始空间直方图
        const int HistSize = 65536;
        var buckets = new ColorBucket[HistSize];

        for (var offset = 0; offset < totalBytes; offset += 4)
        {
            var b = pixelBuffer[offset];
            var g = pixelBuffer[offset + 1];
            var r = pixelBuffer[offset + 2];
            var a = pixelBuffer[offset + 3];

            if (a < 16)
            {
                // 完全透明像素统一汇入 0 号槽
                a = 0;
                r = 0;
                g = 0;
                b = 0;
            }

            var index = ((a >> 4) << 12) | ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
            ref var bucket = ref buckets[index];
            bucket.Count++;
            bucket.SumB += b;
            bucket.SumG += g;
            bucket.SumR += r;
            bucket.SumA += a;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 收集非空桶
        var activeBuckets = new List<ColorBucket>(4096);
        for (var i = 0; i < HistSize; i++)
        {
            if (buckets[i].Count > 0)
            {
                activeBuckets.Add(buckets[i]);
            }
        }

        if (activeBuckets.Count <= maxColors)
        {
            return BuildPaletteFromBuckets(activeBuckets, hasTransparentPixel);
        }

        // Variance-Weighted Box Partition (Median-Cut 改进版)
        var bucketArray = activeBuckets.ToArray();
        var boxes = new List<ColorBox>
        {
            CreateBox(bucketArray, 0, bucketArray.Length)
        };

        var targetBoxCount = Math.Max(16, maxColors);
        while (boxes.Count < targetBoxCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 寻找方差得分最大的 Box 分割
            var bestBoxIndex = -1;
            var maxScore = -1;
            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.End - box.Start > 1 && box.VarianceScore > maxScore)
                {
                    maxScore = box.VarianceScore;
                    bestBoxIndex = i;
                }
            }

            if (bestBoxIndex < 0)
            {
                break;
            }

            var toSplit = boxes[bestBoxIndex];
            var splitDim = ChooseSplitDimension(toSplit);
            SortBuckets(bucketArray, toSplit.Start, toSplit.End, splitDim);

            // 按元素数量或加权权重中位数分割
            var mid = toSplit.Start + (toSplit.End - toSplit.Start) / 2;
            var box1 = CreateBox(bucketArray, toSplit.Start, mid);
            var box2 = CreateBox(bucketArray, mid, toSplit.End);

            boxes[bestBoxIndex] = box1;
            boxes.Add(box2);
        }

        var resultPalette = new List<Color>(boxes.Count);
        if (hasTransparentPixel)
        {
            resultPalette.Add(Color.FromArgb(0, 0, 0, 0));
        }

        foreach (var box in boxes)
        {
            long totalCount = 0;
            long sb = 0, sg = 0, sr = 0, sa = 0;
            for (var i = box.Start; i < box.End; i++)
            {
                var b = bucketArray[i];
                totalCount += b.Count;
                sb += b.SumB;
                sg += b.SumG;
                sr += b.SumR;
                sa += b.SumA;
            }

            if (totalCount <= 0) continue;

            var avgA = (byte)(sa / totalCount);
            var avgR = (byte)(sr / totalCount);
            var avgG = (byte)(sg / totalCount);
            var avgB = (byte)(sb / totalCount);

            if (avgA < 16 && hasTransparentPixel)
            {
                continue; // 已经有透明基色
            }

            var color = Color.FromArgb(avgA, avgR, avgG, avgB);
            if (!resultPalette.Contains(color))
            {
                resultPalette.Add(color);
            }
        }

        if (resultPalette.Count == 0)
        {
            resultPalette.Add(Color.FromArgb(255, 0, 0, 0));
        }

        // 确定性排序
        return resultPalette
            .OrderBy(c => c.A)
            .ThenBy(c => c.R)
            .ThenBy(c => c.G)
            .ThenBy(c => c.B)
            .Take(256)
            .ToList();
    }

    private static List<Color> BuildPaletteFromBuckets(List<ColorBucket> buckets, bool hasTransparentPixel)
    {
        var result = new List<Color>(buckets.Count + 1);
        if (hasTransparentPixel)
        {
            result.Add(Color.FromArgb(0, 0, 0, 0));
        }

        foreach (var b in buckets)
        {
            var c = Color.FromArgb(b.AvgA, b.AvgR, b.AvgG, b.AvgB);
            if (!result.Contains(c))
            {
                result.Add(c);
            }
        }

        return result
            .OrderBy(c => c.A)
            .ThenBy(c => c.R)
            .ThenBy(c => c.G)
            .ThenBy(c => c.B)
            .Take(256)
            .ToList();
    }

    private static ColorBox CreateBox(ColorBucket[] buckets, int start, int end)
    {
        var box = new ColorBox
        {
            Start = start,
            End = end,
            MinR = 255, MaxR = 0,
            MinG = 255, MaxG = 0,
            MinB = 255, MaxB = 0,
            MinA = 255, MaxA = 0
        };

        var count = 0;
        for (var i = start; i < end; i++)
        {
            var b = buckets[i];
            count += b.Count;
            var r = b.AvgR;
            var g = b.AvgG;
            var bl = b.AvgB;
            var a = b.AvgA;

            if (r < box.MinR) box.MinR = r;
            if (r > box.MaxR) box.MaxR = r;
            if (g < box.MinG) box.MinG = g;
            if (g > box.MaxG) box.MaxG = g;
            if (bl < box.MinB) box.MinB = bl;
            if (bl > box.MaxB) box.MaxB = bl;
            if (a < box.MinA) box.MinA = a;
            if (a > box.MaxA) box.MaxA = a;
        }

        box.TotalCount = count;
        return box;
    }

    private static int ChooseSplitDimension(ColorBox box)
    {
        var dr = (box.MaxR - box.MinR) * 3;
        var dg = (box.MaxG - box.MinG) * 4;
        var db = (box.MaxB - box.MinB) * 2;
        var da = (box.MaxA - box.MinA) * 4;

        if (da >= dr && da >= dg && da >= db && (box.MaxA - box.MinA) > 10) return 3; // Alpha
        if (dg >= dr && dg >= db) return 1; // Green
        if (dr >= db) return 0; // Red
        return 2; // Blue
    }

    private static void SortBuckets(ColorBucket[] buckets, int start, int end, int dimension)
    {
        Array.Sort(buckets, start, end - start, Comparer<ColorBucket>.Create((x, y) =>
        {
            return dimension switch
            {
                0 => x.AvgR.CompareTo(y.AvgR),
                1 => x.AvgG.CompareTo(y.AvgG),
                2 => x.AvgB.CompareTo(y.AvgB),
                3 => x.AvgA.CompareTo(y.AvgA),
                _ => 0
            };
        }));
    }

    private static byte[] MapPixelsToIndexed(
        byte[] pixelBuffer,
        int width,
        int height,
        List<Color> palette,
        PngQuantizationParameters policy,
        CancellationToken cancellationToken)
    {
        var paletteCount = palette.Count;
        var palR = new byte[paletteCount];
        var palG = new byte[paletteCount];
        var palB = new byte[paletteCount];
        var palA = new byte[paletteCount];

        for (var i = 0; i < paletteCount; i++)
        {
            palR[i] = palette[i].R;
            palG[i] = palette[i].G;
            palB[i] = palette[i].B;
            palA[i] = palette[i].A;
        }

        var output = new byte[width * height];
        // 快速查找缓存：针对 4-4-4-4 (65536) 颜色索引
        var lookupCache = new int[65536];
        for (var i = 0; i < 65536; i++) lookupCache[i] = -1;

        if (!policy.EnableDithering)
        {
            // 无抖动：直接查表与最近邻匹配
            var outIdx = 0;
            for (var offset = 0; offset < pixelBuffer.Length; offset += 4)
            {
                if ((outIdx & 0x7FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var b = pixelBuffer[offset];
                var g = pixelBuffer[offset + 1];
                var r = pixelBuffer[offset + 2];
                var a = pixelBuffer[offset + 3];

                if (a < 16)
                {
                    output[outIdx++] = 0;
                    continue;
                }

                var cacheKey = ((a >> 4) << 12) | ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                var cached = lookupCache[cacheKey];
                if (cached >= 0)
                {
                    output[outIdx++] = (byte)cached;
                }
                else
                {
                    var bestIdx = FindClosestPaletteIndex(r, g, b, a, palR, palG, palB, palA, paletteCount);
                    lookupCache[cacheKey] = bestIdx;
                    output[outIdx++] = (byte)bestIdx;
                }
            }

            return output;
        }

        // Floyd-Steinberg 误差扩散抖动
        // 误差缓冲区两行：当前行与下一行，每像素 4 个通道 (B, G, R, A)
        var errorStride = (width + 2) * 4;
        var errorCurr = new int[errorStride];
        var errorNext = new int[errorStride];
        var ditherStrength = policy.DitherStrength;

        var pixelOffset = 0;
        var outputIndex = 0;

        for (var y = 0; y < height; y++)
        {
            if ((y & 0x1F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Array.Clear(errorNext, 0, errorStride);

            for (var x = 0; x < width; x++)
            {
                var errOffset = (x + 1) * 4;

                var origB = pixelBuffer[pixelOffset];
                var origG = pixelBuffer[pixelOffset + 1];
                var origR = pixelBuffer[pixelOffset + 2];
                var origA = pixelBuffer[pixelOffset + 3];
                pixelOffset += 4;

                if (origA < 16)
                {
                    output[outputIndex++] = 0;
                    continue;
                }

                var curB = ClampToByte(origB + (int)Math.Round(errorCurr[errOffset] * ditherStrength / 16.0));
                var curG = ClampToByte(origG + (int)Math.Round(errorCurr[errOffset + 1] * ditherStrength / 16.0));
                var curR = ClampToByte(origR + (int)Math.Round(errorCurr[errOffset + 2] * ditherStrength / 16.0));
                var curA = ClampToByte(origA + (int)Math.Round(errorCurr[errOffset + 3] * ditherStrength / 16.0));

                var bestIdx = FindClosestPaletteIndex(curR, curG, curB, curA, palR, palG, palB, palA, paletteCount);
                output[outputIndex++] = (byte)bestIdx;

                var matchedR = palR[bestIdx];
                var matchedG = palG[bestIdx];
                var matchedB = palB[bestIdx];
                var matchedA = palA[bestIdx];

                var errR = curR - matchedR;
                var errG = curG - matchedG;
                var errB = curB - matchedB;
                var errA = curA - matchedA;

                // Floyd-Steinberg: (x+1, y) += 7, (x-1, y+1) += 3, (x, y+1) += 5, (x+1, y+1) += 1
                DistributeError(errorCurr, errOffset + 4, errB, errG, errR, errA, 7);
                DistributeError(errorNext, errOffset - 4, errB, errG, errR, errA, 3);
                DistributeError(errorNext, errOffset, errB, errG, errR, errA, 5);
                DistributeError(errorNext, errOffset + 4, errB, errG, errR, errA, 1);
            }

            // 交换误差行
            var temp = errorCurr;
            errorCurr = errorNext;
            errorNext = temp;
        }

        return output;
    }

    private static void DistributeError(int[] buffer, int offset, int eb, int eg, int er, int ea, int factor)
    {
        buffer[offset] += eb * factor;
        buffer[offset + 1] += eg * factor;
        buffer[offset + 2] += er * factor;
        buffer[offset + 3] += ea * factor;
    }

    private static byte ClampToByte(int value) => (byte)Math.Max(0, Math.Min(255, value));

    private static int FindClosestPaletteIndex(
        byte r, byte g, byte b, byte a,
        byte[] palR, byte[] palG, byte[] palB, byte[] palA,
        int paletteCount)
    {
        var bestIdx = 0;
        var minDistance = int.MaxValue;

        for (var i = 0; i < paletteCount; i++)
        {
            var pa = palA[i];
            if (a == 0 && pa == 0)
            {
                return i;
            }

            var da = a - pa;
            var dr = r - palR[i];
            var dg = g - palG[i];
            var db = b - palB[i];

            var dist = da * da * 4 + dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (dist < minDistance)
            {
                minDistance = dist;
                bestIdx = i;
                if (dist == 0) break;
            }
        }

        return bestIdx;
    }
}
