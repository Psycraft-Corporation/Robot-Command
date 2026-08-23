namespace RobotCommand.Core;

/// <summary>Canonical, front-end-neutral vehicle operations supported by the command workflow.</summary>
public enum OperatorWorkflowCommandKind
{
    Arm,
    Disarm,
    Hold,
    Takeoff,
    GoTo,
    Land,
    ReturnHome,
    ChangeAltitude,
    SetHeading
}

public enum OperatorWorkflowAvailability { Ready, Warning, Blocked, Unavailable }
public enum OperatorWorkflowState { Queued, Submitting, Accepted, InProgress, Succeeded, Cancelled, Rejected, Failed, TimedOut }
public enum OperatorWorkflowSeverity { Info, Warning, Blocking }
public enum OperatorWorkflowGoToTargetKind { GlobalWgs84, LocalNed }
public enum OperatorWorkflowAltitudeTargetKind { AltitudeAmsl, AltitudeAgl, RelativeDelta }
public enum OperatorWorkflowHeadingTargetKind { AbsoluteHeading, RelativeYaw }

/// <summary>Canonical SI/WGS84 values used by the CLI and shared Runtime workflow.</summary>
public sealed record OperatorWorkflowParameters(
    double? TakeoffAltitudeAglMetres = null,
    OperatorWorkflowGoToTargetKind? GoToTargetKind = null,
    double? GoToLatitudeDegrees = null,
    double? GoToLongitudeDegrees = null,
    double? GoToAltitudeAmslMetres = null,
    double? GoToNorthMetres = null,
    double? GoToEastMetres = null,
    double? GoToDownMetres = null,
    double? GoToYawDegrees = null,
    double? GoToAcceptanceRadiusMetres = null,
    OperatorWorkflowAltitudeTargetKind? AltitudeTargetKind = null,
    double? AltitudeAmslMetres = null,
    double? AltitudeAglMetres = null,
    double? AltitudeRelativeDeltaMetres = null,
    OperatorWorkflowHeadingTargetKind? HeadingTargetKind = null,
    double? HeadingDegrees = null,
    double? RelativeYawDegrees = null)
{
    public static OperatorWorkflowParameters None { get; } = new();
}

public sealed record OperatorWorkflowFinding(
    string Code,
    OperatorWorkflowSeverity Severity,
    string Message,
    string Source);

public sealed record OperatorCommandQueueTarget(string UnitId, OperatorWorkflowParameters? Parameters = null);

/// <summary>
/// A batch remains transparent: each target has its own prepared operation and result while sharing a stable batch id.
/// </summary>
public sealed record OperatorCommandQueueRequest(
    OperatorWorkflowCommandKind Command,
    IReadOnlyList<OperatorCommandQueueTarget> Targets,
    string Reason = "Operator request",
    string? DisplayName = null,
    bool RollbackAcceptedGoToOnPartialFailure = false);

public sealed record OperatorCommandQueueSnapshot(
    string QueueId,
    string BatchId,
    string CommandId,
    string UnitId,
    string CommandAuthorityUnitId,
    string UnitName,
    OperatorWorkflowCommandKind Command,
    string DisplayName,
    string Safety,
    OperatorWorkflowState State,
    OperatorWorkflowAvailability Availability,
    string Reason,
    OperatorWorkflowParameters Parameters,
    IReadOnlyList<OperatorWorkflowFinding> Findings,
    string PolicyDecision,
    string PolicySummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Message)
{
    // Kept as a presentation compatibility alias while GUI surfaces transition
    // from raw runtime plans to the workflow snapshot.
    public string TargetName => UnitName;
}

public sealed record OperatorCommandBatchSnapshot(
    string BatchId,
    OperatorWorkflowCommandKind Command,
    string DisplayName,
    IReadOnlyList<OperatorCommandQueueSnapshot> Commands)
{
    public bool CanExecute => Commands.Count > 0 && Commands.All(command =>
        command.State == OperatorWorkflowState.Queued &&
        command.Availability is OperatorWorkflowAvailability.Ready or OperatorWorkflowAvailability.Warning &&
        command.ExpiresAt > DateTimeOffset.UtcNow);
}

public sealed record OperatorCommandExecutionSnapshot(
    string QueueId,
    string CommandId,
    string UnitId,
    bool Accepted,
    OperatorWorkflowState State,
    string Message,
    string? OperationId = null);

public sealed record OperatorCommandWorkflowStatus(bool GatewayAvailable, string GatewayMessage);

public interface IOperatorCommandWorkflow
{
    event EventHandler? Changed;

    OperatorCommandWorkflowStatus Status { get; }
    IReadOnlyList<OperatorCommandQueueSnapshot> Commands { get; }
    IReadOnlyList<OperatorCommandQueueSnapshot> QueuedCommands { get; }
    IReadOnlyList<OperatorCommandQueueSnapshot> ActiveCommands { get; }

    bool TryGet(string queueOrCommandId, out OperatorCommandQueueSnapshot? command);
    Task<OperatorCommandBatchSnapshot> QueueAsync(OperatorCommandQueueRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperatorCommandExecutionSnapshot>> ExecuteAsync(string queueOrBatchId, CancellationToken cancellationToken = default);
    Task CancelAsync(string queueOrBatchId, string message = "Queued command cleared by operator.", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperatorCommandExecutionSnapshot>> CancelActiveAsync(IReadOnlyList<string> unitIds, CancellationToken cancellationToken = default);
}
