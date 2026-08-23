using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Location;
using RobotCommand.Services.Reconciliation;
using RobotCommand.Services.Workflows;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class UnitObservationWorkflowTests
{
    [Fact]
    public void ProjectsTelemetryBatteryDistanceConnectionsAndSignals()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-observation-{Guid.NewGuid():N}");
        try
        {
            var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
            var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
            var diagnostics = new EntityStore<string, VehicleDiagnosticsSnapshot>(item => item.Id, StringComparer.Ordinal);
            var links = new EntityStore<string, LinkRecord>(item => item.Id, StringComparer.Ordinal);
            var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
            var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
            var reconciliation = new UnitDefinitionService(directory);
            var location = new TestOperatorLocationService(43.6500, -79.3800);

            connections.Upsert(new ConnectionRecord("px4", "Dracula radio", "serial://COM3", ConnectionMode.Mavlink, AvailabilityState.Online, true, LastSeen: DateTimeOffset.UtcNow));
            vehicles.Upsert(new VehicleRecord("vehicle-1", "Dracula", ["px4"], null, null, "Multicopter", "Air", "px4", AvailabilityState.Online));
            telemetry.Upsert(new VehicleTelemetryRecord("telemetry-1", "vehicle-1", "px4", null, AvailabilityState.Online,
                true, "Flying", "Multicopter", "Running", "Healthy", "Ready", 43.6510, -79.3790, 120, 20,
                null, null, null, 3, 4, -1, 90, false, "OK", string.Empty, DateTimeOffset.UtcNow));
            diagnostics.Upsert(new VehicleDiagnosticsSnapshot("diagnostics-1", "vehicle-1", "px4", "PX4",
                VehicleDiagnosticStatus.Ready, "Ready", VehicleDiagnosticStatus.Ready, "Ready", VehicleDiagnosticStatus.Ready, "Ready",
                VehicleDiagnosticStatus.Ready, "Current", [], [], DateTimeOffset.UtcNow,
                BatteryRemainingPercent: 76, BatteryVoltageVolts: 15.8));
            links.Upsert(new LinkRecord("link-1", "radio-link", "px4", null, "Dracula radio", "SiK", "Bidirectional", "Online", "Healthy", "Ready",
                true, false, null, null, null, -61, 18, .94, 1.2, 42, "OK", string.Empty, DateTimeOffset.UtcNow));

            var workflow = new UnitObservationWorkflow(vehicles, telemetry, diagnostics, links, commands, reconciliation, connections, location);
            var unit = Assert.Single(workflow.Units);

            Assert.Equal(3, unit.Telemetry?.VelocityNorthMetresPerSecond);
            Assert.Equal(4, unit.Telemetry?.VelocityEastMetresPerSecond);
            Assert.Equal(76, unit.Telemetry?.BatteryRemainingPercent);
            Assert.Equal(15.8, unit.Telemetry?.BatteryVoltageVolts);
            Assert.InRange(unit.DistanceFromOperatorMetres!.Value, 100, 200);
            var association = Assert.Single(unit.AssociatedConnections!);
            Assert.Equal("Dracula radio", association.Name);
            Assert.True(association.IsCommandAuthority);
            var link = Assert.Single(unit.Links);
            Assert.Equal(-61, link.RssiDbm);
            Assert.Equal(18, link.SnrDb);

            telemetry.Upsert(telemetry.Items[0] with { IsStale = true });
            Assert.Null(workflow.Units.Single().DistanceFromOperatorMetres);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void OmitsOperatorDistanceWhenLocationIsUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-observation-{Guid.NewGuid():N}");
        try
        {
            var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
            var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
            var diagnostics = new EntityStore<string, VehicleDiagnosticsSnapshot>(item => item.Id, StringComparer.Ordinal);
            var links = new EntityStore<string, LinkRecord>(item => item.Id, StringComparer.Ordinal);
            var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
            var reconciliation = new UnitDefinitionService(directory);
            vehicles.Upsert(new VehicleRecord("vehicle-1", "Ghost", [], null, null, "Multicopter", "Air", "ghost", AvailabilityState.Online, IsGhost: true));
            diagnostics.Upsert(new VehicleDiagnosticsSnapshot("ghost-diagnostics", "vehicle-1", "ghost", "Ghost",
                VehicleDiagnosticStatus.Ready, "Ready", VehicleDiagnosticStatus.Ready, "Ready", VehicleDiagnosticStatus.Ready, "Ready",
                VehicleDiagnosticStatus.Ready, "Current", [], [], DateTimeOffset.UtcNow, BatteryRemainingPercent: 100, BatteryVoltageVolts: 16.8));
            var workflow = new UnitObservationWorkflow(vehicles, telemetry, diagnostics, links, commands, reconciliation,
                operatorLocation: new TestOperatorLocationService());

            var unit = Assert.Single(workflow.Units);
            Assert.Null(unit.DistanceFromOperatorMetres);
            Assert.Equal("Ready", unit.Diagnostics?.OverallStatus);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class TestOperatorLocationService : IOperatorLocationService
    {
        public TestOperatorLocationService(double? latitude = null, double? longitude = null)
        {
            Snapshot = latitude is double lat && longitude is double lon
                ? OperatorLocationSnapshot.AvailableAt(lon, lat, 5, DateTimeOffset.UtcNow)
                : OperatorLocationSnapshot.Unavailable();
        }

        public event EventHandler? Changed;
        public OperatorLocationSnapshot Snapshot { get; }
    }
}
