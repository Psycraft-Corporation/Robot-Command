using RobotCommand.Core;

namespace RobotCommand.Services.Workflows;

/// <summary>Owns session-only local unit grouping, separate from Logos team observations.</summary>
public sealed class UnitTeamWorkflow : ITeamWorkflow
{
    private readonly object _gate = new();
    private readonly IUnitObservationWorkflow _units;
    private readonly FormationAssignmentFreezeRegistry? _freezeRegistry;
    private List<StoredTeam> _teams;
    private long _revision;
    private TeamWorkflowSnapshot _snapshot = null!;

    public UnitTeamWorkflow(string baseDirectory, IUnitObservationWorkflow units, FormationAssignmentFreezeRegistry? freezeRegistry = null)
    {
        // Kept in the constructor for runtime-host compatibility. Teams are
        // deliberately session-only and never read from or written to disk.
        _units = units;
        _freezeRegistry = freezeRegistry;
        _teams = [];
        _snapshot = BuildSnapshotLocked();
        _units.Changed += OnUnitsChanged;
    }

    public event EventHandler? Changed;

    public TeamWorkflowSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public bool TryGet(string teamId, out TeamSnapshot? team)
    {
        lock (_gate)
        {
            var stored = _teams.FirstOrDefault(item => string.Equals(item.Id, teamId, StringComparison.Ordinal));
            team = stored is null ? null : ToSnapshot(stored);
            return team is not null;
        }
    }

    public string? GetTeamIdForUnit(string unitId)
    {
        lock (_gate)
        {
            return _teams.FirstOrDefault(team => team.MemberIds.Contains(unitId, StringComparer.Ordinal))?.Id;
        }
    }

