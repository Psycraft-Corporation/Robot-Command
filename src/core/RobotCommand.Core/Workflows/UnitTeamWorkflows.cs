namespace RobotCommand.Core;

/// <summary>A session-only local grouping of commandable Robot Command units.</summary>
public sealed record TeamMemberSnapshot(
    string UnitId,
    int Order,
    bool IsOnline,
    string? UnitName,
    string? Backend);

public sealed record TeamSnapshot(
    string Id,
    string Name,
    IReadOnlyList<TeamMemberSnapshot> Members,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TeamWorkflowSnapshot(
    long Revision,
    IReadOnlyList<TeamSnapshot> Teams,
    IReadOnlyDictionary<string, string> TeamByUnitId,
    DateTimeOffset CapturedAt);

public interface ITeamWorkflow
{
    event EventHandler? Changed;
    TeamWorkflowSnapshot Current { get; }
    bool TryGet(string teamId, out TeamSnapshot? team);
    string? GetTeamIdForUnit(string unitId);
    Task<TeamSnapshot> CreateAsync(
        IReadOnlyList<string> unitIds,
        string? name = null,
        CancellationToken cancellationToken = default);
    Task<TeamSnapshot> AssignAsync(
        string teamId,
        IReadOnlyList<string> unitIds,
        CancellationToken cancellationToken = default);
    Task ClearMembershipAsync(
        IReadOnlyList<string> unitIds,
        CancellationToken cancellationToken = default);
    Task ReorderMemberAsync(
        string teamId,
        string unitId,
        int index,
        CancellationToken cancellationToken = default);
    Task RemoveUnitAsync(string unitId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string teamId, CancellationToken cancellationToken = default);
}
