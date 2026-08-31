using NanoPic.App;
using Xunit;

namespace NanoPic.App.Tests;

public sealed class QueueItemMetadataFallbackTests
{
    [Theory]
    [InlineData(false, "完成（已清理元数据）")]
    [InlineData(true, "完成（已超限，已清理元数据）")]
    public void Success_status_keeps_metadata_cleanup_visible_when_target_is_exceeded(
        bool exceededTarget,
        string expectedStatus)
    {
        var item = new QueueItem("C:\\input\\photo.jpg", 1024);

        item.MarkSuccess(
            exceededTarget,
            "C:\\output\\photo.jpg",
            skipped: false,
            metadataFallbackNotice: "已清理部分不兼容的图片元数据。");

        Assert.Equal(expectedStatus, item.Status);
    }
}