    public Task<TeamSnapshot> CreateAsync(
        IReadOnlyList<string> unitIds,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = ValidateUnitIds(unitIds);
        EnsureUnitsMutable(ids);
        ValidateCommandable(ids);
        TeamSnapshot snapshot;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var team = new StoredTeam
            {
                Id = $"unit-team-{Guid.NewGuid():N}",
                Name = string.IsNullOrWhiteSpace(name) ? NextNameLocked() : name.Trim(),
                MemberIds = ids.ToList(),
                CreatedAt = now,
                UpdatedAt = now
            };
            RemoveMembersFromAllLocked(ids);
            _teams.Add(team);
            PublishChangedLocked();
            snapshot = ToSnapshot(team);
        }
        RaiseChanged();
        return Task.FromResult(snapshot);
    }

    public Task<TeamSnapshot> AssignAsync(
        string teamId,
        IReadOnlyList<string> unitIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = ValidateUnitIds(unitIds);
        ValidateCommandable(ids);
        if (_freezeRegistry?.IsTeamFrozen(teamId) == true) throw new InvalidOperationException("Team membership is frozen while an authored formation is assigned.");
        EnsureUnitsMutable(ids);
        TeamSnapshot snapshot;
        lock (_gate)
        {
            var team = FindTeamLocked(teamId);
            RemoveMembersFromAllLocked(ids);
            team.MemberIds.AddRange(ids);
            team.MemberIds = team.MemberIds.Distinct(StringComparer.Ordinal).ToList();
            team.UpdatedAt = DateTimeOffset.UtcNow;
            PublishChangedLocked();
            snapshot = ToSnapshot(team);
        }
        RaiseChanged();
        return Task.FromResult(snapshot);
    }

    public Task ClearMembershipAsync(IReadOnlyList<string> unitIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = ValidateUnitIds(unitIds);
        EnsureUnitsMutable(ids);
        lock (_gate)
        {
            RemoveMembersFromAllLocked(ids);
            RemoveEmptyTeamsLocked();
            PublishChangedLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task ReorderMemberAsync(string teamId, string unitId, int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var team = FindTeamLocked(teamId);
            if (_freezeRegistry?.IsTeamFrozen(teamId) == true) throw new InvalidOperationException("Team membership is frozen while an authored formation is assigned.");
            var current = team.MemberIds.FindIndex(id => string.Equals(id, unitId, StringComparison.Ordinal));
            if (current < 0) throw new KeyNotFoundException($"Unit '{unitId}' is not a member of Team '{teamId}'.");
            team.MemberIds.RemoveAt(current);
            team.MemberIds.Insert(Math.Clamp(index, 0, team.MemberIds.Count), unitId);
            team.UpdatedAt = DateTimeOffset.UtcNow;
            PublishChangedLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string teamId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var team = FindTeamLocked(teamId);
            if (_freezeRegistry?.IsTeamFrozen(teamId) == true) throw new InvalidOperationException("Team membership is frozen while an authored formation is assigned.");
            if (team.MemberIds.Count > 0)
                throw new InvalidOperationException("A Team must be empty before it can be deleted.");
            _teams.Remove(team);
            PublishChangedLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task RemoveUnitAsync(string unitId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(unitId)) return Task.CompletedTask;
        lock (_gate)
        {
            RemoveMembersFromAllLocked([unitId]);
            RemoveEmptyTeamsLocked();
            PublishChangedLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    private void OnUnitsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            PublishChangedLocked();
        }
        RaiseChanged();
    }

    private TeamWorkflowSnapshot BuildSnapshotLocked()
    {
        var teams = _teams.Select(ToSnapshot).ToArray();
        var membership = teams
            .SelectMany(team => team.Members.Select(member => new { member.UnitId, team.Id }))
            .ToDictionary(item => item.UnitId, item => item.Id, StringComparer.Ordinal);
        return new TeamWorkflowSnapshot(_revision, teams, membership, DateTimeOffset.UtcNow);
    }

    private TeamSnapshot ToSnapshot(StoredTeam team)
    {
        var current = _units.Units.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var members = team.MemberIds.Select((unitId, index) =>
        {
            current.TryGetValue(unitId, out var unit);
            return new TeamMemberSnapshot(unitId, index, unit is { State: not ManagedConnectionState.Offline }, unit?.Name, unit?.ProfileKey);
        }).ToArray();
        return new TeamSnapshot(team.Id, team.Name, members, team.CreatedAt, team.UpdatedAt);
    }

    private StoredTeam FindTeamLocked(string teamId)
        => _teams.FirstOrDefault(item => string.Equals(item.Id, teamId, StringComparison.Ordinal))
           ?? throw new KeyNotFoundException($"Team '{teamId}' was not found.");

    private string NextNameLocked()
    {
        for (var index = 1; ; index++)
        {
            var candidate = $"Team {index}";
            if (_teams.All(team => !string.Equals(team.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private List<string> ValidateUnitIds(IReadOnlyList<string> unitIds)
    {
        if (unitIds is null || unitIds.Count == 0) throw new ArgumentException("At least one unit is required.", nameof(unitIds));
        var ids = unitIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count != unitIds.Count || ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("Unit IDs must be non-empty and unique.", nameof(unitIds));
        var known = _units.Units.Select(unit => unit.Id).ToHashSet(StringComparer.Ordinal);
        var missing = ids.FirstOrDefault(id => !known.Contains(id));
        if (missing is not null) throw new KeyNotFoundException($"Unit '{missing}' was not found.");
        return ids;
    }

    private void ValidateCommandable(IEnumerable<string> unitIds)
    {
        var byId = _units.Units.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var id in unitIds)
        {
            if (!byId.TryGetValue(id, out var unit)) throw new KeyNotFoundException($"Unit '{id}' was not found.");
            if (!unit.CanAcceptOperatorCommands) throw new InvalidOperationException($"Unit '{unit.Name}' is read-only and cannot join a local Team.");
        }
    }

    private void EnsureUnitsMutable(IEnumerable<string> unitIds)
    {
        var frozen = unitIds.FirstOrDefault(id => _freezeRegistry?.IsUnitFrozen(id) == true);
        if (frozen is not null) throw new InvalidOperationException($"Unit '{frozen}' belongs to a Team with an assigned authored formation.");
    }

    private void RemoveMembersFromAllLocked(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var team in _teams)
        {
            team.MemberIds.RemoveAll(set.Contains);
            team.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private void RemoveEmptyTeamsLocked() => _teams.RemoveAll(team => team.MemberIds.Count == 0);

    private void PublishChangedLocked()
    {
        _snapshot = BuildSnapshotLocked() with { Revision = ++_revision };
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class StoredTeam
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> MemberIds { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
