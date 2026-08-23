using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Workflows;
using RobotCommand.State;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests.ViewModels;

public sealed class GhostUnitsPanelTests
{
    [Fact]
    public void GhostRuntimeIsMarkedAsGhostInUnitsList()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var viewModel = new UnitsPanelViewModel(runtimes, connections, new SelectionService());

        runtimes.Upsert(new RuntimeRecord(
            "ghost-runtime-1", "Ghost 1", ["ghost-connection-1"], AvailabilityState.Online,
            "Ghost", "Simulated", "multicopter", "ghost", "in-app", "Healthy", "Ready", [],
            VehicleId: "ghost-1", VehicleName: "Ghost 1", IsGhost: true));

        var item = Assert.Single(viewModel.Units);
        Assert.True(item.IsGhost);
        Assert.Equal("ghost-1", item.Record.VehicleId);
    }

    [Fact]
    public void OpeningGhostCreationEnablesConfirmCommand()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var viewModel = new UnitsPanelViewModel(runtimes, connections, new SelectionService());

        Assert.False(viewModel.ConfirmCreateGhostCommand.CanExecute(null));
        viewModel.CreateGhostCommand.Execute(null);

        Assert.True(viewModel.ConfirmCreateGhostCommand.CanExecute(null));
    }

    [Fact]
    public void GhostCountDefaultsToOneAndIsClampedToSafeBounds()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var viewModel = new UnitsPanelViewModel(runtimes, connections, new SelectionService());

        Assert.Equal(1, viewModel.GhostCount);
        viewModel.GhostCount = 0;
        Assert.Equal(1, viewModel.GhostCount);
        viewModel.GhostCount = UnitsPanelViewModel.MaxGhostCount + 1;
        Assert.Equal(UnitsPanelViewModel.MaxGhostCount, viewModel.GhostCount);
    }

    [Fact]
    public void MapVehicleSelectionIsNotReplacedByStaleUnitsSelectionOnTelemetryRefresh()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(runtimes, connections, selection, vehicles);
        var first = new RuntimeRecord("ghost-runtime-1", "Ghost 1", ["ghost-connection-1"], AvailabilityState.Online,
            "Ghost", "Simulated", "multicopter", "ghost", "in-app", "Healthy", "Ready", [],
            VehicleId: "ghost-1", VehicleName: "Ghost 1", IsGhost: true);
        var second = first with { Id = "ghost-runtime-2", Name = "Ghost 2", ConnectionIds = ["ghost-connection-2"], VehicleId = "ghost-2", VehicleName = "Ghost 2" };
        runtimes.ReplaceAll([first, second]);
        vehicles.Upsert(new VehicleRecord("ghost-1", "Ghost 1", ["ghost-connection-1"], "ghost-runtime-1", null, "Multicopter", "Air", "ghost", AvailabilityState.Online, "Ready", "Landed", "Disarmed", "Healthy", [], DateTimeOffset.UtcNow, true));
        vehicles.Upsert(new VehicleRecord("ghost-2", "Ghost 2", ["ghost-connection-2"], "ghost-runtime-2", null, "Multicopter", "Air", "ghost", AvailabilityState.Online, "Ready", "Landed", "Disarmed", "Healthy", [], DateTimeOffset.UtcNow, true));

        viewModel.SelectedUnitItem = viewModel.Units.Single(item => item.Id == first.Id);
        selection.Select(SelectionFactory.From(vehicles.Items.Single(item => item.Id == second.VehicleId)));
        runtimes.ReplaceAll([first with { LastSeen = DateTimeOffset.UtcNow }, second with { LastSeen = DateTimeOffset.UtcNow }]);

        Assert.Equal(second.Id, viewModel.SelectedUnit?.Id);
        Assert.Equal(second.VehicleId, selection.Current.Id);
    }

    [Fact]
    public void UnitOrderCanBeRearrangedWithoutChangingUnitIdentity()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var viewModel = new UnitsPanelViewModel(runtimes, connections, new SelectionService());
        runtimes.ReplaceAll([
            new RuntimeRecord("runtime-1", "Unit 1", [], AvailabilityState.Online, "Role", "Mode", "platform", "profile", "version", "Healthy", "Ready", []),
            new RuntimeRecord("runtime-2", "Unit 2", [], AvailabilityState.Online, "Role", "Mode", "platform", "profile", "version", "Healthy", "Ready", [])]);

        viewModel.ReorderUnit(viewModel.Units[0], 1);

        Assert.Equal(["runtime-2", "runtime-1"], viewModel.Units.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void ReorderingSelectedUnitPreservesSelection()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var viewModel = new UnitsPanelViewModel(runtimes, connections, new SelectionService());
        runtimes.ReplaceAll([
            new RuntimeRecord("runtime-1", "Unit 1", [], AvailabilityState.Online, "Role", "Mode", "platform", "profile", "version", "Healthy", "Ready", []),
            new RuntimeRecord("runtime-2", "Unit 2", [], AvailabilityState.Online, "Role", "Mode", "platform", "profile", "version", "Healthy", "Ready", [])]);

        viewModel.SelectedUnitItem = viewModel.Units[0];
        viewModel.ReorderUnit(viewModel.Units[0], 1);

        Assert.Equal("runtime-1", viewModel.SelectedUnit?.Id);
        Assert.Equal("runtime-1", viewModel.SelectedUnitItem?.Id);
    }

    [Fact]
    public void UnitSelectionSupportsCtrlToggleAndShiftRange()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(runtimes, connections, selection);
        var records = Enumerable.Range(1, 4)
            .Select(number => new RuntimeRecord(
                $"runtime-{number}", $"Unit {number}", [], AvailabilityState.Online,
                "Role", "Mode", "platform", "profile", "version", "Healthy", "Ready", [],
                VehicleId: $"vehicle-{number}", VehicleName: $"Unit {number}"))
            .ToArray();
        runtimes.ReplaceAll(records);

        viewModel.SelectUnit(viewModel.Units[0], false, false);
        viewModel.SelectUnit(viewModel.Units[2], true, false);
        Assert.Equal(["vehicle-1", "vehicle-3"], selection.SelectedUnitIds);

        viewModel.SelectUnit(viewModel.Units[3], false, true);
        Assert.Equal(["vehicle-1", "vehicle-2", "vehicle-3", "vehicle-4"], selection.SelectedUnitIds);

        viewModel.SelectUnit(viewModel.Units[1], false, true);
        Assert.Equal(["vehicle-1", "vehicle-2"], selection.SelectedUnitIds);

        viewModel.SelectUnit(viewModel.Units[0], true, false);
        Assert.Equal(["vehicle-2"], selection.SelectedUnitIds);

        viewModel.SelectUnit(viewModel.Units[2], false, false);
        viewModel.SelectUnit(viewModel.Units[0], false, true);
        Assert.Equal(["vehicle-1", "vehicle-2", "vehicle-3"], selection.SelectedUnitIds);
    }

    [Fact]
    public void NineSelectedGhostRowsKeepTheirVisualItemsDuringLiveRefresh()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(runtimes, connections, selection);
        var records = Enumerable.Range(1, 9)
            .Select(number => new RuntimeRecord(
                $"ghost-runtime-{number}", $"Ghost {number}", [$"ghost-connection-{number}"],
                AvailabilityState.Online, "Ghost", "Simulated", "multicopter", "ghost", "in-app",
                "Healthy", "Ready", [], IsGhost: true))
            .ToArray();

        runtimes.ReplaceAll(records);
        viewModel.SelectUnit(viewModel.Units[0], extend: false, range: false);
        foreach (var row in viewModel.Units.Skip(1))
            viewModel.SelectUnit(row, extend: true, range: false);

        var rows = viewModel.Units.ToArray();
        var indicators = rows.Select(row => row.ListStatusIndicators).ToArray();
        var selectionNotifications = 0;
        viewModel.UnitSelectionChanged += (_, _) => selectionNotifications++;

        runtimes.ReplaceAll(records.Select(record => record with { LastSeen = DateTimeOffset.UtcNow }).ToArray());

        Assert.Equal(9, selection.SelectedUnitIds.Count);
        Assert.Equal(0, selectionNotifications);
        Assert.True(rows.SequenceEqual(viewModel.Units));
        for (var index = 0; index < rows.Length; index++)
            Assert.Same(indicators[index], viewModel.Units[index].ListStatusIndicators);
    }

    [Fact]
    public async Task TeamMembershipUsesCanonicalVehicleIdForRuntimeRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robotcommand-team-ui-{Guid.NewGuid():N}");
        try
        {
            var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
            var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
            var observations = new TestUnitObservationWorkflow(
                new UnitObservationSnapshot("ghost-1", "Ghost 1", [], null, null, "Multicopter", "Air", "Ghost", ManagedConnectionState.Online,
                    "Ready", "Ready", "Disarmed", "Ready", [], DateTimeOffset.UtcNow, true,
                    "ghost-1", "ghost-1", "ghost-1", null, null, [], new UnitActionObservation(null, null)));
            var teams = new UnitTeamWorkflow(root, observations);
            var viewModel = new UnitsPanelViewModel(runtimes, connections, new SelectionService(), teams: teams);

            runtimes.Upsert(new RuntimeRecord(
                "ghost-runtime-1", "Ghost 1", ["ghost-connection-1"], AvailabilityState.Online,
                "Ghost", "Simulated", "multicopter", "ghost", "in-app", "Healthy", "Ready", [],
                VehicleId: "ghost-1", VehicleName: "Ghost 1", IsGhost: true));

            await teams.CreateAsync(["ghost-1"]);

            var row = Assert.Single(viewModel.Units);
            Assert.True(row.IsInTeam);
            Assert.Equal("Team 1", row.TeamName);
            Assert.True(row.IsTeamFirst && row.IsTeamLast);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreatingAGhostBatchSelectsEveryNewGhost()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var workflow = new TestGhostUnitWorkflow(runtimes, vehicles);
        var viewModel = new UnitsPanelViewModel(
            runtimes,
            connections,
            selection,
            vehicleStore: vehicles,
            ghostWorkflow: workflow);

        viewModel.GhostCount = 3;
        viewModel.CreateGhostCommand.Execute(null);
        viewModel.ConfirmCreateGhostCommand.Execute(null);

        await EventuallyAsync(() => selection.SelectedUnitIds.Count == 3);

        Assert.Equal(["ghost-1", "ghost-2", "ghost-3"], selection.SelectedUnitIds);
        Assert.Equal("ghost-1", selection.UnitSelectionAnchorId);
    }

    [Fact]
    public async Task TeamLayoutWaitsUntilASecondUnitHasFinishedBeingAdded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robotcommand-team-reentrancy-{Guid.NewGuid():N}");
        try
        {
            var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
            var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
            var firstObservation = new UnitObservationSnapshot(
                "ghost-1", "Ghost 1", [], null, null, "Multicopter", "Air", "Ghost", ManagedConnectionState.Online,
                "Ready", "Ready", "Disarmed", "Ready", [], DateTimeOffset.UtcNow, true,
                "ghost-1", "ghost-1", "ghost-1", null, null, [], new UnitActionObservation(null, null));
            var observations = new TestUnitObservationWorkflow(
                firstObservation,
                firstObservation with { Id = "ghost-2", Name = "Ghost 2", CommandAuthorityVehicleId = "ghost-2", TelemetryAuthorityVehicleId = "ghost-2", DiagnosticsAuthorityVehicleId = "ghost-2" });
            var teams = new UnitTeamWorkflow(root, observations);
            var viewModel = new UnitsPanelViewModel(runtimes, connections, new SelectionService(), teams: teams);
            var first = new RuntimeRecord(
                "ghost-runtime-1", "Ghost 1", ["ghost-connection-1"], AvailabilityState.Online,
                "Ghost", "Simulated", "multicopter", "ghost", "in-app", "Healthy", "Ready", [],
                VehicleId: "ghost-1", VehicleName: "Ghost 1", IsGhost: true);
            var second = first with
            {
                Id = "ghost-runtime-2",
                Name = "Ghost 2",
                ConnectionIds = ["ghost-connection-2"],
                VehicleId = "ghost-2",
                VehicleName = "Ghost 2"
            };

            runtimes.Upsert(first);
            await teams.CreateAsync(["ghost-2", "ghost-1"]);

            // Adding the second row causes an ObservableCollection notification.
            // Team ordering must run only after that notification completes.
            runtimes.Upsert(second);

            Assert.Equal(["ghost-runtime-2", "ghost-runtime-1"], viewModel.Units.Select(item => item.Id).ToArray());
            Assert.All(viewModel.Units, item => Assert.True(item.IsInTeam));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestUnitObservationWorkflow(params UnitObservationSnapshot[] units) : IUnitObservationWorkflow
    {
        public event EventHandler? Changed;
        public IReadOnlyList<UnitObservationSnapshot> Units { get; } = units;
        public bool TryGet(string unitId, out UnitObservationSnapshot? unit)
        {
            unit = Units.FirstOrDefault(item => item.Id == unitId);
            return unit is not null;
        }
    }

    private sealed class TestGhostUnitWorkflow(
        IEntityStore<string, RuntimeRecord> runtimes,
        IEntityStore<string, VehicleRecord> vehicles) : IGhostUnitWorkflow
    {
        private readonly List<GhostUnitSnapshot> _ghosts = [];

        public event EventHandler? Changed;
        public IReadOnlyList<GhostUnitSnapshot> Ghosts => _ghosts;

        public Task<IReadOnlyList<GhostUnitSnapshot>> CreateAsync(
            GhostCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            var created = new List<GhostUnitSnapshot>();
            for (var index = 0; index < request.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var number = _ghosts.Count + 1;
                var id = $"ghost-{number}";
                var name = $"Ghost {number}";
                var snapshot = new GhostUnitSnapshot(id, name, $"ghost-connection-{number}", 43.65, -79.38, 0, 90, "Disarmed", null);
                _ghosts.Add(snapshot);
                vehicles.Upsert(new VehicleRecord(
                    id,
                    name,
                    [snapshot.ConnectionId],
                    null,
                    null,
                    "Multicopter",
                    "Air",
                    request.ProfileId,
                    AvailabilityState.Online,
                    "Ready",
                    "Landed",
                    "Disarmed",
                    "Healthy",
                    [],
                    DateTimeOffset.UtcNow,
                    true));
                runtimes.Upsert(new RuntimeRecord(
                    $"ghost-runtime-{number}",
                    name,
                    [snapshot.ConnectionId],
                    AvailabilityState.Online,
                    "Ghost",
                    "Simulated",
                    "multicopter",
                    "ghost",
                    "in-app",
                    "Healthy",
                    "Ready",
                    [],
                    DateTimeOffset.UtcNow,
                    id,
                    name,
                    true));
                created.Add(snapshot);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult<IReadOnlyList<GhostUnitSnapshot>>(created);
        }

        public Task DeleteAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected condition was not reached.");
    }
}
