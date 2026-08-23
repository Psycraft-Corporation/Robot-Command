using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Simulation;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GhostUnitServiceTests
{
    [Fact]
    public void DraculaProfile_IsBuiltInAndUsesSimulationEnvelope()
    {
        var profile = GhostProfileDefaults.Dracula;

        Assert.Equal("dracula", profile.Id);
        Assert.Equal("Dracula", profile.Name);
        Assert.Equal("Multicopter", profile.VehicleType);
        Assert.Equal(10, profile.Simulation.MaximumHorizontalSpeedMetresPerSecond);
        Assert.Equal(2, profile.Simulation.MaximumClimbRateMetresPerSecond);
        Assert.Equal(2, profile.Simulation.MaximumDescentRateMetresPerSecond);
        Assert.Equal(120, profile.Simulation.MaximumAltitudeAglMetres);
        Assert.True(profile.IsBuiltIn);
        Assert.False(profile.IsEditable);
    }

    [Fact]
    public async Task ProfileAwareCreation_AttachesProfileToVehicleAndGhostSnapshot()
    {
        var stores = new Stores();
        var profiles = new GhostProfileWorkflow();
        await using var service = stores.CreateService(profiles);

        var ghost = await service.CreateAsync("dracula");

        Assert.Equal("dracula", ghost.ProfileKey);
        Assert.Equal("dracula", stores.Vehicles.Items.Single(item => item.Id == ghost.Id).ProfileKey);
    }

    [Fact]
    public async Task ProfileAwareCreation_RejectsUnknownProfile()
    {
        var stores = new Stores();
        await using var service = stores.CreateService(new GhostProfileWorkflow());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("not-a-profile"));

        Assert.Contains("not-a-profile", error.Message, StringComparison.Ordinal);
        Assert.Empty(service.GhostVehicleIds);
    }

    [Fact]
    public async Task Create_UsesViewportAndCreatesEphemeralMulticopter()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();

        var first = await service.CreateAsync(new MapViewportSnapshot(-79.38, 43.65, 20, 0));
        var second = await service.CreateAsync(new MapViewportSnapshot(-79.40, 43.66, 20, 0));

        Assert.Equal("ghost-1", first.Id);
        Assert.Equal("Ghost 1", first.Name);
        Assert.Equal("Ghost 2", second.Name);
        Assert.Equal("Multicopter", first.VehicleClass);
        Assert.True(first.IsGhost);
        Assert.Equal(-79.38, stores.Telemetry.Items.Single(item => item.VehicleId == first.Id).LongitudeDegrees);
        Assert.Equal(43.65, stores.Telemetry.Items.Single(item => item.VehicleId == first.Id).LatitudeDegrees);
        Assert.Equal(0, stores.Telemetry.Items.Single(item => item.VehicleId == first.Id).AltitudeAglMetres);
        Assert.Equal(90, stores.Telemetry.Items.Single(item => item.VehicleId == first.Id).HeadingDegrees);
        Assert.True(stores.Connections.Items.Single(item => item.Id == "ghost-connection-1").IsGhost);
        Assert.Equal(1d, stores.Links.Items.Single(item => item.ConnectionId == "ghost-connection-1").Quality);
        Assert.Equal(0d, stores.Links.Items.Single(item => item.ConnectionId == "ghost-connection-1").PacketLoss);
        Assert.Equal("Simulated horizon", stores.CameraSources.Items.Single(item => item.ConnectionId == "ghost-connection-1").Kind);
        Assert.Equal(960u, stores.CameraSources.Items.Single(item => item.ConnectionId == "ghost-connection-1").Width);
        Assert.DoesNotContain(stores.Connections.Items, item => item.Mode == ConnectionMode.Direct);
    }

    [Fact]
    public async Task Delete_CleansGhostRecordsAndSelection()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        var ghost = await service.CreateAsync();
        stores.Selection.Select(SelectionFactory.From(ghost));

        await service.DeleteAsync(ghost.Id);

        Assert.Empty(service.GhostVehicleIds);
        Assert.DoesNotContain(stores.Connections.Items, item => item.IsGhost);
        Assert.DoesNotContain(stores.Runtimes.Items, item => item.IsGhost);
        Assert.DoesNotContain(stores.Vehicles.Items, item => item.IsGhost);
        Assert.DoesNotContain(stores.Telemetry.Items, item => item.IsGhost);
        Assert.DoesNotContain(stores.Links.Items, item => item.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal));
        Assert.Equal(SelectionKind.None, stores.Selection.Current.Kind);
    }

    [Fact]
    public async Task Delete_PreservesOtherSelectedGhosts()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        var first = await service.CreateAsync();
        var second = await service.CreateAsync();
        stores.Selection.SetUnitSelection([SelectionFactory.From(first), SelectionFactory.From(second)], first.Id);

        await service.DeleteAsync(first.Id);

        Assert.Equal([second.Id], stores.Selection.SelectedUnitIds);
        Assert.Equal(second.Id, stores.Selection.UnitSelectionAnchorId);
    }

    [Fact]
    public async Task Delete_AfterMissionPreparationRemovesAllEphemeralRecords()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync();
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);
        await service.ExecuteAsync(new OperatorCommandRequest(
            "arm-before-delete", "corr", "idem", OperatorCommandKind.Arm, target,
            "test", false, DateTimeOffset.UtcNow));
        await service.PrepareMissionAsync(ghost.Id, new FlightMissionExecutionArtifact(
            "delete-mission", "Delete test", [new(0, "takeoff", FlightMissionStepKind.Takeoff, null, 5, 1)], "hash"));
        await service.StartMissionAsync(ghost.Id);

        await service.DeleteAsync(ghost.Id);

        Assert.Empty(service.GhostVehicleIds);
        Assert.DoesNotContain(stores.Connections.Items, item => item.IsGhost);
        Assert.DoesNotContain(stores.Runtimes.Items, item => item.IsGhost);
        Assert.DoesNotContain(stores.Vehicles.Items, item => item.IsGhost);
        Assert.DoesNotContain(stores.Telemetry.Items, item => item.IsGhost);
    }

    [Fact]
    public async Task Create_CanSetInitialCompassHeading()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();

        var ghost = await service.CreateAsync(null, 270);

        Assert.Equal(270, stores.Telemetry.Items.Single(item => item.VehicleId == ghost.Id).HeadingDegrees);
    }

    [Fact]
    public async Task TakeoffAndHeading_AreSimulatedAtRuntime()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync();
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);

        await service.ExecuteAsync(new OperatorCommandRequest(
            "takeoff-1", "corr-1", "idem-1", OperatorCommandKind.Takeoff, target,
            "test", false, DateTimeOffset.UtcNow,
            Parameters: new OperatorCommandParameters(TakeoffAltitudeAglMetres: 2)));
        await Task.Delay(350);

        var telemetry = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.True(telemetry.AltitudeAglMetres > 0);
        Assert.Equal("Flying", telemetry.LandedState);

        await service.ExecuteAsync(new OperatorCommandRequest(
            "heading-1", "corr-2", "idem-2", OperatorCommandKind.SetHeading, target,
            "test", false, DateTimeOffset.UtcNow,
            Parameters: OperatorCommandParameters.AbsoluteHeading(180)));
        await Task.Delay(1100);

        telemetry = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.InRange(telemetry.HeadingDegrees.GetValueOrDefault(), 179, 181);
    }

    [Fact]
    public async Task GoTo_GraduallyTurnsGhostTowardTravelBearing()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync();
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);

        await service.ExecuteAsync(new OperatorCommandRequest(
            "goto-1", "corr-1", "idem-1", OperatorCommandKind.GoTo, target,
            "test", false, DateTimeOffset.UtcNow,
            Parameters: OperatorCommandParameters.LocalGoTo(100, 0, 0, null, 1)));
        await Task.Delay(350);

        var telemetry = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.True(telemetry.LatitudeDegrees > 43.65);
        Assert.InRange(telemetry.HeadingDegrees.GetValueOrDefault(), 0, 85);
    }

    [Fact]
    public async Task GlobalGoTo_MovesGhostTowardWgs84Target()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync(new MapViewportSnapshot(-79.42, 43.73, 20, 0));
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);

        await service.ExecuteAsync(new OperatorCommandRequest(
            "global-goto-1", "corr-1", "idem-1", OperatorCommandKind.GoTo, target,
            "test", false, DateTimeOffset.UtcNow,
            Parameters: OperatorCommandParameters.GlobalGoTo(43.731, -79.419, 100, 1)));
        await Task.Delay(350);

        var telemetry = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.True(telemetry.LatitudeDegrees > 43.73);
        Assert.True(telemetry.LongitudeDegrees > -79.42);
    }

    [Fact]
    public async Task CompletingOperationDoesNotStopPhysicsLoop()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync(new MapViewportSnapshot(-79.42, 43.73, 20, 0));
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);

        await service.ExecuteAsync(new OperatorCommandRequest(
            "takeoff-complete", "corr-1", "idem-1", OperatorCommandKind.Takeoff, target,
            "test", false, DateTimeOffset.UtcNow,
            Parameters: new OperatorCommandParameters(TakeoffAltitudeAglMetres: 0.5)));
        await Task.Delay(450);

        await service.ExecuteAsync(new OperatorCommandRequest(
            "goto-after-complete", "corr-2", "idem-2", OperatorCommandKind.GoTo, target,
            "test", false, DateTimeOffset.UtcNow,
            Parameters: OperatorCommandParameters.GlobalGoTo(43.731, -79.419, 100, 1)));
        await Task.Delay(350);

        var telemetry = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.True(telemetry.LatitudeDegrees > 43.73);
        Assert.True(telemetry.LongitudeDegrees > -79.42);
    }

    [Fact]
    public async Task FormationTracking_UsesDampedCorrectionAndSettlesWithoutLargeOvershoot()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync();
        var initial = stores.Telemetry.Items.Single(item => item.VehicleId == ghost.Id);
        var initialLatitude = Assert.IsType<double>(initial.LatitudeDegrees);
        var initialLongitude = Assert.IsType<double>(initial.LongitudeDegrees);
        var initialAltitude = Assert.IsType<double>(initial.AltitudeAglMetres);
        const double targetDistanceMetres = 5d;
        const double earthRadiusMetres = 6378137d;
        var targetLatitude = initialLatitude + targetDistanceMetres / earthRadiusMetres * 180d / Math.PI;

        await service.SetFormationTargetAsync(ghost.Id, new GhostFormationTarget(
            "formation-test", targetLatitude, initialLongitude, initialAltitude,
            0d, 0d, 0d));

        var maximumNorth = double.NegativeInfinity;
        for (var sample = 0; sample < 60; sample++)
        {
            await Task.Delay(75);
            maximumNorth = Math.Max(maximumNorth, stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id).LocalNorthMetres ?? 0d);
        }

        var settled = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.InRange(settled.LocalNorthMetres ?? 0d, targetDistanceMetres - 0.25d, targetDistanceMetres + 0.25d);
        Assert.InRange(maximumNorth, targetDistanceMetres - 0.25d, targetDistanceMetres + 0.75d);
    }

    [Fact]
    public async Task FormationTracking_BrakesAndSettlesWhenAMovingTransformReachesItsFinalHold()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync();
        var initial = stores.Telemetry.Items.Single(item => item.VehicleId == ghost.Id);
        var latitude = Assert.IsType<double>(initial.LatitudeDegrees);
        var longitude = Assert.IsType<double>(initial.LongitudeDegrees);
        var altitude = Assert.IsType<double>(initial.AltitudeAglMetres);
        const double earthRadiusMetres = 6378137d;
        const double targetDistanceMetres = 8d;
        var targetLatitude = latitude + targetDistanceMetres / earthRadiusMetres * 180d / Math.PI;

        // Simulate the final part of a translation + rotation where the
        // target has a significant tangential/feed-forward velocity.
        await service.SetFormationTargetAsync(ghost.Id, new GhostFormationTarget(
            "formation-brake", targetLatitude, longitude, altitude, 7d, 0d, 0d));
        await Task.Delay(850);

        // The formation workflow must then publish a stationary final hold.
        await service.SetFormationTargetAsync(ghost.Id, new GhostFormationTarget(
            "formation-brake", targetLatitude, longitude, altitude, 0d, 0d, 0d));

        var maximumNorth = double.NegativeInfinity;
        VehicleTelemetryRecord? settled = null;
        var stableSamples = 0;
        var settleDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < settleDeadline && stableSamples < 3)
        {
            await Task.Delay(50);
            settled = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
            maximumNorth = Math.Max(maximumNorth, settled.LocalNorthMetres ?? 0d);

            var isSettled = (settled.LocalNorthMetres ?? 0d) is >= targetDistanceMetres - 0.5d and <= targetDistanceMetres + 0.5d &&
                             Math.Abs(settled.VelocityNorthMetresPerSecond ?? 0d) <= 0.3d &&
                             Math.Abs(settled.VelocityEastMetresPerSecond ?? 0d) <= 0.3d;
            stableSamples = isSettled ? stableSamples + 1 : 0;
        }

        settled ??= stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.InRange(settled.LocalNorthMetres ?? 0d, targetDistanceMetres - 0.5d, targetDistanceMetres + 0.5d);
        Assert.InRange(maximumNorth, 0d, targetDistanceMetres + 0.1d);
        Assert.InRange(Math.Abs(settled.VelocityNorthMetresPerSecond ?? 0d), 0d, 0.3d);
        Assert.InRange(Math.Abs(settled.VelocityEastMetresPerSecond ?? 0d), 0d, 0.3d);
    }

    [Fact]
    public async Task Stop_IsIdempotentWhenHostAndContainerDisposeBothStopTheService()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualControl_UsesBodyVelocityAndPublishesManualTelemetry()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync();
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);
        await service.ExecuteAsync(new OperatorCommandRequest(
            "takeoff-manual", "corr", "idem", OperatorCommandKind.Takeoff, target, "test", false, DateTimeOffset.UtcNow,
            Parameters: new OperatorCommandParameters(TakeoffAltitudeAglMetres: 1)));
        await Task.Delay(650);

        Assert.True(await service.BeginManualControlAsync(ghost.Id, "manual-test"));
        service.UpdateManualControl(ghost.Id, "manual-test", new ManualControlSetpoint(3, 0, 0, 0, true, DateTimeOffset.UtcNow));
        await Task.Delay(450);

        var telemetry = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.Equal("Manual control", telemetry.AdapterState);
        Assert.True(telemetry.VelocityNorthMetresPerSecond.GetValueOrDefault() > 0);
        await service.EndManualControlAsync(ghost.Id, "manual-test", "test complete");
        telemetry = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.NotEqual("Manual control", telemetry.AdapterState);
    }

    [Fact]
    public async Task ManualControl_DoesNotAutoDisarmALandedGhost()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync();
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);

        Assert.True(await service.BeginManualControlAsync(ghost.Id, "manual-arm-test"));
        await service.ExecuteAsync(new OperatorCommandRequest(
            "arm-manual", "corr", "idem", OperatorCommandKind.Arm, target,
            "test", false, DateTimeOffset.UtcNow));
        await Task.Delay(150);

        var telemetry = stores.Telemetry.Items.ToArray().Single(item => item.VehicleId == ghost.Id);
        Assert.True(telemetry.Armed);
        Assert.Equal("Landed", telemetry.LandedState);
    }

    [Fact]
    public async Task MissionRequiresArmAndRunsWithSimulatedCapture()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync(new MapViewportSnapshot(-79.42, 43.73, 20, 0));
        var artifact = new FlightMissionExecutionArtifact(
            "ghost-mission",
            "Ghost simulator",
            [
                new(0, "takeoff", FlightMissionStepKind.Takeoff, null, 0.4, 5),
                new(1, "camera", FlightMissionStepKind.CameraCaptureIntent, null, 0.4, 5, SimulatedCapture: true),
                new(2, "land", FlightMissionStepKind.Land, null, 0, 5)
            ],
            "test-hash");

        Assert.False((await service.StartMissionAsync(ghost.Id)).Succeeded);
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);
        await service.ExecuteAsync(new OperatorCommandRequest(
            "arm-for-mission", "corr", "idem", OperatorCommandKind.Arm, target,
            "test", false, DateTimeOffset.UtcNow));

        Assert.True((await service.PrepareMissionAsync(ghost.Id, artifact)).Succeeded);
        Assert.True((await service.StartMissionAsync(ghost.Id)).Succeeded);
        await Task.Delay(800);

        Assert.True(service.TryGetMissionProgress(ghost.Id, out var progress));
        Assert.Equal(FlightMissionExecutionState.Completed, progress.State);
        Assert.Contains(progress.CaptureEvents ?? [], item => item.StepId == "camera");
        Assert.False(stores.Telemetry.Items.Single(item => item.VehicleId == ghost.Id).Armed);
    }

    [Fact]
    public async Task MissionPauseAndResumePreserveProgress()
    {
        var stores = new Stores();
        await using var service = stores.CreateService();
        await service.StartAsync(CancellationToken.None);
        var ghost = await service.CreateAsync(new MapViewportSnapshot(-79.42, 43.73, 20, 0));
        var target = new OperatorCommandTarget("ghost-connection-1", ghost.Id, null, DateTimeOffset.UtcNow);
        await service.ExecuteAsync(new OperatorCommandRequest("arm", "corr", "idem", OperatorCommandKind.Arm, target, "test", false, DateTimeOffset.UtcNow));
        var artifact = new FlightMissionExecutionArtifact("pause-mission", "Ghost simulator", [new(0, "takeoff", FlightMissionStepKind.Takeoff, null, 5, 1)], "hash");
        await service.PrepareMissionAsync(ghost.Id, artifact);
        await service.StartMissionAsync(ghost.Id);
        await Task.Delay(200);
        Assert.True((await service.PauseMissionAsync(ghost.Id)).Succeeded);
        Assert.True(service.TryGetMissionProgress(ghost.Id, out var paused));
        Assert.Equal(FlightMissionExecutionState.Paused, paused.State);
        Assert.True((await service.ResumeMissionAsync(ghost.Id)).Succeeded);
        Assert.True(service.TryGetMissionProgress(ghost.Id, out var resumed));
        Assert.True(resumed.CurrentItemIndex >= paused.CurrentItemIndex);
    }

    private sealed class Stores
    {
        public EntityStore<string, ConnectionRecord> Connections { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, RuntimeRecord> Runtimes { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleRecord> Vehicles { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleTelemetryRecord> Telemetry { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, LinkRecord> Links { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, CameraSourceRecord> CameraSources { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, CameraStreamRecord> CameraStreams { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, OperationalCommandRecord> Commands { get; } = new(item => item.Id, StringComparer.Ordinal);
        public SelectionService Selection { get; } = new();

        public GhostUnitService CreateService(IGhostProfileWorkflow? profiles = null)
            => new(Connections, Runtimes, Vehicles, Telemetry, Commands, Selection,
                new ImmediateDispatcher(), new AppConfiguration(), links: Links,
                cameraSources: CameraSources, cameraStreams: CameraStreams, profiles: profiles);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
