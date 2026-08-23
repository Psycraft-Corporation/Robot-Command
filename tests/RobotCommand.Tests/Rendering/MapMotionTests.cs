using RobotCommand.Models;
using RobotCommand.Services.Operations;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapMotionTests
{
    [Fact]
    public void BuildMotion_UsesSameDisplayFrameAndProjectsVelocityForGlobalMap()
    {
        var builder = new OperationalMapSceneBuilder();
        var vehicle = new VehicleRecord(
            "alpha", "Alpha", ["conn-a"], null, null, "Multicopter", "Air", "test",
            AvailabilityState.Online);
        var sample = new VehicleTelemetryRecord(
            "telemetry-alpha", "alpha", "conn-a", null, AvailabilityState.Online,
            false, "OnGround", "Multicopter", "Ready", "Healthy", "Ready",
            43.65, -79.38, null, null, null, null, null, 10, 5, null, 90,
            false, "OK", string.Empty, DateTimeOffset.UtcNow, true);

        var motion = builder.BuildMotion(
            [vehicle], [sample], MapFrameKind.GlobalWgs84, DateTimeOffset.UtcNow);

        var rendered = Assert.Single(motion.Vehicles);
        Assert.Equal(MapFrameKind.GlobalWgs84, motion.Frame);
        Assert.Equal(-79.38, rendered.X, 6);
        Assert.Equal(43.65, rendered.Y, 6);
        Assert.True(rendered.VelocityXPerSecond is > 0 and < 0.001);
        Assert.True(rendered.VelocityYPerSecond is > 0 and < 0.001);
        Assert.Equal(90, rendered.HeadingDegrees);
    }

    [Fact]
    public void BuildMotion_UsesEastNorthLocalCoordinates()
    {
        var builder = new OperationalMapSceneBuilder();
        var vehicle = new VehicleRecord(
            "alpha", "Alpha", ["conn-a"], null, null, "Multicopter", "Air", "test",
            AvailabilityState.Online);
        var sample = new VehicleTelemetryRecord(
            "telemetry-alpha", "alpha", "conn-a", null, AvailabilityState.Online,
            false, "InAir", "Multicopter", "Ready", "Healthy", "Ready",
            null, null, null, null, 12, 34, -5, 3, 4, -1, 180,
            false, "OK", string.Empty, DateTimeOffset.UtcNow, true);

        var motion = builder.BuildMotion(
            [vehicle], [sample], MapFrameKind.LocalEnu, DateTimeOffset.UtcNow);

        var rendered = Assert.Single(motion.Vehicles);
        Assert.Equal(34, rendered.X);
        Assert.Equal(12, rendered.Y);
        Assert.Equal(4, rendered.VelocityXPerSecond);
        Assert.Equal(3, rendered.VelocityYPerSecond);
        Assert.Equal(1, rendered.VelocityZPerSecond);
    }

    [Fact]
    public void PresentationStateMotionUpdateDoesNotReplaceStructuralScene()
    {
        var state = new RobotCommand.Services.Maps.MapPresentationState();
        var scene = new OperationalMapScene(
            MapFrameKind.GlobalWgs84, "Global",
            [new MapVehicleVisual("alpha", "Alpha", -79.38, 43.65, 0, AvailabilityState.Online, false)],
            [], null, MapViewportMode.FitAll, true);
        var presentation = new OperationalMapPresentation(
            OperationalMapRendererKind.NativeGlobal, scene, null, false, "Map", "", "");
        state.Update(presentation, null, true);

        var motion = new MapVehicleMotionSnapshot(
            MapFrameKind.GlobalWgs84,
            [new MapVehicleMotionSample(
                "alpha", MapFrameKind.GlobalWgs84, -79.379, 43.651, 15,
                null, null, null, AvailabilityState.Online, "Multicopter", "InAir", false,
                DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);
        state.UpdateMotion(motion);

        Assert.Same(scene, state.Presentation.Scene);
        Assert.Same(motion, state.Presentation.Motion);
    }
}
