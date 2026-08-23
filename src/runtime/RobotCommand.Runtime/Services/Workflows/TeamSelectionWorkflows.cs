using RobotCommand.Core;
using RobotCommand.State;

namespace RobotCommand.Services.Workflows;

internal interface ITeamSelectionState
{
    string? TeamId { get; }
    event EventHandler? Changed;
    void Set(string? teamId);
}

internal sealed class TeamSelectionState : ITeamSelectionState
{
    public string? TeamId { get; private set; }
    public event EventHandler? Changed;

    public void Set(string? teamId)
    {
        if (string.Equals(TeamId, teamId, StringComparison.Ordinal)) return;
        TeamId = string.IsNullOrWhiteSpace(teamId) ? null : teamId;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Coordinates the two independent stores so only one active selection scope remains.</summary>
internal sealed class SelectionScopeCoordinator
{
    private readonly ISelectionService _units;
    private readonly ITeamSelectionState _team;

    public SelectionScopeCoordinator(ISelectionService units, ITeamSelectionState team)
    {
        _units = units;
        _team = team;
        _units.Changed += (_, _) => _team.Set(null);
    }
}

internal sealed class TeamSelectionWorkflow : ITeamSelectionWorkflow
{
    private readonly object _gate = new();
    private readonly ITeamSelectionState _state;
    private readonly ITeamWorkflow _teams;
    private readonly ISelectionWorkflow _unitSelection;
    private long _revision;
    private SelectionSignature _published = SelectionSignature.Empty;

    public TeamSelectionWorkflow(ITeamSelectionState state, ITeamWorkflow teams, ISelectionWorkflow unitSelection, SelectionScopeCoordinator _coordinator)
    {
        _state = state;
        _teams = teams;
        _unitSelection = unitSelection;
        _state.Changed += (_, _) => PublishChanged();
        _teams.Changed += (_, _) =>
        {
            if (_state.TeamId is { } id && !_teams.TryGet(id, out _)) _state.Set(null);
            PublishChanged();
        };
    }

    public event EventHandler? Changed;

    public TeamSelectionWorkflowSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                if (_state.TeamId is not { } id || !_teams.TryGet(id, out var team) || team is null)
                    return new(null, null, [], _revision, DateTimeOffset.UtcNow);
                return new(team.Id, team.Name, team.Members.OrderBy(member => member.Order).Select(member => member.UnitId).ToArray(), _revision, DateTimeOffset.UtcNow);
            }
        }
    }

    public async Task SelectAsync(string teamId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_teams.TryGet(teamId, out var team) || team is null)
            throw new KeyNotFoundException($"Team '{teamId}' was not found.");
        await _unitSelection.ClearAsync(cancellationToken);
        _state.Set(team.Id);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state.Set(null);
        return Task.CompletedTask;
    }

    private void PublishChanged()
    {
        var current = ReadSignature();
        lock (_gate)
        {
            // Team membership workflows refresh as telemetry changes. A Team
            // selection is not changed by that refresh, so do not fan out
            // redundant selection events to GUI, CLI, and observers.
            if (_published.Equals(current)) return;
            _published = current;
            _revision++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private SelectionSignature ReadSignature()
    {
        if (_state.TeamId is not { } id || !_teams.TryGet(id, out var team) || team is null)
            return SelectionSignature.Empty;
        return new(team.Id, team.Name, string.Join("\u001F", team.Members.OrderBy(member => member.Order).Select(member => member.UnitId)));
    }

    private sealed record SelectionSignature(string? TeamId, string? TeamName, string Members)
    {
        public static SelectionSignature Empty { get; } = new(null, null, string.Empty);
    }
}

public sealed class OperatorTargetScopeWorkflow : IOperatorTargetScopeWorkflow
{
    private readonly ISelectionWorkflow _units;
    private readonly ITeamSelectionWorkflow _teamSelection;
    private readonly ITeamWorkflow _teams;

    public OperatorTargetScopeWorkflow(ISelectionWorkflow units, ITeamSelectionWorkflow teamSelection, ITeamWorkflow teams)
    {
        _units = units;
        _teamSelection = teamSelection;
        _teams = teams;
        _units.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _teamSelection.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _teams.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public OperatorTargetScopeSnapshot Current
    {
        get
        {
            var teamSelection = _teamSelection.Current;
            if (teamSelection.IsSelected)
                return new(OperatorTargetScopeKind.Team, teamSelection.TeamId, teamSelection.TeamName, teamSelection.MemberUnitIds.ToArray(), teamSelection.MemberUnitIds.Count);
            var unitIds = _units.Current.UnitIds.ToArray();
            return new(unitIds.Length > 0 ? OperatorTargetScopeKind.UnitSelection : OperatorTargetScopeKind.None,
                _units.Current.PrimaryUnitId, null, unitIds, unitIds.Length);
        }
    }

    public Task<OperatorTargetScopeSnapshot> ResolveTeamAsync(string teamId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_teams.TryGet(teamId, out var team) || team is null)
            throw new KeyNotFoundException($"Team '{teamId}' was not found.");
        var ids = team.Members.OrderBy(member => member.Order).Select(member => member.UnitId).ToArray();
        return Task.FromResult(new OperatorTargetScopeSnapshot(OperatorTargetScopeKind.Team, team.Id, team.Name, ids, ids.Length));
    }
}
