using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public sealed record LogosConnectionLiveSnapshot(
    IReadOnlyList<VehicleTelemetryRecord> Telemetry,
    IReadOnlyList<LinkRecord> Links,
    IReadOnlyList<ConsoleEventRecord> Events,
    IReadOnlyList<LiveStreamRecord> Streams,
    IReadOnlyList<GeometryOverlayRecord> Geometries,
    IReadOnlyList<PerceptionTrackRecord> Tracks,
    IReadOnlyList<CameraSourceRecord> CameraSources,
    IReadOnlyList<CameraStreamRecord> CameraStreams,
    IReadOnlyList<VehicleDiagnosticsSnapshot>? Diagnostics = null)
{
    public static LogosConnectionLiveSnapshot Empty { get; } =
        new([], [], [], [], [], [], [], []);

    public bool HasLiveStreams => Streams.Any(item => item.State == LiveStreamState.Live);

    public IReadOnlyList<VehicleDiagnosticsSnapshot> VehicleDiagnostics => Diagnostics ?? [];
}
