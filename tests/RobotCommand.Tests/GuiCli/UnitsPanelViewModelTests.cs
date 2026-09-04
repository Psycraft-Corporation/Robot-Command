using RobotCommand.Core;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests.ViewModels;

public sealed class UnitsPanelViewModelTests
{
    [Fact]
    public void NewUnitShowsSavedConnectionsWhenNoVehicleHasBeenDiscovered()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-unit-connections-{Guid.NewGuid():N}");
        try
        {
            var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
            var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
            var cameras = new EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal);
            var definitions = new UnitDefinitionService(directory);
            var settings = new ApplicationSettingsService(
                AppConfiguration.Load(directory),
                new ApplicationSettingsPersistence(directory));
            var localization = new LocalizationService(settings);
            var library = new UnitsLibraryViewModel(definitions, connections, vehicles, cameras, localization);
            connections.Upsert(new ConnectionRecord(
                "px4", "PX4 Dracula", "udp-listen://0.0.0.0:14550", ConnectionMode.Mavlink,
                AvailabilityState.Offline, true));

            library.NewUnitCommand.Execute(null);

            var candidate = Assert.Single(library.AvailableConnections);
            Assert.Equal("px4", candidate.ConnectionId);
            Assert.Equal("PX4 Dracula", candidate.Name);
            Assert.False(candidate.HasVehicleObservation);
            Assert.Empty(library.AvailableVehicles);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectingAConnectionSelectsItsDiscoveredVehicleSources()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-unit-selection-{Guid.NewGuid():N}");
        try
        {
            var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
            var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
            var cameras = new EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal);
            var definitions = new UnitDefinitionService(directory);
            var settings = new ApplicationSettingsService(
                AppConfiguration.Load(directory),
                new ApplicationSettingsPersistence(directory));
            var localization = new LocalizationService(settings);
            var library = new UnitsLibraryViewModel(definitions, connections, vehicles, cameras, localization);
            connections.Upsert(new ConnectionRecord(
                "px4", "PX4 Dracula", "udp-listen://0.0.0.0:14550", ConnectionMode.Mavlink,
                AvailabilityState.Online, true));
            vehicles.Upsert(new VehicleRecord(
                "px4-system-1", "Dracula", ["px4"], null, null, "Multicopter", "Air", "px4",
                AvailabilityState.Online));

            library.NewUnitCommand.Execute(null);

            var connection = Assert.Single(library.AvailableConnections);
            connection.IsSelected = true;

            var vehicle = Assert.Single(library.AvailableVehicles);
            Assert.True(vehicle.IsSelected);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ManageConnectionsOpensTheSharedUnitEditorForTheSelectedVehicle()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-unit-panel-{Guid.NewGuid():N}");
        try
        {
            var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
            var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
            var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
            var cameras = new EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal);
            var selection = new SelectionService();
            var definitions = new UnitDefinitionService(directory);
            var settings = new ApplicationSettingsService(
                AppConfiguration.Load(directory),
                new ApplicationSettingsPersistence(directory));
            var localization = new LocalizationService(settings);
            var library = new UnitsLibraryViewModel(definitions, connections, vehicles, cameras, localization);
            var viewModel = new UnitsPanelViewModel(
                runtimes, connections, selection, vehicles, reconciliation: definitions, unitLibrary: library);
            connections.Upsert(new ConnectionRecord(
                "logos", "Logos", "http://localhost:19000", ConnectionMode.Direct,
                AvailabilityState.Online, true));
            connections.Upsert(new ConnectionRecord(
                "px4", "PX4", "serial://COM3", ConnectionMode.Mavlink,
                AvailabilityState.Online, true));
            var logos = new VehicleRecord(
                "logos-dracula", "Dracula", ["logos"], null, null, "Multicopter", "Air", "",
                AvailabilityState.Online);
            var px4 = new VehicleRecord(
                "mavlink:px4:1", "PX4 System 1", ["px4"], null, null, "Multicopter", "Air", "",
                AvailabilityState.Online);
            vehicles.Upsert(logos);
            vehicles.Upsert(px4);
            var runtime = new RuntimeRecord(
                "logos-runtime", "Dracula", ["logos"], AvailabilityState.Online, "Vehicle", "",
                "", "", "", "", "", [], VehicleId: logos.Id, VehicleName: logos.Name);
            runtimes.Upsert(runtime);
            runtimes.Upsert(runtime with
            {
                Id = "px4-runtime",
                Name = "PX4 System 1",
                ConnectionIds = ["px4"],
                VehicleId = px4.Id,
                VehicleName = px4.Name
            });
            viewModel.SelectedUnit = runtime;

