using RobotCommand.Models;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests.ViewModels;

public sealed class UnitsPanelViewModelTests
{
    [Fact]
    public void ManageConnectionsListsConnectionObservationsAndEnablesAuthoritySelection()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-unit-panel-{Guid.NewGuid():N}");
        try
        {
            var runtimes = new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal);
            var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
            var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
            var selection = new SelectionService();
            var definitions = new UnitDefinitionService(directory);
            var viewModel = new UnitsPanelViewModel(
                runtimes, connections, selection, vehicles, reconciliation: definitions);
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

            Assert.True(viewModel.IsEditingUnitConnections);
            Assert.Equal(2, viewModel.AvailableUnitConnections.Count);
            Assert.Single(viewModel.IncludedUnitConnections);
            viewModel.AvailableUnitConnections.Single(item => item.ConnectionId == "px4").IsIncluded = true;
            Assert.Equal(2, viewModel.IncludedUnitConnections.Count);
            Assert.Equal("logos", viewModel.CommandAuthority?.ConnectionId);
            viewModel.TelemetryAuthority = viewModel.IncludedUnitConnections.Single(item => item.ConnectionId == "px4");
            viewModel.DiagnosticsAuthority = viewModel.IncludedUnitConnections.Single(item => item.ConnectionId == "px4");
            Assert.Equal("px4", viewModel.TelemetryAuthority?.ConnectionId);
            Assert.True(viewModel.SaveUnitConnectionsCommand.CanExecute(null));
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
        var selection = new SelectionService();
        var viewModel = new UnitsPanelViewModel(runtimes, connections, selection, vehicles, telemetry);
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
        viewModel.SelectedUnit = runtime;

        Assert.Equal("5.5°", viewModel.SelectedHeading);
    }
}
