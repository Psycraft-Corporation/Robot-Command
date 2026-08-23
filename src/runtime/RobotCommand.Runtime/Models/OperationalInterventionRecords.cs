namespace RobotCommand.Models;

public enum OperationalInterventionKind
{
    PauseMission,
    ResumeMission,
    EndMission,
    CancelMission,
    AbortMission,
    CancelTask,
    AbortTask
}

public enum OperationalInterventionStage
{
    None,
    Preparing,
    Prepared,
    Executing,
    Accepted,
    Rejected,
    Expired,
    Failed,
    Discarded
}

public sealed record PreparedOperationReference(
    string PreparationId,
    string ConfirmationToken);

public sealed record PreparedOperationTargetSnapshot(
    string? LogosInstanceId,
    string? VehicleId,
    string? VehicleBindingGeneration,
    string? MissionId,
    string? MissionExecutionId,
    string? TaskId,
    string? TaskExecutionId,
    ulong ControlStateVersion);

public sealed record PreparedOperationGatewayResult(
    bool Accepted,
    string Message,
    PreparedOperationReference? Reference,
    PreparedOperationTargetSnapshot? Target,
    string OperationType,
    bool Destructive,
    bool AuthorizationAllowed,
    string AuthorizationDecision,
    string Readiness,
    IReadOnlyList<string> Warnings,
    DateTimeOffset PreparedAt,
    DateTimeOffset ExpiresAt)
{
    public static PreparedOperationGatewayResult Rejected(string message) => new(
        false,
        message,
        null,
        null,
        string.Empty,
        false,
        false,
        "Rejected",
        "Unknown",
        [],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}

public sealed record MissionOperationPreparationRequest(
    string ConnectionId,
    OperationalInterventionKind Kind,
    string MissionId,
    string? MissionExecutionId,
    string Reason,
    bool Emergency,
    ulong ExpectedControlStateVersion,
    WorkspaceCommandIdentity Identity,
    IReadOnlyDictionary<string, string>? PolicyContext = null);

public sealed record TaskOperationPreparationRequest(
    string ConnectionId,
    OperationalInterventionKind Kind,
    string TaskId,
    string? TaskExecutionId,
    string Reason,
    bool Emergency,
    ulong ExpectedControlStateVersion,
    WorkspaceCommandIdentity Identity,
    IReadOnlyDictionary<string, string>? PolicyContext = null);

public sealed record OperationalInterventionTarget(
    string ConnectionId,
    string VehicleId,
    string VehicleName,
    string? LogosInstanceId,
    string MissionId,
    string? MissionExecutionId,
    string TaskId,
    string? TaskExecutionId,
    DateTimeOffset CapturedAt);

public sealed record OperationalInterventionPreparation(
    string OperationId,
    OperationalInterventionKind Kind,
    string DisplayName,
    OperationalInterventionTarget Target,
    string Reason,
    bool Emergency,
    bool Destructive,
    string ConfirmationPhrase,
    PreparedOperationReference Reference,
    PreparedOperationTargetSnapshot PreparedTarget,
    string AuthorizationDecision,
    string Readiness,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    OperationalInterventionStage Stage,
    DateTimeOffset PreparedAt,
    DateTimeOffset ExpiresAt)
{
    public bool RequiresTypedConfirmation => !string.IsNullOrWhiteSpace(ConfirmationPhrase);

    public bool CanExecute =>
        Stage == OperationalInterventionStage.Prepared &&
        Blockers.Count == 0 &&
        DateTimeOffset.UtcNow < ExpiresAt;

    public string ExpiryText
    {
        get
        {
            var remaining = ExpiresAt - DateTimeOffset.UtcNow;
            return remaining <= TimeSpan.Zero
                ? "Preparation expired"
                : $"Expires in {Math.Ceiling(remaining.TotalSeconds):0} seconds";
        }
    }
}

public sealed record OperationalInterventionResult(
    string OperationId,
    OperationalInterventionKind Kind,
    OperationalInterventionStage Stage,
    bool Accepted,
    string Message,
    OperationalCommandState CommandState,
    string? ExecutionId = null,
    string? LifecycleState = null,
    DateTimeOffset? CompletedAt = null);