            viewModel.ManageUnitConnectionsCommand.Execute(null);

            Assert.True(library.IsEditing);
            Assert.Equal("logos", library.AvailableVehicles.Single(item => item.SourceId == logos.Id).ConnectionId);
            Assert.True(library.AvailableVehicles.Single(item => item.SourceId == logos.Id).IsSelected);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectingSparseRuntimePublishesSelectionWithoutThrowing()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(runtimes, connections, selection);
        Assert.False(viewModel.HasSelectedUnit);
        var runtime = new RuntimeRecord(
            "logos-sitl",
            "SITL",
            [],
            AvailabilityState.Online,
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            null!,
            DateTimeOffset.UtcNow);

        runtimes.Upsert(runtime);

        var exception = Record.Exception(() => viewModel.SelectedUnit = runtime);

        Assert.Null(exception);
        Assert.True(viewModel.HasSelectedUnit);
        Assert.Equal(SelectionKind.Runtime, selection.Current.Kind);
        Assert.Equal("logos-sitl", selection.Current.Id);
        Assert.Equal("0", viewModel.SelectedConnections);
        Assert.Equal("0", selection.Current.Fields.Single(item => item.Label == "Capabilities").Value);
    }

    [Fact]
    public void LiveRuntimeRefreshDoesNotClearSelectedUnit()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(runtimes, connections, selection);
        var runtime = new RuntimeRecord(
            "logos-sitl",
            "Logos API Server",
            [],
            AvailabilityState.Online,
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            [],
            VehicleId: "digital_dracula",
            VehicleName: "Dracula");

        runtimes.Upsert(runtime);
        viewModel.SelectedUnit = runtime;

        runtimes.ReplaceAll([runtime with { State = AvailabilityState.Degraded }]);
        viewModel.SelectedUnit = null;

        Assert.NotNull(viewModel.SelectedUnit);
        Assert.Equal("Dracula", viewModel.SelectedTitle);
        Assert.Equal("digital_dracula", viewModel.SelectedIdentity);
        Assert.Equal(SelectionKind.Runtime, selection.Current.Kind);
    }

    [Fact]
    public void TransientListItemClearDoesNotClearSharedSelection()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(runtimes, connections, selection);
        var runtime = new RuntimeRecord(
            "logos-sitl", "Logos API Server", [], AvailabilityState.Online, "Development", "SITL",
            "development", "local", "0.1.0", "Healthy", "Ready", []);

        runtimes.Upsert(runtime);
        viewModel.SelectedUnit = runtime;
        viewModel.SelectedUnitItem = null;

        Assert.NotNull(viewModel.SelectedUnit);
        Assert.Equal(SelectionKind.Runtime, selection.Current.Kind);
    }

    [Fact]
    public void SelectingRuntimeBackedByVehicleSelectsVehicleTarget()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(runtimes, connections, selection, vehicles);
        var vehicle = new VehicleRecord(
            "digital_dracula",
            "Dracula",
            ["connection-1"],
            "logos-sitl",
            null,
            "Multicopter",
            "Air",
            "sitl",
            AvailabilityState.Online,
            "Ready",
            "Running",
            "Disarmed",
            "Healthy");
        var runtime = new RuntimeRecord(
            "logos-sitl",
            "Logos API Server",
            ["connection-1"],
            AvailabilityState.Online,
            "Development",
            "SITL",
            "development",
            "local",
            "0.1.0",
            "Healthy",
            "Ready",
            [],
            VehicleId: vehicle.Id,
            VehicleName: vehicle.Name);

        vehicles.Upsert(vehicle);
        runtimes.Upsert(runtime);
        viewModel.SelectedUnit = runtime;

        Assert.Equal(SelectionKind.Vehicle, selection.Current.Kind);
        Assert.Equal(vehicle.Id, selection.Current.Id);
    }

    [Fact]
    public void SelectedUnitShowsCompassHeadingFromLiveTelemetry()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        var diagnostics = new EntityStore<string, VehicleDiagnosticsSnapshot>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(
            runtimes,
            connections,
            selection,
            vehicles,
            telemetry,
            diagnosticsStore: diagnostics);
        var vehicle = new VehicleRecord(
            "digital_dracula", "Dracula", ["connection-1"], "logos-sitl", null,
            "Multicopter", "Air", "sitl", AvailabilityState.Online, "Ready", "Running",
            "Armed", "Healthy");
        var runtime = new RuntimeRecord(
            "logos-sitl", "Logos API Server", ["connection-1"], AvailabilityState.Online,
            "Development", "SITL", "development", "local", "0.1.0", "Healthy", "Ready", [],
            VehicleId: vehicle.Id, VehicleName: vehicle.Name);
        var sample = new VehicleTelemetryRecord(
            "connection-1:digital_dracula", vehicle.Id, "connection-1", "logos-sitl",
            AvailabilityState.Online, true, "Flying", "Multicopter", "Running", "Healthy", "Ready",
            43.3977443, -79.454594, 120, 50, 10, 20, -50, 0, 0, 0, 725.5, false,
            "OK", "", DateTimeOffset.UtcNow);

        vehicles.Upsert(vehicle);
        runtimes.Upsert(runtime);
        telemetry.Upsert(sample);
        diagnostics.Upsert(new VehicleDiagnosticsSnapshot(
            "diagnostics-1",
            vehicle.Id,
            "connection-1",
            "logos-sitl",
            VehicleDiagnosticStatus.Ready,
            "Ready",
            VehicleDiagnosticStatus.Ready,
            "Ready",
            VehicleDiagnosticStatus.Ready,
            "Ready",
            VehicleDiagnosticStatus.Ready,
            "Ready",
            [],
            [],
            DateTimeOffset.UtcNow,
            BatteryRemainingPercent: 100));
        viewModel.SelectedUnit = runtime;

        Assert.Empty(viewModel.SelectedBattery);
        Assert.Equal("5.5°", viewModel.SelectedHeading);
    }

    [Fact]
    public async Task CreatingTeamSelectsTheCreatedTeam()
    {
        var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        selection.SetUnitSelection([
            new OperationalSelection(SelectionKind.Vehicle, "vehicle-1", "Vehicle 1", "", [])]);
        var team = new TeamSnapshot(
            "team-created", "Team 1",
            [new TeamMemberSnapshot("vehicle-1", 0, true, "Vehicle 1", "Ghost")],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var teams = new RecordingTeamWorkflow(team);
        var teamSelection = new RecordingTeamSelectionWorkflow();
        var viewModel = new UnitsPanelViewModel(
            runtimes,
            connections,
            selection,
            teams: teams,
            teamSelection: teamSelection);

        await viewModel.CreateTeamFromSelectionAsync();

        Assert.Equal(team.Id, teams.CreatedTeam?.Id);
        Assert.Equal(team.Id, teamSelection.SelectedTeamId);
    }

    private sealed class RecordingTeamWorkflow(TeamSnapshot team) : ITeamWorkflow
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public TeamSnapshot? CreatedTeam { get; private set; }

        public TeamWorkflowSnapshot Current => new(
            0,
            CreatedTeam is null ? [] : [CreatedTeam],
            CreatedTeam?.Members.ToDictionary(member => member.UnitId, _ => team.Id) ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        public bool TryGet(string teamId, out TeamSnapshot? result)
        {
            result = CreatedTeam?.Id == teamId ? CreatedTeam : null;
            return result is not null;
        }

        public string? GetTeamIdForUnit(string unitId)
            => CreatedTeam?.Members.Any(member => member.UnitId == unitId) == true ? CreatedTeam.Id : null;

        public Task<TeamSnapshot> CreateAsync(
            IReadOnlyList<string> unitIds,
            string? name = null,
            CancellationToken cancellationToken = default)
        {
            CreatedTeam = team;
            return Task.FromResult(team);
        }

        public Task<TeamSnapshot> AssignAsync(string teamId, IReadOnlyList<string> unitIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearMembershipAsync(IReadOnlyList<string> unitIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReorderMemberAsync(string teamId, string unitId, int index, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUnitAsync(string unitId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingTeamSelectionWorkflow : ITeamSelectionWorkflow
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public string? SelectedTeamId { get; private set; }

        public TeamSelectionWorkflowSnapshot Current => SelectedTeamId is null
            ? TeamSelectionWorkflowSnapshot.Empty
            : new(SelectedTeamId, "Team 1", ["vehicle-1"], 1, DateTimeOffset.UtcNow);

        public Task SelectAsync(string teamId, CancellationToken cancellationToken = default)
        {
            SelectedTeamId = teamId;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
