namespace RobotCommand.Models;

public sealed record GeometryQuery(
    IReadOnlyList<GeometryDocumentKind>? Kinds = null,
    IReadOnlyList<GeometryCoordinateFrame>? Frames = null,
    IReadOnlyList<string>? PolicyKinds = null,
    IReadOnlyList<string>? PolicyConstraints = null,
    bool IncludeObjects = false,
    bool Refresh = false,
    IReadOnlyList<string>? PolicyOperations = null,
    IReadOnlyList<string>? PolicyTags = null);

public sealed record RemoteGeometryRecord(
    string ConnectionId,
    string GeometryId,
    GeometryDocumentKind Kind,
    string DisplayName,
    GeometryCoordinateFrame Frame,
    bool Closed,
    int PointCount,
    int RingCount,
    ulong SizeBytes,
    string Sha256,
    string? Revision,
    DateTimeOffset? UpdatedAt,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record RemoteGeometryObject(
    GeometryDocument Document,
    RemoteGeometryRecord Record);

public enum GeometryRegistryState
{
    Unknown,
    Ready,
    Reloading,
    Degraded,
    Failed
}

public sealed record GeometryRegistrySnapshot(
    string ConnectionId,
    GeometryRegistryState State,
    string Health,
    string Readiness,
    string Code,
    string Message,
    int ObjectCount,
    string RegistrySignature,
    DateTimeOffset? LoadedAt,
    DateTimeOffset CheckedAt,
    IReadOnlyList<GeometryValidationIssue> Issues)
{
    public static GeometryRegistrySnapshot Unknown(string connectionId, string message = "Geometry registry status is unavailable.")
        => new(
            connectionId,
            GeometryRegistryState.Unknown,
            "Unknown",
            "Unknown",
            "GEOMETRY_REGISTRY_UNKNOWN",
            message,
            0,
            string.Empty,
            null,
            DateTimeOffset.UtcNow,
            []);
}

public enum GeometryRegistryEventKind
{
    Unknown,
    Snapshot,
    Created,
    Updated,
    Deleted,
    Reloaded,
    StatusUpdate,
    Heartbeat
}

public sealed record GeometryRegistryEvent(
    string ConnectionId,
    GeometryRegistryEventKind Kind,
    GeometryRegistrySnapshot? Registry,
    RemoteGeometryRecord? Record,
    RemoteGeometryObject? Object,
    DateTimeOffset ObservedAt);

public enum GeometryCommandState
{
    Accepted,
    Rejected,
    Conflict,
    Failed
}

public sealed record GeometryCommandResult(
    bool Accepted,
    GeometryCommandState State,
    string Message,
    RemoteGeometryObject? Geometry = null,
    GeometryValidationResult? Validation = null,
    string? OperationId = null);

public sealed record GeometryCreateRequest(
    string ConnectionId,
    GeometryDocument Document,
    bool ValidateOnCreate = true,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

public sealed record GeometryUpdateRequest(
    string ConnectionId,
    GeometryDocument Document,
    string? ExpectedRevision,
    bool ValidateOnUpdate = true,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

public sealed record GeometryDeleteRequest(
    string ConnectionId,
    string GeometryId,
    bool AllowDeleteReferenced = false,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);
