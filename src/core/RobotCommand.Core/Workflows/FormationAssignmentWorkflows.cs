namespace RobotCommand.Core;

/// <summary>Session-only lifecycle for entering a persistent authored formation.</summary>
public enum FormationAssignmentState
{
    Unassigned,
    Assigned,
    Transitioning,
    InFormation,
    Interrupted
}

public sealed record FormationSlotAssignmentSnapshot(
    string UnitId,
    string UnitName,
    string SlotId,
    string SlotName,
    int SlotOrder,
    string? Backend,
    FormationMemberControlState ControlState,
    bool IsAtTarget,
    string? Detail = null);

public sealed record FormationAssignmentSnapshot(
    string? TeamId,
    string? TeamName,
    string? FormationId,
    string? FormationName,
    FormationAssignmentState State,
    int RequiredSlotCount,
    int AssignedSlotCount,
    double? EntryHeadingDegrees,
    double? TeamLatitudeDegrees,
    double? TeamLongitudeDegrees,
    double? TeamAltitudeAglMetres,
    IReadOnlyList<FormationSlotAssignmentSnapshot> Assignments,
    IReadOnlyList<FormationLockFinding> Findings,
    string? InterruptionReason,
    DateTimeOffset CapturedAt)
{
    public static FormationAssignmentSnapshot Unassigned { get; } = new(
        null, null, null, null, FormationAssignmentState.Unassigned, 0, 0, null,
        null, null, null, [], [], null, DateTimeOffset.UtcNow);

    public bool HasAssignment => FormationId is not null && State != FormationAssignmentState.Unassigned;
    public bool IsActive => State is FormationAssignmentState.Transitioning or FormationAssignmentState.InFormation;
}

public sealed record FormationAssignmentRequest(string TeamId, string FormationId, double EntryHeadingDegrees);

public interface IFormationAssignmentWorkflow
{
    event EventHandler? Changed;
    FormationAssignmentSnapshot Current { get; }
    bool TryGet(string teamId, out FormationAssignmentSnapshot? assignment);

    Task<FormationAssignmentSnapshot> AssignAsync(
        FormationAssignmentRequest request,
        CancellationToken cancellationToken = default);

    Task<FormationAssignmentSnapshot> SwapSlotAsync(
        string teamId,
        string unitId,
        string slotId,
        CancellationToken cancellationToken = default);

    Task<FormationAssignmentSnapshot> ClearAsync(
        string teamId,
        CancellationToken cancellationToken = default);

    Task<ReviewedOperationSnapshot> PlanEnterAsync(
        string teamId,
        CancellationToken cancellationToken = default);

    Task<FormationAssignmentSnapshot> EnterAsync(
        string teamId,
        CancellationToken cancellationToken = default);
}
