using RobotCommand.Core;

namespace RobotCommand.Services.Workflows;

/// <summary>
/// Session-only bridge between an authored Formation document and a live
/// Team. It deliberately delegates all motion and safety behavior to the
/// existing FormationLockWorkflow.
/// </summary>
public sealed class FormationAssignmentWorkflow : IFormationAssignmentWorkflow, IDisposable
{
    private readonly object _gate = new();
    private readonly ITeamWorkflow _teams;
    private readonly IFormationAuthoringWorkflow _formations;
    private readonly IUnitObservationWorkflow _units;
    private readonly FormationLockWorkflow _lockWorkflow;
    private readonly ReviewedOperationWorkflow _reviewed;
    private readonly FormationAssignmentFreezeRegistry _freezeRegistry;
    private readonly Dictionary<string, StoredAssignment> _assignments = new(StringComparer.Ordinal);
    private FormationAssignmentSnapshot _current = FormationAssignmentSnapshot.Unassigned;
    private int _disposed;

    public FormationAssignmentWorkflow(
        ITeamWorkflow teams,
        IFormationAuthoringWorkflow formations,
        IUnitObservationWorkflow units,
        FormationLockWorkflow lockWorkflow,
        ReviewedOperationWorkflow reviewed,
        FormationAssignmentFreezeRegistry? freezeRegistry = null)
    {
        _teams = teams;
        _formations = formations;
        _units = units;
        _lockWorkflow = lockWorkflow;
        _reviewed = reviewed;
        _freezeRegistry = freezeRegistry ?? new FormationAssignmentFreezeRegistry();
        _teams.Changed += OnTeamsChanged;
        _formations.Changed += OnFormationsChanged;
        _lockWorkflow.Changed += OnLockChanged;
        PublishLocked();
    }

    public event EventHandler? Changed;
    public FormationAssignmentSnapshot Current { get { lock (_gate) return _current; } }

    public bool TryGet(string teamId, out FormationAssignmentSnapshot? assignment)
    {
        lock (_gate)
        {
            if (!_assignments.TryGetValue(teamId, out var storedAssignment))
            {
                assignment = null;
                return false;
            }
            assignment = BuildSnapshotLocked(storedAssignment);
            return true;
        }
    }

    public Task<FormationAssignmentSnapshot> AssignAsync(FormationAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(request.EntryHeadingDegrees) || request.EntryHeadingDegrees is < 0d or > 360d)
            throw new ArgumentOutOfRangeException(nameof(request), "Formation entry heading must be between 0 and 360 degrees.");
        if (!_teams.TryGet(request.TeamId, out var team) || team is null)
            throw new KeyNotFoundException($"Team '{request.TeamId}' was not found.");
        if (!_formations.TryGet(request.FormationId, out var formation) || formation is null)
            throw new KeyNotFoundException($"Formation '{request.FormationId}' was not found.");
        if (team.Members.Count != formation.Members.Count)
            throw new InvalidOperationException($"Team '{team.Name}' has {team.Members.Count} members but formation '{formation.Name}' requires {formation.Members.Count} slots.");

