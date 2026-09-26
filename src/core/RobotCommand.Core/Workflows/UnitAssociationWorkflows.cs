namespace RobotCommand.Core;

public enum UnitAuthorityRole
{
    Command,
    Telemetry,
    Diagnostics
}

public sealed record UnitVehicleSourceBinding(string ConnectionId, string VehicleId);

public sealed record UnitCameraSourceBinding(string ConnectionId, string CameraSourceId);

public sealed record UnitCameraDeviceBinding(
    string Id,
    string Name,
    string? ControlConnectionId = null,
    string? ControlCameraSourceId = null,
    string? MediaSourceId = null,
    IReadOnlyList<string>? StandbyMediaSourceIds = null);

public enum UnitRouteRole
{
    Command,
    Gimbal,
    Telemetry,
    Diagnostics,
    Video
}

public sealed record UnitRouteCandidate(
    UnitRouteRole Role,
    string? ConnectionId = null,
    string? VehicleId = null,
    string? CameraSourceId = null,
    string? MediaSourceId = null);

public enum UnitRouteHealth
{
    Healthy,
    Degraded,
    Unavailable,
    AwaitingConfirmation
}

public sealed record UnitRouteStatus(
    string UnitId,
    UnitRouteRole Role,
    IReadOnlyList<UnitRouteCandidate> Candidates,
    UnitRouteCandidate? Active,
    UnitRouteHealth Health,
    string Detail,
    DateTimeOffset UpdatedAt);

public enum UnitSourceKind
{
    Vehicle,
    Camera
}

public sealed record UnitSourceStatus(
    UnitSourceKind Kind,
    string ConnectionId,
    string SourceId,
    string DisplayName,
    bool IsAvailable,
    string Detail);

public sealed record UnitDefinitionRequest(
    string DisplayName,
    IReadOnlyList<UnitVehicleSourceBinding> VehicleSources,
    IReadOnlyList<UnitCameraSourceBinding>? CameraSources = null,
    string? CommandAuthorityConnectionId = null,
    string? TelemetryAuthorityConnectionId = null,
    string? DiagnosticsAuthorityConnectionId = null,
    IReadOnlyList<string>? ConnectionIds = null,
    IReadOnlyList<UnitCameraDeviceBinding>? Cameras = null,
    IReadOnlyList<UnitRouteCandidate>? Routes = null);

public sealed record UnitDefinitionSnapshot(
    string Id,
    string DisplayName,
    IReadOnlyList<UnitVehicleSourceBinding> VehicleSources,
    IReadOnlyList<UnitCameraSourceBinding> CameraSources,
    string CommandAuthorityConnectionId,
    string TelemetryAuthorityConnectionId,
    string DiagnosticsAuthorityConnectionId,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<UnitSourceStatus> Sources,
    IReadOnlyList<string>? ConnectionIds = null,
    IReadOnlyList<UnitCameraDeviceBinding>? Cameras = null,
    IReadOnlyList<UnitRouteCandidate>? Routes = null);

public interface IUnitAssociationWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<UnitDefinitionSnapshot> Units { get; }
    bool TryGet(string unitId, out UnitDefinitionSnapshot? definition);
    UnitDefinitionSnapshot? FindByVehicle(string vehicleId);
    UnitDefinitionSnapshot? FindByVehicleBinding(string connectionId, string vehicleId);
    Task<UnitDefinitionSnapshot> SaveAsync(string? unitId, UnitDefinitionRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(string unitId, CancellationToken cancellationToken = default);
}

public interface IUnitRoutingWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<UnitRouteStatus> Routes { get; }
    IReadOnlyList<UnitRouteStatus> ForUnit(string unitId);
    string? ActiveConnectionFor(string unitId, UnitRouteRole role);
    Task<UnitRouteStatus> SelectAsync(string unitId, UnitRouteRole role, int candidateIndex, bool confirmControlSwitch, CancellationToken cancellationToken = default);
}
