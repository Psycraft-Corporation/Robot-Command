namespace RobotCommand.Models;

public enum GStreamerRuntimeState
{
    Unknown,
    Inspecting,
    Available,
    Missing,
    Faulted
}

public sealed record GStreamerRuntimeDiagnostics(
    GStreamerRuntimeState State,
    string Summary,
    string Detail,
    string? LaunchExecutable = null,
    string? InspectExecutable = null,
    string? Version = null,
    IReadOnlyList<string>? MissingPlugins = null,
    IReadOnlyList<string>? AvailablePlugins = null,
    IReadOnlyList<GStreamerProtocolCapability>? ProtocolCapabilities = null)
{
    public static GStreamerRuntimeDiagnostics Unknown { get; } = new(
        GStreamerRuntimeState.Unknown,
        "GStreamer not inspected",
        "Runtime status is available in Settings.");

    public bool Available => State == GStreamerRuntimeState.Available;

    public IReadOnlyList<string> RequiredPluginsMissing => MissingPlugins ?? [];

    public IReadOnlyList<string> PluginsAvailable => AvailablePlugins ?? [];

    public IReadOnlyList<GStreamerProtocolCapability> Protocols => ProtocolCapabilities ?? [];

    public bool HasPlugin(string plugin)
        => PluginsAvailable.Contains(plugin, StringComparer.OrdinalIgnoreCase);

    public bool Supports(VideoProtocolPreference protocol)
        => Protocols.FirstOrDefault(item => item.Protocol == protocol)?.Available == true;
}

public sealed record GStreamerProtocolCapability(
    VideoProtocolPreference Protocol,
    bool Available,
    IReadOnlyList<string> MissingPlugins,
    string Detail);

public enum NativeVideoPipelineState
{
    Stopped,
    InspectingRuntime,
    Starting,
    Playing,
    Stopping,
    Faulted
}

public sealed record NativeVideoPipelineStatus(
    NativeVideoPipelineState State,
    string Summary,
    string Detail,
    DateTimeOffset UpdatedAt,
    string? PipelineDescription = null,
    string? LastError = null)
{
    public static NativeVideoPipelineStatus Stopped { get; } = new(
        NativeVideoPipelineState.Stopped,
        "Native video stopped",
        "Open a camera stream to begin playback.",
        DateTimeOffset.UtcNow);

    public bool Running => State is NativeVideoPipelineState.Starting or NativeVideoPipelineState.Playing;
}

public sealed record VideoFrameInfo(
    int Width,
    int Height,
    int Stride,
    long Sequence,
    DateTimeOffset Timestamp)
{
    public int RequiredBytes => checked(Stride * Height);
}

public sealed record GStreamerRawVideoOptions(
    int Width,
    int Height,
    int FrameRate)
{
    public static GStreamerRawVideoOptions Default { get; } = new(960, 540, 30);
}

public sealed record GStreamerTestSourceOptions(
    int Width,
    int Height,
    int FrameRate,
    string Pattern)
{
    public static GStreamerTestSourceOptions Default { get; } = new(960, 540, 30, "smpte");

    public GStreamerRawVideoOptions Output => new(Width, Height, FrameRate);
}

public enum RtspTransportMode
{
    Automatic,
    Tcp,
    Udp,
    UdpMulticast
}

public sealed record RtspPlaybackOptions(
    string Endpoint,
    GStreamerRawVideoOptions Output,
    int LatencyMilliseconds,
    RtspTransportMode Transport,
    string StreamId,
    string CameraSourceId,
    string Codec);


public enum NativeVideoProtocol
{
    Unknown,
    Rtsp,
    Srt,
    Hls,
    Whep
}

public sealed record SrtPlaybackOptions(
    string Endpoint,
    GStreamerRawVideoOptions Output,
    int LatencyMilliseconds,
    string StreamId,
    string CameraSourceId,
    string Codec);

public sealed record HlsPlaybackOptions(
    string Endpoint,
    GStreamerRawVideoOptions Output,
    int TimeoutSeconds,
    string StreamId,
    string CameraSourceId,
    string Codec);

public sealed record WhepPlaybackOptions(
    string Endpoint,
    GStreamerRawVideoOptions Output,
    string Codec,
    int PayloadType,
    string? AuthToken,
    int TimeoutSeconds,
    bool UseLinkHeaders,
    string? StunServer,
    string? TurnServer,
    string StreamId,
    string CameraSourceId);
