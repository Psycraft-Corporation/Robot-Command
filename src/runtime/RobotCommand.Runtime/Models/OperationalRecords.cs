namespace RobotCommand.Models;

public enum ConnectionMode
{
    Direct,
    FieldLink,
    Ghost,
    Mavlink,
    /// <summary>A read-only observation projected from another Robot Command.</summary>
    TeamObserver
}

public enum AvailabilityState
{
    Unknown,
    Connecting,
    Reconnecting,
    Online,
    Degraded,
    Stale,
    Offline,
    Faulted
}

public sealed record ConnectionRecord(
    string Id,
    string Name,
    string Target,
    ConnectionMode Mode,
    AvailabilityState State,
    bool AutoReconnect,
    string? LogosInstanceId = null,
    string? RuntimeRole = null,
    DateTimeOffset? ConnectedAt = null,
    DateTimeOffset? LastConnectedAt = null,
    DateTimeOffset? LastSeen = null,
    DateTimeOffset? LastAttempt = null,
    string? LastError = null,
    bool IsGhost = false);

public sealed record RuntimeRecord(
    string Id,
    string Name,
    IReadOnlyList<string> ConnectionIds,
    AvailabilityState State,
    string Role,
    string RuntimeMode,
    string PlatformKind,
    string PlatformProfile,
    string LogosVersion,
    string Health,
    string Readiness,
    IReadOnlyList<string> CapabilityKeys,
    DateTimeOffset? LastSeen = null,
    string? VehicleId = null,
    string? VehicleName = null,
    bool IsGhost = false);

public sealed record TeamRecord(
    string Id,
    string Name,
    IReadOnlyList<string> ConnectionIds,
    AvailabilityState State,
    int MemberCount = 0,
    bool IsPartial = true,
    string? ManagerLogosInstanceId = null,
    DateTimeOffset? LastSeen = null);

public sealed record VehicleRecord(
    string Id,
    string Name,
    IReadOnlyList<string> ConnectionIds,
    string? LogosInstanceId,
    string? TeamId,
    string VehicleClass,
    string Domain,
    string ProfileKey,
    AvailabilityState State,
    string Readiness = "Unknown",
    string Lifecycle = "Unknown",
    string ArmState = "Unknown",
    string Health = "Unknown",
    IReadOnlyList<string>? CapabilityKeys = null,
    DateTimeOffset? LastSeen = null,
    bool IsGhost = false);

public enum PlanValidationState
{
    NotValidated,
    Valid,
    Warning,
    Invalid,
    Unavailable
}

public sealed record MissionRecord(
    string Id,
    string Name,
    string State,
    string? AssignedTeamId = null,
    string? AssignedVehicleId = null,
    string? ConnectionId = null,
    string Objective = "",
    string Priority = "Normal",
    string PolicyId = "",
    IReadOnlyList<string>? GeometryIds = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    string PayloadJson = "",
    string? SourcePath = null,
    PlanValidationState ValidationState = PlanValidationState.NotValidated,
    string ValidationSummary = "Not validated",
    string? MissionExecutionId = null,
    double? Progress = null,
    DateTimeOffset? ObservedAt = null,
    bool IsLocalDraft = true);

public sealed record OperationalTaskRecord(
    string Id,
    string Name,
    string State,
    string? MissionId = null,
    string? AssignedVehicleId = null,
    string? ConnectionId = null,
    string? TeamId = null,
    string? AssignedMemberId = null,
    string? AssignedLogosInstanceId = null,
    string Objective = "",
    string TaskType = "",
    string Priority = "Normal",
    string BehaviourId = "",
    string BehaviourVersion = "",
    string PackageId = "",
    string ParametersJson = "",
    string AssignmentState = "Unassigned",
    PlanValidationState ValidationState = PlanValidationState.NotValidated,
    string ValidationSummary = "Not validated",
    string? TaskExecutionId = null,
    double? Progress = null,
    DateTimeOffset? ObservedAt = null,
    string? SourcePath = null,
    bool IsLocalDraft = true,
    IReadOnlyList<string>? GeometryIds = null);

public enum OperationalCommandState
{
    Draft,
    Submitting,
    Accepted,
    InProgress,
    Succeeded,
    Cancelled,
    Rejected,
    Failed,
    TimedOut
}

public sealed record OperationalCommandRecord(
    string Id,
    string Kind,
    string TargetKind,
    string TargetId,
    string? ConnectionId,
    OperationalCommandState State,
    string Summary,
    string Message,
    string CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? VehicleId = null,
    string? LogosInstanceId = null,
    string? IdempotencyKey = null,
    string? PolicyDecision = null,
    string? Reason = null,
    bool Emergency = false);

public sealed record ConsoleEventRecord(
    string Id,
    DateTimeOffset Timestamp,
    string Severity,
    string Source,
    string Message,
    string? ConnectionId = null,
    string? LogosInstanceId = null,
    string? Domain = null,
    string? Kind = null,
    string? Code = null,
    string? SubjectId = null);
