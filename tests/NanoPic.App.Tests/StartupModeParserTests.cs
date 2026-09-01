using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.App.Tests;

public sealed class StartupModeParserTests
{
    [Fact]
    public void S1_NoArgumentsStartsNormalWindow()
    {
        var decision = StartupModeParser.Parse(new string[0]);
        Assert.Equal(NanoPicStartupMode.Normal, decision.Mode);
    }

    [Fact]
    public void S2_SmokeTestRequiresExactlyTwoPaths()
    {
        var valid = StartupModeParser.Parse(new[] { "--smoke-test", @"C:\in.png", @"C:\out.png" });
        Assert.Equal(NanoPicStartupMode.SmokeTest, valid.Mode);
        Assert.Equal(@"C:\in.png", valid.SmokeTestInput);
        Assert.Equal(@"C:\out.png", valid.SmokeTestOutput);

        foreach (var arguments in new[]
                 {
                     new[] { "--smoke-test" },
                     new[] { "--smoke-test", @"C:\in.png" },
                     new[] { "--smoke-test", @"C:\in.png", @"C:\out.png", "extra" }
                 })
        {
            var invalid = StartupModeParser.Parse(arguments);
            Assert.Equal(NanoPicStartupMode.Invalid, invalid.Mode);
            Assert.Equal(StartupModeParser.UsageExitCode, invalid.ExitCode);
        }
    }

    [Theory]
    [InlineData("-Embedding")]
    [InlineData("-embedding")]
    [InlineData("/Embedding")]
    public void S3_EmbeddingSwitchSelectsComMode(string argument)
    {
        var decision = StartupModeParser.Parse(new[] { argument });
        Assert.Equal(NanoPicStartupMode.ComEmbedding, decision.Mode);
    }

    [Fact]
    public void S4_UnknownSwitchNeverOpensTheMainWindow()
    {
        foreach (var argument in new[] { "--shell-add", "-x", "--register", "/Q" })
        {
            var decision = StartupModeParser.Parse(new[] { argument });
            Assert.Equal(NanoPicStartupMode.Invalid, decision.Mode);
            Assert.Equal(StartupModeParser.UsageExitCode, decision.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(decision.Diagnostic));
        }
    }

    [Fact]
    public void S5_UnknownSwitchCombinedWithEmbeddingIsRejected()
    {
        var decision = StartupModeParser.Parse(new[] { "-Embedding", "--shell-add" });
        Assert.Equal(NanoPicStartupMode.Invalid, decision.Mode);
    }

    [Fact]
    public void S6_PlainPathArgumentsKeepLegacyNormalBehaviour()
    {
        var decision = StartupModeParser.Parse(new[] { @"C:\photos\a.png", @"D:\b.jpg" });
        Assert.Equal(NanoPicStartupMode.Normal, decision.Mode);
    }
}
