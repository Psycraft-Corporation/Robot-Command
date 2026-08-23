using RobotCommand.Models;
using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GStreamerRecordingArgumentsTests
{
    [Fact]
    public void BuildConsoleRecorder_UsesPrivateRawFrameInputAndSplitMatroskaOutput()
    {
        var arguments = GStreamerRecordingArguments.BuildConsoleRecorder(
            new GStreamerRawVideoOptions(1280, 720, 30),
            41234,
            Path.Combine("recordings", "segment-%05d.mkv"),
            segmentSeconds: 10,
            bitrateKbps: 5000);

        Assert.Contains("tcpclientsrc", arguments);
        Assert.Contains("host=127.0.0.1", arguments);
        Assert.Contains("port=41234", arguments);
        Assert.Contains("do-timestamp=true", arguments);
        Assert.Contains("rawvideoparse", arguments);
        Assert.Contains("format=bgra", arguments);
        Assert.Contains("width=1280", arguments);
        Assert.Contains("height=720", arguments);
        Assert.Contains("framerate=30/1", arguments);
        Assert.Contains("x264enc", arguments);
        Assert.Contains("bitrate=5000", arguments);
        Assert.Contains("key-int-max=300", arguments);
        Assert.Contains("splitmuxsink", arguments);
        Assert.Contains("max-size-time=10000000000", arguments);
        Assert.Contains("muxer-factory=matroskamux", arguments);
    }

    [Fact]
    public void BuildConsoleRecorder_RejectsUnsafeSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GStreamerRecordingArguments.BuildConsoleRecorder(
                new GStreamerRawVideoOptions(640, 360, 30),
                0,
                "segment-%05d.mkv",
                10,
                4000));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GStreamerRecordingArguments.BuildConsoleRecorder(
                new GStreamerRawVideoOptions(640, 360, 30),
                4000,
                "segment-%05d.mkv",
                1,
                4000));
    }
}
