namespace RobotCommand.Core;

/// <summary>Session-only selection of one local Team, independent of unit selection.</summary>
public sealed record TeamSelectionWorkflowSnapshot(
    string? TeamId,
    string? TeamName,
    IReadOnlyList<string> MemberUnitIds,
    long Revision,
    DateTimeOffset CapturedAt)
{
    public static TeamSelectionWorkflowSnapshot Empty { get; } =
        new(null, null, [], 0, DateTimeOffset.UtcNow);

    public bool IsSelected => !string.IsNullOrWhiteSpace(TeamId);
}

public interface ITeamSelectionWorkflow
{
    event EventHandler? Changed;
    TeamSelectionWorkflowSnapshot Current { get; }
    Task SelectAsync(string teamId, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public enum OperatorTargetScopeKind
{
    None,
    UnitSelection,
    Team
}

/// <summary>Resolved command target scope. Team commands still expand to unit plans at execution.</summary>
public sealed record OperatorTargetScopeSnapshot(
    OperatorTargetScopeKind Kind,
    string? TargetId,
    string? TargetName,
    IReadOnlyList<string> UnitIds,
    int TargetCount)
{
    public static OperatorTargetScopeSnapshot Empty { get; } =
        new(OperatorTargetScopeKind.None, null, null, [], 0);
}

public interface IOperatorTargetScopeWorkflow
{
    event EventHandler? Changed;
    OperatorTargetScopeSnapshot Current { get; }
    Task<OperatorTargetScopeSnapshot> ResolveTeamAsync(string teamId, CancellationToken cancellationToken = default);
}
