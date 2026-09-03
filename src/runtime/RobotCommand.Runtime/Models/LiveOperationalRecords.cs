namespace RobotCommand.Models;

public enum LiveStreamKind
{
    Health,
    VehicleState,
    VehicleTelemetry,
    Links,
    Events,
    CameraStatus,
    PerceptionTracks
}

public enum LiveStreamState
{
    Stopped,
    Starting,
    Live,
    BackingOff,
    Faulted
}

public sealed record LiveStreamRecord(
    string Id,
    string ConnectionId,
    LiveStreamKind Kind,
    LiveStreamState State,
    DateTimeOffset? LastMessageAt = null,
    DateTimeOffset? LastStartedAt = null,
    int RestartCount = 0,
    string? LastError = null);

public sealed record VehicleTelemetryRecord(
    string Id,
    string VehicleId,
    string ConnectionId,
    string? LogosInstanceId,
    AvailabilityState State,
    bool Armed,
    string LandedState,
    string AirframeMode,
    string AdapterState,
    string Health,
    string Readiness,
    double? LatitudeDegrees,
    double? LongitudeDegrees,
    double? AltitudeMslMetres,
    double? AltitudeAglMetres,
    double? LocalNorthMetres,
    double? LocalEastMetres,
    double? LocalDownMetres,
    double? VelocityNorthMetresPerSecond,
    double? VelocityEastMetresPerSecond,
    double? VelocityDownMetresPerSecond,
    double? HeadingDegrees,
    bool IsStale,
    string Code,
    string Message,
    DateTimeOffset ObservedAt,
    bool IsGhost = false,
    double? GimbalPitchDegrees = null,
    double? GimbalYawDegrees = null,
    double? GimbalRollDegrees = null,
    double? CameraZoomPercent = null,
    bool? CameraRecordingVideo = null,
    double? BatteryRemainingPercent = null,
    double? BatteryVoltageVolts = null,
    DateTimeOffset? BatteryObservedAt = null,
    bool? GimbalYawInEarthFrame = null);

public sealed record LinkRecord(
    string Id,
    string LinkId,
    string ConnectionId,
    string? LogosInstanceId,
    string Name,
    string Kind,
    string Direction,
    string State,
    string Health,
    string Readiness,
    bool Connected,
    bool IsStale,
    string? PeerId,
    string? PeerKind,
    string? PeerName,
    double? RssiDbm,
    double? SnrDb,
    double? Quality,
    double? PacketLoss,
    double? LatencyMilliseconds,
    string Code,
    string Message,
    DateTimeOffset ObservedAt);
