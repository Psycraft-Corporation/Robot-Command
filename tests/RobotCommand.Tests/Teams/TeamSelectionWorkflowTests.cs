using RobotCommand.Core;
using RobotCommand.Services.Workflows;
using Xunit;

namespace RobotCommand.Tests;

public sealed class TeamSelectionWorkflowTests
{
    [Fact]
    public async Task TargetScope_ResolvesTeamMembersInStoredOrder()
    {
        var team = new TeamSnapshot(
            "team-1", "Team 1",
            [new TeamMemberSnapshot("ghost-2", 0, true, "Ghost 2", "Ghost"),
             new TeamMemberSnapshot("ghost-1", 1, false, "Ghost 1", "Ghost")],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var selection = new FakeSelectionWorkflow(["single"]);
        var teamSelection = new FakeTeamSelectionWorkflow(new("team-1", "Team 1", ["ghost-2", "ghost-1"], 1, DateTimeOffset.UtcNow));
        var scope = new OperatorTargetScopeWorkflow(selection, teamSelection, new FakeTeamWorkflow(team));

        var current = scope.Current;

        Assert.Equal(OperatorTargetScopeKind.Team, current.Kind);
        Assert.Equal("team-1", current.TargetId);
        Assert.Equal(["ghost-2", "ghost-1"], current.UnitIds);
        Assert.Equal(2, current.TargetCount);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => scope.ResolveTeamAsync("missing"));
    }

    [Fact]
    public void EmptyTeamSelection_UsesUnitSelectionScope()
    {
        var selection = new FakeSelectionWorkflow(["ghost-1", "ghost-2"]);
        var scope = new OperatorTargetScopeWorkflow(selection, new FakeTeamSelectionWorkflow(TeamSelectionWorkflowSnapshot.Empty), new FakeTeamWorkflow());

        Assert.Equal(OperatorTargetScopeKind.UnitSelection, scope.Current.Kind);
        Assert.Equal(["ghost-1", "ghost-2"], scope.Current.UnitIds);
    }

    private sealed class FakeSelectionWorkflow(IReadOnlyList<string> ids) : ISelectionWorkflow
    {
        public event EventHandler? Changed;
        public SelectionWorkflowSnapshot Current { get; } = new(ids.FirstOrDefault(), ids, ids.FirstOrDefault());
        public Task SetUnitsAsync(IReadOnlyList<string> unitIds, string? anchorUnitId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTeamSelectionWorkflow(TeamSelectionWorkflowSnapshot snapshot) : ITeamSelectionWorkflow
    {
        public event EventHandler? Changed;
        public TeamSelectionWorkflowSnapshot Current { get; } = snapshot;
        public Task SelectAsync(string teamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTeamWorkflow(params TeamSnapshot[] teams) : ITeamWorkflow
    {
        public event EventHandler? Changed;
        public TeamWorkflowSnapshot Current => new(0, teams, teams.SelectMany(team => team.Members.Select(member => new { member.UnitId, team.Id })).ToDictionary(item => item.UnitId, item => item.Id), DateTimeOffset.UtcNow);
        public bool TryGet(string teamId, out TeamSnapshot? team) { team = teams.FirstOrDefault(item => item.Id == teamId); return team is not null; }
        public string? GetTeamIdForUnit(string unitId) => teams.FirstOrDefault(team => team.Members.Any(member => member.UnitId == unitId))?.Id;
        public Task<TeamSnapshot> CreateAsync(IReadOnlyList<string> unitIds, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamSnapshot> AssignAsync(string teamId, IReadOnlyList<string> unitIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearMembershipAsync(IReadOnlyList<string> unitIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReorderMemberAsync(string teamId, string unitId, int index, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUnitAsync(string unitId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
