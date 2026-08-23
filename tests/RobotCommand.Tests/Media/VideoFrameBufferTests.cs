using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class VideoFrameBufferTests
{
    [Fact]
    public void Publish_ReusesLatestFrameAndCopiesItToCallerBuffer()
    {
        var buffer = new VideoFrameBuffer();
        var events = 0;
        buffer.FrameAvailable += (_, _) => events++;
        var first = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        buffer.Publish(first, width: 4, height: 2, stride: 16, DateTimeOffset.UnixEpoch);

        var destination = new byte[32];
        Assert.True(buffer.TryCopyLatest(destination, out var info));
        Assert.NotNull(info);
        Assert.Equal(4, info.Width);
        Assert.Equal(2, info.Height);
        Assert.Equal(16, info.Stride);
        Assert.Equal(1, info.Sequence);
        Assert.Equal(first, destination);
        Assert.Equal(1, events);

        var second = Enumerable.Repeat((byte)77, 32).ToArray();
        buffer.Publish(second, width: 4, height: 2, stride: 16, DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.True(buffer.TryCopyLatest(destination, out info));
        Assert.Equal(2, info!.Sequence);
        Assert.Equal(second, destination);
        Assert.Equal(2, events);
    }

    [Fact]
    public void Clear_RemovesLatestFrameAndPublishesChange()
    {
        var buffer = new VideoFrameBuffer();
        var events = 0;
        buffer.FrameAvailable += (_, _) => events++;
        buffer.Publish(new byte[16], width: 2, height: 2, stride: 8, DateTimeOffset.UtcNow);

        buffer.Clear();

        Assert.Null(buffer.LatestInfo);
        Assert.False(buffer.TryCopyLatest(new byte[16], out var info));
        Assert.Null(info);
        Assert.Equal(2, events);
    }

    [Fact]
    public void Publish_RejectsFramesSmallerThanDeclaredCaps()
    {
        var buffer = new VideoFrameBuffer();

        Assert.Throws<ArgumentException>(() =>
            buffer.Publish(new byte[15], width: 2, height: 2, stride: 8, DateTimeOffset.UtcNow));
    }
}
