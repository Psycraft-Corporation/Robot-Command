namespace RobotCommand.Core;

/// <summary>Session-only formation ownership for a local Team.</summary>
public enum FormationLockState
{
    Unlocked,
    Locked,
    Moving,
    Interrupted,
    Unsupported
}

public enum FormationLockSeverity { Info, Warning, Blocking }

[Flags]
public enum FormationControlCapabilities
{
    None = 0,
    Translation = 1 << 0,
    Altitude = 1 << 1,
    Rotation = 1 << 2,
    Scale = 1 << 3
}

public sealed record FormationLockFinding(string Code, FormationLockSeverity Severity, string Message);

/// <summary>Backend-owned state for one active formation participant.</summary>
public enum FormationMemberControlState { Pending, Active, Holding, Failed, Unsupported }

/// <summary>
/// A controller-neutral member target. North/East/Up velocity is expressed in
/// the global local-tangent frame; yaw is intentionally not part of a
/// formation target. TargetRevision lets a backend discard an older batch if
/// an operator action arrives while a previous batch is still in flight.
/// </summary>
public sealed record FormationMemberTarget(
    string LockId,
    string UnitId,
    double TeamLatitudeDegrees,
    double TeamLongitudeDegrees,
    double TeamAltitudeAglMetres,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeAglMetres,
    double VelocityNorthMetresPerSecond,
    double VelocityEastMetresPerSecond,
    double VelocityUpMetresPerSecond,
    DateTimeOffset CapturedAt,
    long TargetRevision = 0,
    long PoseRevision = 0);

public sealed record FormationExecutorResult(
    bool Accepted,
    FormationMemberControlState State,
    string? Detail = null,
    DateTimeOffset? LastSetpointAt = null)
{
    public static FormationExecutorResult AcceptedActive(string? detail = null)
        => new(true, FormationMemberControlState.Active, detail, DateTimeOffset.UtcNow);
    public static FormationExecutorResult Rejected(string detail)
        => new(false, FormationMemberControlState.Failed, detail, null);
}

/// <summary>North/East/Up offset from the stable virtual Team position.</summary>
public sealed record FormationLockMemberSnapshot(
    string UnitId,
    string Name,
    double NorthOffsetMetres,
    double EastOffsetMetres,
    double UpOffsetMetres,
    bool IsActive,
    string? Detail = null,
    string? Backend = null,
    FormationMemberControlState ControlState = FormationMemberControlState.Pending,
    DateTimeOffset? LastSetpointAt = null,
    double? TargetNorthOffsetMetres = null,
    double? TargetEastOffsetMetres = null,
    double? TargetUpOffsetMetres = null,
    bool IsAtTarget = true);

/// <summary>
/// The Team position is captured at lock time and only moves through explicit
/// formation commands. It is never recalculated when Team membership changes.
/// </summary>
public sealed record FormationLockSnapshot(
    string? TeamId,
    string? TeamName,
    FormationLockState State,
    double? TeamLatitudeDegrees,
    double? TeamLongitudeDegrees,
    double? TeamAltitudeAglMetres,
    double? TargetLatitudeDegrees,
    double? TargetLongitudeDegrees,
    double? TargetAltitudeAglMetres,
    IReadOnlyList<FormationLockMemberSnapshot> Members,
    IReadOnlyList<FormationLockFinding> Findings,
    string? InterruptionReason,
    DateTimeOffset CapturedAt,
    double CurrentRotationDegrees = 0d,
    double TargetRotationDegrees = 0d,
    double CurrentScalePercent = 100d,
    double TargetScalePercent = 100d,
    bool IsConverged = true,
    string? InterruptionCode = null)
{
    public static FormationLockSnapshot Unlocked { get; } = new(
        null, null, FormationLockState.Unlocked, null, null, null, null, null, null,
        [], [], null, DateTimeOffset.UtcNow);

    public bool IsLocked => State is FormationLockState.Locked or FormationLockState.Moving;
}

public interface IFormationLockExecutor
{
    string Backend { get; }
    FormationControlCapabilities Capabilities => FormationControlCapabilities.Translation | FormationControlCapabilities.Altitude;
    bool CanHandle(UnitObservationSnapshot unit);
    Task<IReadOnlyList<FormationLockFinding>> ValidateAsync(
        IReadOnlyList<UnitObservationSnapshot> units,
        CancellationToken cancellationToken = default);

    Task<FormationExecutorResult> BeginAsync(
        UnitObservationSnapshot unit,
        FormationMemberTarget target,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FormationExecutorResult.Rejected($"{Backend} formation control is not implemented."));

    Task<FormationExecutorResult> UpdateAsync(
        UnitObservationSnapshot unit,
        FormationMemberTarget target,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FormationExecutorResult.Rejected($"{Backend} formation control is not implemented."));

    Task<FormationExecutorResult> HoldAndReleaseAsync(
        UnitObservationSnapshot unit,
        string lockId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FormationExecutorResult.AcceptedActive());

    /// <summary>
    /// Reports whether the backend's current telemetry has reached the latest
    /// absolute target. Ghosts may use their deterministic simulator target;
    /// live backends must include current telemetry and their expected control
    /// mode in this decision.
    /// </summary>
    bool IsTargetConverged(UnitObservationSnapshot unit, FormationMemberTarget target) => true;

    /// <summary>
    /// Reports whether the backend still owns the member in its formation
    /// control mode. A backend that does not need a mode transition may keep
    /// the default true value.
    /// </summary>
    bool IsControlActive(UnitObservationSnapshot unit) => true;
}

public interface IFormationLockWorkflow
{
    event EventHandler? Changed;
    FormationLockSnapshot Current { get; }
    bool IsUnitLocked(string unitId);
    Task<FormationLockSnapshot> LockAsync(string teamId, CancellationToken cancellationToken = default);
    Task<FormationLockSnapshot> UnlockAsync(string? teamId = null, string reason = "Operator unlocked the formation.", CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanMoveToAsync(string teamId, double latitudeDegrees, double longitudeDegrees, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanChangeAltitudeAsync(string teamId, double altitudeAglMetres, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanRotateAsync(string teamId, double signedDegrees, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanScaleAsync(string teamId, double percent, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanHoldAsync(string teamId, CancellationToken cancellationToken = default);
    Task<FormationLockSnapshot> MoveToAsync(string teamId, double latitudeDegrees, double longitudeDegrees, CancellationToken cancellationToken = default);
    Task<FormationLockSnapshot> ChangeAltitudeAsync(string teamId, double altitudeAglMetres, CancellationToken cancellationToken = default);
    Task<FormationLockSnapshot> RotateAsync(string teamId, double signedDegrees, CancellationToken cancellationToken = default);
    Task<FormationLockSnapshot> ScaleAsync(string teamId, double percent, CancellationToken cancellationToken = default);
    Task<FormationLockSnapshot> HoldAsync(string teamId, CancellationToken cancellationToken = default);
    Task HandleIndependentOperationAsync(string unitId, OperatorWorkflowCommandKind command, CancellationToken cancellationToken = default);
    Task HandleUnitDeletedAsync(string unitId, CancellationToken cancellationToken = default);
}
