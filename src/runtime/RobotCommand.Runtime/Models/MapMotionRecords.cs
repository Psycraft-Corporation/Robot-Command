namespace RobotCommand.Models;

/// <summary>
/// Display-only vehicle motion. It is never used for command, safety, or
/// formation decisions; those paths continue to consume raw telemetry.
/// </summary>
public sealed record MapVehicleMotionSample(
    string VehicleId,
    MapFrameKind Frame,
    double X,
    double Y,
    double? HeadingDegrees,
    double? VelocityXPerSecond,
    double? VelocityYPerSecond,
    double? VelocityZPerSecond,
    AvailabilityState State,
    string AirframeMode,
    string LandedState,
    bool IsStale,
    DateTimeOffset SourceTimestamp,
    long PositionResetRevision = 0,
    double? GimbalPitchDegrees = null,
    double? GimbalYawDegrees = null,
    bool? GimbalYawInEarthFrame = null);

public sealed record MapVehicleMotionSnapshot(
    MapFrameKind Frame,
    IReadOnlyList<MapVehicleMotionSample> Vehicles,
    DateTimeOffset CapturedAt)
{
    public static MapVehicleMotionSnapshot Empty { get; } =
        new(MapFrameKind.Unknown, [], DateTimeOffset.MinValue);

    public IReadOnlyList<MapVehicleTrailVisual> Trails { get; init; } = [];

    public bool TrailsUpdated { get; init; }
}