        lock (_gate)
        {
            if (_assignments.TryGetValue(request.TeamId, out var existing) && existing.State is FormationAssignmentState.Transitioning or FormationAssignmentState.InFormation)
                throw new InvalidOperationException("The Team already has an active authored formation. Unlock and clear it before assigning another formation.");

            var slots = formation.Members.ToArray();
            var members = team.Members.OrderBy(member => member.Order).ToArray();
            var assignment = new StoredAssignment
            {
                TeamId = team.Id,
                TeamName = team.Name,
                FormationId = formation.Id,
                FormationName = formation.Name,
                EntryHeadingDegrees = request.EntryHeadingDegrees,
                State = FormationAssignmentState.Assigned,
                Assignments = members.Select((member, index) => new SlotAssignment(member.UnitId, slots[index].Id)).ToDictionary(item => item.UnitId, StringComparer.Ordinal),
                MemberIds = members.Select(member => member.UnitId).ToHashSet(StringComparer.Ordinal)
            };
            _assignments[request.TeamId] = assignment;
            _freezeRegistry.Freeze(request.TeamId, assignment.MemberIds);
            _current = BuildSnapshotLocked(assignment);
        }
        RaiseChanged();
        return Task.FromResult(Current);
    }

    public Task<FormationAssignmentSnapshot> SwapSlotAsync(string teamId, string unitId, string slotId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var assignment = FindLocked(teamId);
            if (assignment.State is FormationAssignmentState.Transitioning or FormationAssignmentState.InFormation)
                throw new InvalidOperationException("Formation slots cannot be changed while the Team is transitioning or in formation.");
            if (!assignment.Assignments.TryGetValue(unitId, out var currentAssignment)) throw new KeyNotFoundException($"Unit '{unitId}' is not assigned to Team '{teamId}'.");
            var other = assignment.Assignments.Values.FirstOrDefault(item => item.SlotId == slotId);
            if (other is null) throw new KeyNotFoundException($"Formation slot '{slotId}' is not assigned in Team '{teamId}'.");
            var currentSlot = currentAssignment.SlotId;
            assignment.Assignments[unitId] = new SlotAssignment(unitId, slotId);
            assignment.Assignments[other.UnitId] = new SlotAssignment(other.UnitId, currentSlot);
            _current = BuildSnapshotLocked(assignment);
        }
        RaiseChanged();
        return Task.FromResult(Current);
    }

    public async Task<FormationAssignmentSnapshot> ClearAsync(string teamId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FormationAssignmentState state;
        lock (_gate) state = _assignments.TryGetValue(teamId, out var assignment) ? assignment.State : FormationAssignmentState.Unassigned;
        if (state is FormationAssignmentState.Transitioning or FormationAssignmentState.InFormation)
            await _lockWorkflow.UnlockAsync(teamId, "Authored formation assignment cleared by operator.", cancellationToken);
        lock (_gate)
        {
            _assignments.Remove(teamId);
            _freezeRegistry.Unfreeze(teamId);
            _current = _assignments.Values
                .OrderByDescending(item => item.UpdatedAt)
                .Select(BuildSnapshotLocked)
                .FirstOrDefault()
                ?? FormationAssignmentSnapshot.Unassigned;
        }
        RaiseChanged();
        return Current;
    }

    public Task<ReviewedOperationSnapshot> PlanEnterAsync(string teamId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var findings = ValidateEntry(teamId);
        return Task.FromResult(_reviewed.Plan(
            ReviewedOperationKind.TeamFormationEntry,
            "Enter authored formation",
            findings,
            [$"Enter authored formation for Team {teamId}"],
            "Capture the current Team layout, then transition all members to their assigned authored slots.",
            [teamId],
            async token =>
            {
                await EnterAsync(teamId, token);
                return new(string.Empty, ReviewedOperationState.Succeeded, true, "Authored formation entry accepted.", []);
            }));
    }

    public async Task<FormationAssignmentSnapshot> EnterAsync(string teamId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var findings = ValidateEntry(teamId);
        var blocking = findings.Where(item => item.Severity == WorkflowFindingSeverity.Blocking).ToArray();
        if (blocking.Length > 0) throw new InvalidOperationException(string.Join(" ", blocking.Select(item => item.Message)));

        StoredAssignment assignment;
        FormationWorkflowSnapshot formation;
        lock (_gate)
        {
            assignment = FindLocked(teamId);
            formation = _formations.TryGet(assignment.FormationId, out var selected) && selected is not null
                ? selected
                : throw new KeyNotFoundException("The assigned authored formation is no longer available.");
            assignment.State = FormationAssignmentState.Transitioning;
            assignment.InterruptionReason = null;
            _current = BuildSnapshotLocked(assignment);
        }
        RaiseChanged();

        try
        {
            var radians = assignment.EntryHeadingDegrees * Math.PI / 180d;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            var offsets = new Dictionary<string, (double North, double East, double Up)>(StringComparer.Ordinal);
            foreach (var slot in formation.Members)
            {
                var unit = assignment.Assignments.Values.First(item => item.SlotId == slot.Id).UnitId;
                var north = slot.NorthMetres * cosine - slot.EastMetres * sine;
                var east = slot.NorthMetres * sine + slot.EastMetres * cosine;
                offsets[unit] = (north, east, slot.UpMetres);
            }

            await _lockWorkflow.TransitionToLayoutAsync(teamId, offsets, cancellationToken);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var locked = _lockWorkflow.Current;
                if (locked.TeamId is null || !locked.IsLocked)
                    throw new InvalidOperationException(locked.InterruptionReason ?? "Formation transition was interrupted.");
                if (locked.IsConverged)
                {
                    lock (_gate)
                    {
                        assignment.State = FormationAssignmentState.InFormation;
                        assignment.TeamLatitudeDegrees = locked.TeamLatitudeDegrees;
                        assignment.TeamLongitudeDegrees = locked.TeamLongitudeDegrees;
                        assignment.TeamAltitudeAglMetres = locked.TeamAltitudeAglMetres;
                        _current = BuildSnapshotLocked(assignment);
                    }
                    RaiseChanged();
                    return Current;
                }
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException("Authored formation entry did not converge within 120 seconds.");
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                assignment.State = FormationAssignmentState.Interrupted;
                assignment.InterruptionReason = ex.Message;
                _current = BuildSnapshotLocked(assignment);
            }
            RaiseChanged();
            try { await _lockWorkflow.UnlockAsync(teamId, $"Authored formation entry interrupted: {ex.Message}", CancellationToken.None); } catch { }
            throw;
        }
    }

    private IReadOnlyList<WorkflowFinding> ValidateEntry(string teamId)
    {
        lock (_gate)
        {
            if (!_assignments.TryGetValue(teamId, out var assignment))
                return [new("FORMATION_ASSIGNMENT_MISSING", WorkflowFindingSeverity.Blocking, "Assign an authored formation before entering it.")];
            if (assignment.State is FormationAssignmentState.Transitioning or FormationAssignmentState.InFormation)
                return [new("FORMATION_ALREADY_ACTIVE", WorkflowFindingSeverity.Blocking, "The Team is already transitioning or in an authored formation.")];
        }
        if (!_teams.TryGet(teamId, out var team) || team is null)
            return [new("TEAM_NOT_FOUND", WorkflowFindingSeverity.Blocking, $"Team '{teamId}' was not found.")];
        if (!_formations.TryGet(_assignments[teamId].FormationId, out var formation) || formation is null)
            return [new("FORMATION_NOT_FOUND", WorkflowFindingSeverity.Blocking, "The assigned authored formation was not found.")];
        var assignmentData = _assignments[teamId];
        var findings = new List<WorkflowFinding>();
        if (team.Members.Count != formation.Members.Count)
            findings.Add(new("FORMATION_COUNT_MISMATCH", WorkflowFindingSeverity.Blocking, $"Team has {team.Members.Count} members but the formation requires {formation.Members.Count} slots."));
        if (assignmentData.Assignments.Count != formation.Members.Count || formation.Members.Any(slot => assignmentData.Assignments.Values.All(item => item.SlotId != slot.Id)))
            findings.Add(new("FORMATION_SLOTS_INCOMPLETE", WorkflowFindingSeverity.Blocking, "Every authored formation slot must be assigned exactly once."));
        if (_lockWorkflow.Current.IsLocked && !string.Equals(_lockWorkflow.Current.TeamId, teamId, StringComparison.Ordinal))
            findings.Add(new("FORMATION_OTHER_TEAM_LOCKED", WorkflowFindingSeverity.Blocking, "Another Team currently owns the formation controller."));
        foreach (var member in team.Members)
        {
            if (!_units.TryGet(member.UnitId, out var unit) || unit is null)
            {
                findings.Add(new("FORMATION_UNIT_MISSING", WorkflowFindingSeverity.Blocking, $"Unit '{member.UnitId}' is no longer available."));
                continue;
            }
            if (unit.State == ManagedConnectionState.Offline || unit.Telemetry is null || unit.Telemetry.IsStale)
                findings.Add(new("FORMATION_TELEMETRY_STALE", WorkflowFindingSeverity.Blocking, $"{unit.Name} does not have current telemetry."));
            else if (!unit.Telemetry.Armed || !FormationAirborneReadiness.IsConfirmedAirborne(unit.Telemetry))
                findings.Add(new("FORMATION_UNIT_NOT_AIRBORNE", WorkflowFindingSeverity.Blocking, $"{unit.Name} must be armed and airborne before entering a formation."));
        }
        return findings;
    }

    private StoredAssignment FindLocked(string teamId)
        => _assignments.TryGetValue(teamId, out var assignment)
            ? assignment
            : throw new KeyNotFoundException($"Team '{teamId}' has no authored formation assignment.");

    private FormationAssignmentSnapshot BuildSnapshotLocked(StoredAssignment assignment)
    {
        _formations.TryGet(assignment.FormationId, out var formation);
        _teams.TryGet(assignment.TeamId, out var team);
        var slots = formation?.Members ?? [];
        var locked = _lockWorkflow.Current;
        var members = assignment.Assignments.Values.Select(item =>
        {
            var unit = team?.Members.FirstOrDefault(member => member.UnitId == item.UnitId);
            var slotEntry = slots.Select((candidate, index) => (candidate, index)).FirstOrDefault(entry => entry.candidate.Id == item.SlotId);
            var slot = slotEntry.candidate;
            _units.TryGet(item.UnitId, out var observation);
            var lockedMember = locked.TeamId == assignment.TeamId
                ? locked.Members.FirstOrDefault(candidate => candidate.UnitId == item.UnitId)
                : null;
            return new FormationSlotAssignmentSnapshot(item.UnitId, observation?.Name ?? unit?.UnitName ?? item.UnitId,
                item.SlotId, slot?.Name ?? item.SlotId, slot is null ? -1 : slotEntry.index, observation?.ProfileKey,
                lockedMember?.ControlState ?? FormationMemberControlState.Pending,
                lockedMember?.IsAtTarget ?? false,
                lockedMember?.Detail);
        }).OrderBy(item => item.SlotOrder).ToArray();
        return new(assignment.TeamId, team?.Name ?? assignment.TeamName, assignment.FormationId, formation?.Name ?? assignment.FormationName,
            assignment.State, slots.Count, members.Length, assignment.EntryHeadingDegrees,
            locked.TeamId == assignment.TeamId ? locked.TeamLatitudeDegrees : assignment.TeamLatitudeDegrees,
            locked.TeamId == assignment.TeamId ? locked.TeamLongitudeDegrees : assignment.TeamLongitudeDegrees,
            locked.TeamId == assignment.TeamId ? locked.TeamAltitudeAglMetres : assignment.TeamAltitudeAglMetres,
            members, locked.TeamId == assignment.TeamId ? locked.Findings : [], assignment.InterruptionReason, DateTimeOffset.UtcNow);
    }

    private void PublishLocked()
    {
        lock (_gate)
            _current = _assignments.Values.OrderByDescending(item => item.UpdatedAt).Select(BuildSnapshotLocked).FirstOrDefault()
                ?? FormationAssignmentSnapshot.Unassigned;
    }

    private void OnTeamsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            foreach (var assignment in _assignments.Values)
            {
                if (!_teams.TryGet(assignment.TeamId, out var team) || team is null || !team.Members.Select(item => item.UnitId).ToHashSet(StringComparer.Ordinal).SetEquals(assignment.MemberIds))
                {
                    assignment.State = FormationAssignmentState.Interrupted;
                    assignment.InterruptionReason = "Team membership changed while an authored formation was assigned.";
                }
            }
            PublishLocked();
        }
        RaiseChanged();
    }

    private void OnFormationsChanged(object? sender, EventArgs e)
    {
        lock (_gate) PublishLocked();
        RaiseChanged();
    }

    private void OnLockChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            var locked = _lockWorkflow.Current;
            foreach (var assignment in _assignments.Values.ToArray())
            {
                var ownsLock = locked.IsLocked && string.Equals(locked.TeamId, assignment.TeamId, StringComparison.Ordinal);
                if (assignment.State == FormationAssignmentState.Transitioning && !ownsLock)
                {
                    assignment.State = FormationAssignmentState.Interrupted;
                    assignment.InterruptionReason = locked.InterruptionReason ?? "Formation transition was interrupted.";
                }
                else if (assignment.State == FormationAssignmentState.InFormation && !ownsLock)
                {
                    // Unlocking exits active formation control but keeps the
                    // authored slot assignment available for a later entry.
                    // Team membership remains frozen until the operator clears
                    // the assignment explicitly.
                    assignment.State = FormationAssignmentState.Assigned;
                    assignment.InterruptionReason = null;
                }
            }
            PublishLocked();
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _teams.Changed -= OnTeamsChanged;
        _formations.Changed -= OnFormationsChanged;
        _lockWorkflow.Changed -= OnLockChanged;
        foreach (var teamId in _assignments.Keys.ToArray()) _freezeRegistry.Unfreeze(teamId);
    }

    private sealed class StoredAssignment
    {
        public string TeamId { get; init; } = string.Empty;
        public string TeamName { get; init; } = string.Empty;
        public string FormationId { get; init; } = string.Empty;
        public string FormationName { get; init; } = string.Empty;
        public double EntryHeadingDegrees { get; init; }
        public FormationAssignmentState State { get; set; }
        public Dictionary<string, SlotAssignment> Assignments { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> MemberIds { get; init; } = new(StringComparer.Ordinal);
        public double? TeamLatitudeDegrees { get; set; }
        public double? TeamLongitudeDegrees { get; set; }
        public double? TeamAltitudeAglMetres { get; set; }
        public string? InterruptionReason { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed record SlotAssignment(string UnitId, string SlotId);
}
