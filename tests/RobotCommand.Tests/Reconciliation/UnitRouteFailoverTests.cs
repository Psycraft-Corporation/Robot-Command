using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests.Reconciliation;

public sealed class UnitRouteFailoverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"robot-command-routes-{Guid.NewGuid():N}");

    [Fact]
    public async Task TelemetryFailsOverAndDoesNotFailBackWhenPrimaryRecovers()
    {
        var units = new UnitDefinitionService(_directory);
        var connections = Store<ConnectionRecord>();
        var telemetry = Store<VehicleTelemetryRecord>();
        var diagnostics = Store<VehicleDiagnosticsSnapshot>();
        var cameras = Store<CameraSourceRecord>();
        AddVehicleRoute(connections, telemetry, "primary", AvailabilityState.Online);
        AddVehicleRoute(connections, telemetry, "standby", AvailabilityState.Online);
        var unit = await SaveUnit(units);
        using var routing = new UnitRouteFailoverService(units, connections, telemetry, diagnostics, cameras, new AppConfiguration());

        Assert.Equal("primary", routing.ActiveConnectionFor(unit.Id, UnitRouteRole.Telemetry));
        connections.Upsert(Connection("primary", AvailabilityState.Offline));
        Assert.Equal("standby", routing.ActiveConnectionFor(unit.Id, UnitRouteRole.Telemetry));
        Assert.Equal(UnitRouteHealth.Degraded, Assert.Single(routing.ForUnit(unit.Id).Where(item => item.Role == UnitRouteRole.Telemetry)).Health);

        connections.Upsert(Connection("primary", AvailabilityState.Online));
        Assert.Equal("standby", routing.ActiveConnectionFor(unit.Id, UnitRouteRole.Telemetry));
    }

    [Fact]
    public async Task CommandLossAwaitsConfirmationAndDoesNotChooseStandbyAutomatically()
    {
        var units = new UnitDefinitionService(_directory);
        var connections = Store<ConnectionRecord>();
        var telemetry = Store<VehicleTelemetryRecord>();
        var diagnostics = Store<VehicleDiagnosticsSnapshot>();
        var cameras = Store<CameraSourceRecord>();
        AddVehicleRoute(connections, telemetry, "primary", AvailabilityState.Online);
        AddVehicleRoute(connections, telemetry, "standby", AvailabilityState.Online);
        var unit = await SaveUnit(units);
        using var routing = new UnitRouteFailoverService(units, connections, telemetry, diagnostics, cameras, new AppConfiguration());

        connections.Upsert(Connection("primary", AvailabilityState.Offline));
        var status = Assert.Single(routing.ForUnit(unit.Id).Where(item => item.Role == UnitRouteRole.Command));
        Assert.Equal(UnitRouteHealth.AwaitingConfirmation, status.Health);
        Assert.Null(routing.ActiveConnectionFor(unit.Id, UnitRouteRole.Command));
        await Assert.ThrowsAsync<InvalidOperationException>(() => routing.SelectAsync(unit.Id, UnitRouteRole.Command, 1, false));

        var selected = await routing.SelectAsync(unit.Id, UnitRouteRole.Command, 1, true);
        Assert.Equal("standby", selected.Active?.ConnectionId);
        Assert.Equal("standby", routing.ActiveConnectionFor(unit.Id, UnitRouteRole.Command));
    }

    [Fact]
    public async Task VideoAutomaticallyMovesToConfiguredHealthyStandby()
    {
        var units = new UnitDefinitionService(_directory);
        var connections = Store<ConnectionRecord>();
        var telemetry = Store<VehicleTelemetryRecord>();
        var diagnostics = Store<VehicleDiagnosticsSnapshot>();
        var cameras = Store<CameraSourceRecord>();
        cameras.Upsert(MediaSource("video-primary", AvailabilityState.Online, true));
        cameras.Upsert(MediaSource("video-standby", AvailabilityState.Online, true));
        var unit = await units.SaveAsync(null, new UnitDefinitionRequest("Dracula", [], ConnectionIds: ["camera-link"],
            Cameras: [new UnitCameraDeviceBinding("zr10", "ZR10", MediaSourceId: "video-primary", StandbyMediaSourceIds: ["video-standby"])]));
        using var routing = new UnitRouteFailoverService(units, connections, telemetry, diagnostics, cameras, new AppConfiguration());

        Assert.Equal("video-primary", routing.ForUnit(unit.Id).Single(item => item.Role == UnitRouteRole.Video).Active?.MediaSourceId);
        cameras.Upsert(MediaSource("video-primary", AvailabilityState.Offline, false));
        var status = routing.ForUnit(unit.Id).Single(item => item.Role == UnitRouteRole.Video);
        Assert.Equal("video-standby", status.Active?.MediaSourceId);
        Assert.Equal(UnitRouteHealth.Degraded, status.Health);
    }

    private async Task<UnitDefinitionSnapshot> SaveUnit(UnitDefinitionService units)
    {
        var routes = new[]
        {
            new UnitRouteCandidate(UnitRouteRole.Command, "primary", "vehicle"),
            new UnitRouteCandidate(UnitRouteRole.Command, "standby", "vehicle"),
            new UnitRouteCandidate(UnitRouteRole.Telemetry, "primary", "vehicle"),
            new UnitRouteCandidate(UnitRouteRole.Telemetry, "standby", "vehicle")
        };
        return await units.SaveAsync(null, new UnitDefinitionRequest("Dracula", [new("primary", "vehicle"), new("standby", "vehicle")],
            ConnectionIds: ["primary", "standby"], Routes: routes));
    }

    private static void AddVehicleRoute(EntityStore<string, ConnectionRecord> connections,
        EntityStore<string, VehicleTelemetryRecord> telemetry, string id, AvailabilityState state)
    {
        connections.Upsert(Connection(id, state));
        telemetry.Upsert(new VehicleTelemetryRecord($"telemetry-{id}", "vehicle", id, null, state, false, "Landed", "Hold", "Ready",
            "Healthy", "Ready", null, null, null, null, null, null, null, null, null, null, null, false, "OK", "Fresh",
            DateTimeOffset.UtcNow));
    }

    private static ConnectionRecord Connection(string id, AvailabilityState state)
        => new(id, id, "test", ConnectionMode.Mavlink, state, false, LastSeen: DateTimeOffset.UtcNow);

    private static CameraSourceRecord MediaSource(string id, AvailabilityState state, bool fresh)
        => new($"media:{id}", id, $"media:{id}", null, id, "RTSP", state, state.ToString(), state.ToString(),
            fresh, fresh, false, 0, 0, 0, 0, string.Empty, "TEST", state.ToString(), DateTimeOffset.UtcNow,
            SupportsPhoto: false, SupportsVideo: false, SupportsGimbal: false);

    private static EntityStore<string, T> Store<T>() where T : notnull
        => new(item => item switch
        {
            ConnectionRecord value => value.Id,
            VehicleTelemetryRecord value => value.Id,
            VehicleDiagnosticsSnapshot value => value.Id,
            CameraSourceRecord value => value.Id,
            _ => throw new InvalidOperationException("Unsupported test record.")
        }, StringComparer.Ordinal);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
