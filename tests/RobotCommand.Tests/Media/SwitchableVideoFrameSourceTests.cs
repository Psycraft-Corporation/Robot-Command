using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class SwitchableVideoFrameSourceTests
{
    [Fact]
    public void SetSource_RelaysOnlyCurrentSource()
    {
        var first = new VideoFrameBuffer();
        var second = new VideoFrameBuffer();
        var switchable = new SwitchableVideoFrameSource();
        var events = 0;
        switchable.FrameAvailable += (_, _) => events++;

        switchable.SetSource(first);
        first.Publish(new byte[16], 2, 2, 8, DateTimeOffset.UtcNow);
        switchable.SetSource(second);
        first.Publish(new byte[16], 2, 2, 8, DateTimeOffset.UtcNow);
        second.Publish(Enumerable.Repeat((byte)42, 16).ToArray(), 2, 2, 8, DateTimeOffset.UtcNow);

        var bytes = new byte[16];
        Assert.True(switchable.TryCopyLatest(bytes, out var info));
        Assert.NotNull(info);
        Assert.All(bytes, value => Assert.Equal(42, value));
        Assert.Equal(4, events);
    }
}
