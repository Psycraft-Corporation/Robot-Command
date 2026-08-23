namespace RobotCommand.Services.Workflows;

/// <summary>Internal session guard used to freeze Team composition during authored entry.</summary>
public sealed class FormationAssignmentFreezeRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _teams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _units = new(StringComparer.Ordinal);

    public bool IsTeamFrozen(string teamId)
    {
        lock (_gate) return _teams.Contains(teamId);
    }

    public bool IsUnitFrozen(string unitId)
    {
        lock (_gate) return _units.ContainsKey(unitId);
    }

    public void Freeze(string teamId, IEnumerable<string> unitIds)
    {
        lock (_gate)
        {
            _teams.Add(teamId);
            foreach (var unitId in unitIds) _units[unitId] = teamId;
        }
    }

    public void Unfreeze(string teamId)
    {
        lock (_gate)
        {
            _teams.Remove(teamId);
            foreach (var unitId in _units.Where(item => item.Value == teamId).Select(item => item.Key).ToArray()) _units.Remove(unitId);
        }
    }
}
