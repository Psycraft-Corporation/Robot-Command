using RobotCommand.Models;
using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GStreamerPipelineArgumentsTests
{
    [Fact]
    public void BuildTestPattern_ForcesFixedBgraCapsAndPrivateLoopbackSink()
    {
        var arguments = GStreamerPipelineArguments.BuildTestPattern(
            new GStreamerTestSourceOptions(640, 360, 25, "ball"),
            loopbackPort: 41234);

        Assert.Contains("videotestsrc", arguments);
        Assert.Contains("pattern=ball", arguments);
        Assert.Contains("video/x-raw,format=BGRA,width=640,height=360,framerate=25/1,pixel-aspect-ratio=1/1", arguments);
        Assert.Contains("tcpclientsink", arguments);
        Assert.Contains("host=127.0.0.1", arguments);
        Assert.Contains("port=41234", arguments);
        Assert.Contains("leaky=downstream", arguments);
    }

    [Fact]
    public void BuildTestPattern_FallsBackForUnknownPattern()
    {
        var arguments = GStreamerPipelineArguments.BuildTestPattern(
            new GStreamerTestSourceOptions(320, 180, 30, "not-a-pattern"),
            loopbackPort: 41234);

        Assert.Contains("pattern=smpte", arguments);
    }

    [Fact]
    public void BuildFile_UsesAbsoluteExistingPath()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "clip with spaces.mp4");
        File.WriteAllBytes(path, [0]);

        var arguments = GStreamerPipelineArguments.BuildFile(
            path,
            new GStreamerRawVideoOptions(640, 360, 30),
            loopbackPort: 41234);

        Assert.Contains($"location={Path.GetFullPath(path)}", arguments);
        Assert.Contains("decodebin", arguments);
    }

    [Fact]
    public void BuildRtsp_UsesNegotiatedEndpointAndConfiguredTransport()
    {
        var arguments = GStreamerPipelineArguments.BuildRtsp(
            new RtspPlaybackOptions(
                "rtsp://camera.local:8554/front",
                new GStreamerRawVideoOptions(1280, 720, 30),
                125,
                RtspTransportMode.Tcp,
                "stream-1",
                "front",
                "h264"),
            loopbackPort: 41234);

        Assert.Contains("rtspsrc", arguments);
        Assert.Contains("location=rtsp://camera.local:8554/front", arguments);
        Assert.Contains("latency=125", arguments);
        Assert.Contains("protocols=tcp", arguments);
        Assert.Contains("decodebin", arguments);
        Assert.Contains("video/x-raw,format=BGRA,width=1280,height=720,framerate=30/1,pixel-aspect-ratio=1/1", arguments);
        Assert.Contains("host=127.0.0.1", arguments);
    }

    [Fact]
    public void BuildRtsp_AutomaticTransportDoesNotForceProtocols()
    {
        var arguments = GStreamerPipelineArguments.BuildRtsp(
            new RtspPlaybackOptions(
                "rtsps://camera.local/front",
                new GStreamerRawVideoOptions(640, 360, 25),
                0,
                RtspTransportMode.Automatic,
                "stream-1",
                "front",
                "h264"),
            loopbackPort: 41234);

        Assert.DoesNotContain(arguments, item => item.StartsWith("protocols=", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_RedactsRtspCredentialsAndQuery()
    {
        var description = GStreamerPipelineArguments.Describe(
        [
            "rtspsrc",
            "location=rtsp://operator:secret@camera.local:8554/front?token=abc",
            "!",
            "decodebin"
        ]);

        Assert.Contains("camera.local:8554/front?redacted", description);
        Assert.DoesNotContain("operator", description);
        Assert.DoesNotContain("secret", description);
        Assert.DoesNotContain("token=abc", description);
    }

    [Fact]
    public void RedactText_RemovesCredentialsFromGStreamerDiagnosticLine()
    {
        var text = GStreamerPipelineArguments.RedactText(
            "ERROR opening rtsp://operator:secret@camera.local/front?token=abc");

        Assert.Contains("camera.local/front?redacted", text);
        Assert.DoesNotContain("operator", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("token=abc", text);
    }

    [Fact]
    public void BuildRtsp_RejectsNonRtspEndpoint()
    {
        Assert.Throws<ArgumentException>(() =>
            GStreamerPipelineArguments.BuildRtsp(
                new RtspPlaybackOptions(
                    "https://camera.local/front",
                    new GStreamerRawVideoOptions(640, 360, 30),
                    100,
                    RtspTransportMode.Tcp,
                    "stream-1",
                    "front",
                    "h264"),
                loopbackPort: 41234));
    }

    [Fact]
    public void BuildTestPattern_RejectsUnsafeDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GStreamerPipelineArguments.BuildTestPattern(
                new GStreamerTestSourceOptions(0, 360, 30, "smpte"),
                loopbackPort: 41234));
    }

    [Fact]
    public void BuildTestPattern_RejectsInvalidLoopbackPort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GStreamerPipelineArguments.BuildTestPattern(
                new GStreamerTestSourceOptions(640, 360, 30, "smpte"),
                loopbackPort: 0));
    }


    [Fact]
    public void BuildSrt_UsesMpegTsDemuxAndConfiguredLatency()
    {
        var arguments = GStreamerPipelineArguments.BuildSrt(
            new SrtPlaybackOptions(
                "srt://camera.local:8890?streamid=read:front",
                new GStreamerRawVideoOptions(1280, 720, 30),
                125,
                "stream-1",
                "front",
                "h264"),
            41234);

        Assert.Contains("srtsrc", arguments);
        Assert.Contains("latency=125", arguments);
        Assert.Contains("auto-reconnect=true", arguments);
        Assert.Contains("tsdemux", arguments);
    }

    [Fact]
    public void BuildHls_UsesNativeHttpAndHlsDemux()
    {
        var arguments = GStreamerPipelineArguments.BuildHls(
            new HlsPlaybackOptions(
                "https://camera.local/front/index.m3u8?token=secret",
                new GStreamerRawVideoOptions(960, 540, 30),
                20,
                "stream-1",
                "front",
                "h264"),
            41234);

        Assert.Contains("souphttpsrc", arguments);
        Assert.Contains("timeout=20", arguments);
        Assert.Contains("hlsdemux", arguments);
        Assert.DoesNotContain("token=secret", GStreamerPipelineArguments.Describe(arguments));
    }

    [Fact]
    public void BuildWhep_UsesCodecCapsDepayloaderAndRedactsAuthToken()
    {
        var arguments = GStreamerPipelineArguments.BuildWhep(
            new WhepPlaybackOptions(
                "https://camera.local/front/whep",
                new GStreamerRawVideoOptions(1280, 720, 30),
                "h264",
                127,
                "secret-token",
                15,
                true,
                null,
                null,
                "stream-1",
                "front"),
            41234);

        Assert.Contains("whepsrc", arguments);
        Assert.Contains("video-caps=application/x-rtp,media=video,encoding-name=H264,payload=127,clock-rate=90000", arguments);
        Assert.Contains("rtph264depay", arguments);
        var description = GStreamerPipelineArguments.Describe(arguments);
        Assert.DoesNotContain("secret-token", description);
        Assert.Contains("auth-token=<redacted>", description);
    }

    [Fact]
    public void RedactText_RemovesSrtAndHttpQuerySecrets()
    {
        var text = GStreamerPipelineArguments.RedactText(
            "srt://host:8890?streamid=read:x&passphrase=secret https://host/x.m3u8?token=abc");

        Assert.DoesNotContain("passphrase=secret", text);
        Assert.DoesNotContain("token=abc", text);
        Assert.Equal(2, text.Split("?redacted", StringSplitOptions.None).Length - 1);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Logos-robot-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
