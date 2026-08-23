using RobotCommand.Core;
using RobotCommand.Services.Workflows;
using Xunit;

namespace RobotCommand.Tests;

public sealed class UnitTeamWorkflowTests
{
    [Fact]
    public async Task CreateAndAssign_EnforcesOneTeamPerUnitAndPreservesOrder()
    {
        var root = NewRoot();
        try
        {
            var units = new FakeUnitObservationWorkflow(Unit("ghost-1"), Unit("ghost-2"), Unit("ghost-3"));
            var workflow = new UnitTeamWorkflow(root, units);
            var first = await workflow.CreateAsync(["ghost-1", "ghost-2"]);
            var second = await workflow.CreateAsync(["ghost-3"]);

            await workflow.AssignAsync(second.Id, ["ghost-2"]);

            Assert.Equal(["ghost-1"], workflow.Current.Teams.Single(team => team.Id == first.Id).Members.Select(member => member.UnitId));
            Assert.Equal(["ghost-3", "ghost-2"], workflow.Current.Teams.Single(team => team.Id == second.Id).Members.Select(member => member.UnitId));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ClearMembership_RemovesEmptyTeams_AndReorderUpdatesPersistence()
    {
        var root = NewRoot();
        try
        {
            var units = new FakeUnitObservationWorkflow(Unit("ghost-1"), Unit("ghost-2"));
            var workflow = new UnitTeamWorkflow(root, units);
            var team = await workflow.CreateAsync(["ghost-1", "ghost-2"]);

            await workflow.ReorderMemberAsync(team.Id, "ghost-1", 1);
            Assert.Equal(["ghost-2", "ghost-1"], workflow.Current.Teams.Single().Members.Select(member => member.UnitId));

            await workflow.ClearMembershipAsync(["ghost-2", "ghost-1"]);
            Assert.Empty(workflow.Current.Teams);

            var reloaded = new UnitTeamWorkflow(root, units);
            Assert.Empty(reloaded.Current.Teams);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ReadOnlyUnits_AreRejected()
    {
        var root = NewRoot();
        try
        {
            var units = new FakeUnitObservationWorkflow(Unit("remote-1", canCommand: false));
            var workflow = new UnitTeamWorkflow(root, units);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.CreateAsync(["remote-1"]));

            Assert.Contains("read-only", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteRoot(root); }
    }

    private static UnitObservationSnapshot Unit(string id, bool canCommand = true)
        => new(id, id, [], null, null, "Multicopter", "Air", "Ghost", ManagedConnectionState.Online,
            "Ready", "Ready", "Disarmed", "Ready", [], DateTimeOffset.UtcNow, true,
            id, id, id, null, null, [], new UnitActionObservation(null, null),
            CanAcceptOperatorCommands: canCommand);

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"robotcommand-teams-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FakeUnitObservationWorkflow(params UnitObservationSnapshot[] units) : IUnitObservationWorkflow
    {
        public event EventHandler? Changed;
        public IReadOnlyList<UnitObservationSnapshot> Units { get; } = units;
        public bool TryGet(string unitId, out UnitObservationSnapshot? unit)
        {
            unit = Units.FirstOrDefault(item => item.Id == unitId);
            return unit is not null;
        }

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public async Task GeneratedNamesAreUnique_AndDeletingAUnitRemovesOnlyItsMembership()
    {
        var root = NewRoot();
        try
        {
            var units = new FakeUnitObservationWorkflow(Unit("ghost-1"), Unit("ghost-2"), Unit("ghost-3"));
            var workflow = new UnitTeamWorkflow(root, units);
            var first = await workflow.CreateAsync(["ghost-1"]);
            var second = await workflow.CreateAsync(["ghost-2", "ghost-3"]);

            Assert.Equal("Team 1", first.Name);
            Assert.Equal("Team 2", second.Name);

            await workflow.RemoveUnitAsync("ghost-2");

            var remaining = workflow.Current.Teams.Single(team => team.Id == second.Id);
            Assert.Equal(["ghost-3"], remaining.Members.Select(member => member.UnitId));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void Reload_StartsWithNoTeamsBecauseTeamsAreSessionOnly()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllText(
                Path.Combine(root, "data", "unit-teams.json"),
                """
                {
                  "version": 1,
                  "teams": [
                    {
                      "id": "team-1",
                      "name": "Team 1",
                      "memberIds": ["ghost-1", "vehicle-1"]
                    }
                  ]
                }
                """);

            var workflow = new UnitTeamWorkflow(
                root,
                new FakeUnitObservationWorkflow(Unit("vehicle-1") with { IsGhost = false }));

            Assert.Empty(workflow.Current.Teams);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ChangedNotification_CanReenterWorkflowWithoutDeadlocking()
    {
        var root = NewRoot();
        try
        {
            var units = new FakeUnitObservationWorkflow(Unit("ghost-1"));
            var workflow = new UnitTeamWorkflow(root, units);
            string? teamId = null;
            workflow.Changed += (_, _) =>
            {
                if (teamId is not null)
                    Assert.True(workflow.TryGet(teamId, out _));
            };

            var createTask = workflow.CreateAsync(["ghost-1"]);
            var completed = await Task.WhenAny(createTask, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(createTask, completed);
            teamId = (await createTask).Id;

            var updateTask = workflow.AssignAsync(teamId, ["ghost-1"]);
            completed = await Task.WhenAny(updateTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(updateTask, completed);
        }
        finally { DeleteRoot(root); }
    }

}
