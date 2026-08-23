using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public static class GStreamerRecordingArguments
{
    public static IReadOnlyList<string> BuildConsoleRecorder(
        GStreamerRawVideoOptions input,
        int loopbackPort,
        string locationPattern,
        int segmentSeconds,
        int bitrateKbps)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (loopbackPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(loopbackPort));
        }
        if (string.IsNullOrWhiteSpace(locationPattern))
        {
            throw new ArgumentException("A recording location pattern is required.", nameof(locationPattern));
        }
        if (segmentSeconds is < 2 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentSeconds));
        }
        if (bitrateKbps is < 128 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        }

        var segmentNanoseconds = checked((long)segmentSeconds * 1_000_000_000L);
        var keyFrameInterval = Math.Max(1, input.FrameRate * segmentSeconds);
        return
        [
            "-e", "-q",
            "tcpclientsrc", "host=127.0.0.1", $"port={loopbackPort}", "timeout=5", "do-timestamp=true",
            "!", "rawvideoparse", "use-sink-caps=false", "format=bgra",
            $"width={input.Width}", $"height={input.Height}", $"framerate={input.FrameRate}/1",
            "!", "videoconvert",
            "!", "queue", "max-size-buffers=4", "leaky=downstream",
            "!", "x264enc", "tune=zerolatency", "speed-preset=veryfast",
            $"bitrate={bitrateKbps}", $"key-int-max={keyFrameInterval}", "bframes=0",
            "!", "h264parse", "config-interval=-1",
            "!", "splitmuxsink", $"location={locationPattern}",
            $"max-size-time={segmentNanoseconds}", "max-size-bytes=0",
            "send-keyframe-requests=true", "async-finalize=true",
            "muxer-factory=matroskamux"
        ];
    }

    public static IReadOnlyList<string> RequiredPlugins { get; } =
    [
        "tcpclientsrc",
        "rawvideoparse",
        "videoconvert",
        "queue",
        "x264enc",
        "h264parse",
        "splitmuxsink",
        "matroskamux"
    ];
}
