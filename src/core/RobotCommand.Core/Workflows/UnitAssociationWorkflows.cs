namespace RobotCommand.Core;

public enum UnitAuthorityRole
{
    Command,
    Telemetry,
    Diagnostics
}

public sealed record UnitVehicleSourceBinding(string ConnectionId, string VehicleId);

public sealed record UnitCameraSourceBinding(string ConnectionId, string CameraSourceId);

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
    IReadOnlyList<string>? ConnectionIds = null);

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
    IReadOnlyList<string>? ConnectionIds = null);

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
