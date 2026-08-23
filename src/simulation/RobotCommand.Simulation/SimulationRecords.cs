using RobotCommand.Core;

namespace RobotCommand.Simulation;

public sealed record SimulationPoint(double LatitudeDegrees, double LongitudeDegrees, double AltitudeAglMetres = 0);

public sealed record SimulationGhostSnapshot(
    string Id,
    string Name,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeAglMetres,
    double HeadingDegrees,
    double NorthVelocityMetresPerSecond,
    double EastVelocityMetresPerSecond,
    double VerticalVelocityMetresPerSecond,
    bool Armed,
    bool Landed,
    string Mode,
    string? Operation,
    long MissionItem,
    long MissionItemCount,
    string ProfileId = "dracula");

public sealed record SimulationSnapshot(
    int ProtocolVersion,
    string WorkerInstanceId,
    long Sequence,
    DateTimeOffset CapturedAt,
    DateTimeOffset HeartbeatAt,
    IReadOnlyList<SimulationGhostSnapshot> Ghosts,
    SimulationPerformanceMetrics? Metrics = null);

public sealed record SimulationPerformanceMetrics(
    long PhysicsTicks,
    long MissedDeadlines,
    double LastTickMilliseconds,
    double MaxTickMilliseconds,
    double SnapshotPublicationRateHz,
    double PhysicsP95Milliseconds = 0,
    long SnapshotPublications = 0);

public enum SimulationCommandKind
{
    Create,
    Delete,
    Arm,
    Disarm,
    Takeoff,
    Land,
    Hold,
    GoTo,
    ChangeAltitude,
    StartRoute,
    PauseRoute,
    ResumeRoute,
    Shutdown
}

public sealed record SimulationCommand(
    string RequestId,
    SimulationCommandKind Kind,
    string? TargetId = null,
    double? LatitudeDegrees = null,
    double? LongitudeDegrees = null,
    double? AltitudeAglMetres = null,
    IReadOnlyList<SimulationPoint>? Route = null,
    string ProfileId = "dracula",
    GhostProfileSnapshot? Profile = null);

public sealed record SimulationCommandResult(
    string RequestId,
    bool Accepted,
    string Code,
    string Message,
    long Sequence);
