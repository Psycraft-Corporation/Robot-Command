using RobotCommand.Models;
using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class NativeVideoProtocolResolverTests
{
    [Theory]
    [InlineData("Rtsp", "rtsp://host/path", NativeVideoProtocol.Rtsp)]
    [InlineData("Custom", "srt://host:8890?streamid=read:path", NativeVideoProtocol.Srt)]
    [InlineData("Hls", "https://host/path/index.m3u8", NativeVideoProtocol.Hls)]
    [InlineData("Webrtc", "https://host/path/whep", NativeVideoProtocol.Whep)]
    public void Resolve_UsesProtocolAndEndpoint(string protocol, string endpoint, NativeVideoProtocol expected)
        => Assert.Equal(expected, NativeVideoProtocolResolver.Resolve(Stream(protocol, endpoint)));

    private static CameraStreamRecord Stream(string protocol, string endpoint)
        => new("id", "stream", "camera", "connection", null, protocol, "Active", endpoint,
            "", "h264", 640, 360, 30, 1000, null, null, "", "", DateTimeOffset.UtcNow);
}
