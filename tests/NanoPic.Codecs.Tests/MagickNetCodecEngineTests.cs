using NanoPic.Codecs;
using NanoPic.Core;
using Xunit;

namespace NanoPic.Codecs.Tests;

public sealed class MagickNetCodecEngineTests
{
    [Fact]
    public void Selected_engine_is_magick_net_q8_x64()
    {
        Assert.Equal("Magick.NET-Q8-x64", MagickNetCodecEngine.PackageId);
        Assert.Equal("Magick.NET-Q8-x64", MagickNetCodecEngine.RuntimeAssemblyName);
    }

}

public sealed class MagickNetImageCodecContractTests : ImageCodecContractTests
{
    protected override IImageCodec CreateCodec() => new MagickNetImageCodec();
}

public sealed class WicImageCodecContractTests : ImageCodecContractTests
{
    protected override IImageCodec CreateCodec() => new WicImageCodec();
}
