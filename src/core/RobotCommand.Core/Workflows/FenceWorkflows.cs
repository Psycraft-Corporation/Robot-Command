namespace RobotCommand.Core;

/// <summary>Target-neutral polygon fence kind.</summary>
public enum FenceKind { Inclusion, Exclusion }

/// <summary>
/// A reusable polygon fence. The document is target-neutral; backend-specific
/// transfer capability is evaluated only when a target operation is planned.
/// </summary>
public sealed record FenceDocument(
    string SchemaVersion,
    string FenceId,
    string DisplayName,
    FenceKind Kind,
    IReadOnlyList<FlightMissionCoordinate> Coordinates,
    string? SourceGeometryId,
    string? SourceGeometryName,
    string? SourceGeometryHash,
    double? MinimumAltitudeMetres,
    double? MaximumAltitudeMetres,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ContentSha256 = "")
{
    public const string CurrentSchemaVersion = "robotcommand.fence.v1";
}

public sealed record FenceSnapshot(FenceDocument Document, IReadOnlyList<WorkflowFinding> Findings);

public sealed record FenceTargetSnapshot(
    string VehicleId,
    string Name,
    string Backend,
    string VehicleClass,
    bool IsOnline,
    bool HasFreshGlobalPosition,
    bool SupportsAltitudeBounds,
    bool SupportsUpload,
    string Status,
    IReadOnlyList<WorkflowFinding> Findings);

public interface IFenceWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<FenceSnapshot> Fences { get; }
    IReadOnlyList<FenceTargetSnapshot> Targets { get; }

    Task<FenceSnapshot> CreateFromZoneAsync(string geometryId, string? name = null, FenceKind kind = FenceKind.Inclusion, CancellationToken cancellationToken = default);
    Task DeleteAsync(string fenceId, CancellationToken cancellationToken = default);
    Task<FenceSnapshot> ImportAsync(string path, bool replace = false, CancellationToken cancellationToken = default);
    Task ExportAsync(string fenceId, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowFinding>> ValidateAsync(string fenceId, string? vehicleId = null, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanUploadAsync(string fenceId, string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanDownloadAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanClearAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
}
