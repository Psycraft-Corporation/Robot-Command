using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Rendering;
using RobotCommand.Services.Media;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GhostCameraStreamProviderTests
{
    [Fact]
    public void CameraPoseUsesVehicleHeadingGimbalRollZoomAndConfiguredResolution()
    {
        var scene = Scene();
        var telemetry = Telemetry(heading: 90);
        var state = CameraState(zoom: 2, pitch: -30, yaw: 15, roll: 8);

        var pose = GhostCameraStreamProvider.CreateCameraPose(scene, telemetry, state, 960, 540);
        var zoomed = GhostCameraStreamProvider.CreateCameraPose(scene, telemetry, state with { ZoomMagnification = 4 }, 960, 540);

        Assert.Equal(255, pose.YawDegrees);
        Assert.Equal(30, pose.PitchDegrees);
        Assert.Equal(8, pose.RollDegrees);
        Assert.True(zoomed.FieldOfViewDegrees < pose.FieldOfViewDegrees);
        Assert.Equal(960u, state.VideoWidth);
        Assert.Equal(540u, state.VideoHeight);
        Assert.Equal(0, scene.Camera.RollDegrees); // Pose calculation does not mutate the world camera.
    }

    [Fact]
    public void CameraPoseUsesFreshTelemetryInsteadOfSlowCameraCapabilitySnapshot()
    {
        var scene = Scene();
        var staleCameraState = CameraState(pitch: 0, yaw: 0, roll: 0);
        var telemetry = Telemetry() with
        {
            GimbalPitchDegrees = -25,
            GimbalYawDegrees = 40,
            GimbalRollDegrees = 12
        };

        var pose = GhostCameraStreamProvider.CreateCameraPose(scene, telemetry, staleCameraState, 960, 540);

        Assert.Equal(25, pose.PitchDegrees);
        Assert.Equal(320, pose.YawDegrees);
        Assert.Equal(12, pose.RollDegrees);
    }

    [Fact]
    public void TelemetryInterpolationSmoothsGimbalAndVehicleAnglesAcrossWraparound()
    {
        var start = DateTimeOffset.UtcNow;
        var earlier = Telemetry(heading: 359) with
        {
            ObservedAt = start,
            GimbalPitchDegrees = 0,
            GimbalYawDegrees = 179,
            GimbalRollDegrees = 0
        };
        var later = earlier with
        {
            ObservedAt = start.AddMilliseconds(50),
            HeadingDegrees = 1,
            GimbalPitchDegrees = -30,
            GimbalYawDegrees = -179,
            GimbalRollDegrees = 20
        };

        var middle = GhostCameraStreamProvider.InterpolateTelemetry(earlier, later, start.AddMilliseconds(25));

        Assert.Equal(0, middle.HeadingDegrees);
        Assert.Equal(-15, middle.GimbalPitchDegrees);
        Assert.Equal(180, middle.GimbalYawDegrees);
        Assert.Equal(10, middle.GimbalRollDegrees);
        Assert.Equal(start.AddMilliseconds(25), middle.ObservedAt);
    }

    [Fact]
    public async Task OpenPublishesFramesAndCloseUpdatesSimulatedStreamLifecycle()
    {
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        vehicles.Upsert(new("ghost-1", "Ghost 1", ["ghost-connection-1"], null, null,
            "Multicopter", "Air", "ghost", AvailabilityState.Online, IsGhost: true));
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        telemetry.Upsert(Telemetry());
        var sources = new EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal);
        var source = new CameraSourceRecord("ghost-camera-record", "ghost-camera-1", "ghost-connection-1", null,
            "Ghost Camera", "simulated", AvailabilityState.Online, "Healthy", "Ready", true, true, false,
            0, 0, 960, 540, "frame", "", "", DateTimeOffset.UtcNow, true, true, true,
            DeviceState: CameraState());
        sources.Upsert(source);
        var streams = new EntityStore<string, CameraStreamRecord>(item => item.Id, StringComparer.Ordinal);
        var provider = new GhostCameraStreamProvider(vehicles, telemetry, sources, streams, new TestSceneWorkflow(Scene()), new InlineUiDispatcher());

        var session = await provider.OpenAsync(source);
        var copied = new byte[960 * 540 * 4];

        Assert.Equal("Ghost3D", session.Stream.Protocol);
        Assert.Equal("Playing", session.Stream.State);
        Assert.True(session.Frames.TryCopyLatest(copied, out var frame));
        Assert.Equal((960, 540), (frame!.Width, frame.Height));
        var firstSequence = frame.Sequence;
        await Task.Delay(80);
        Assert.True(session.Frames.LatestInfo!.Sequence > firstSequence);

        await provider.CloseAsync(session.Stream);

        Assert.Equal("Closed", Assert.Single(streams.Items).State);
        Assert.Null(session.Frames.LatestInfo);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task RenderingFailureIsReportedAsFaultedRatherThanSilentBlackVideo()
    {
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        vehicles.Upsert(new("ghost-1", "Ghost 1", ["ghost-connection-1"], null, null,
            "Multicopter", "Air", "ghost", AvailabilityState.Online, IsGhost: true));
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        telemetry.Upsert(Telemetry());
        var sources = new EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal);
        var source = new CameraSourceRecord("ghost-camera-record", "ghost-camera-1", "ghost-connection-1", null,
            "Ghost Camera", "simulated", AvailabilityState.Online, "Healthy", "Ready", true, true, false,
            0, 0, 640, 360, "frame", "", "", DateTimeOffset.UtcNow, true, true, true,
            DeviceState: CameraState() with { VideoWidth = 640, VideoHeight = 360 });
        sources.Upsert(source);
        var streams = new EntityStore<string, CameraStreamRecord>(item => item.Id, StringComparer.Ordinal);
        var invalidScene = Scene() with
        {
            Lines = [new("broken-line", null!, "#FFFFFF")]
        };
        var provider = new GhostCameraStreamProvider(vehicles, telemetry, sources, streams, new TestSceneWorkflow(invalidScene), new InlineUiDispatcher());

        await Assert.ThrowsAsync<NullReferenceException>(() => provider.OpenAsync(source));

        Assert.Equal("Faulted", Assert.Single(streams.Items).State);
        Assert.Equal("GHOST_STREAM_RENDER_FAILED", streams.Items[0].Code);
        await provider.DisposeAsync();
    }

    private static CameraDeviceStateSnapshot CameraState(double zoom = 1, double pitch = 0, double yaw = 0, double roll = 0)
        => new(FlightMissionCameraMode.Video, 960, 540, 30, GhostCameraDefaults.SupportedVideoFormats,
            zoom, 1, 10, false, 0, true, true, true,
            new(pitch, yaw, roll, pitch, yaw, roll, -90, 30, 180, 45, 60));

    private static VehicleTelemetryRecord Telemetry(double heading = 0)
        => new("ghost-telemetry-1", "ghost-1", "ghost-connection-1", null, AvailabilityState.Online,
            false, "Landed", "Hold", "Simulated", "Healthy", "Ready", 43.6532, -79.3832,
            10, 10, 0, 0, 0, 0, 0, 0, heading, false, "", "", DateTimeOffset.UtcNow, true);

    private static ThreeDSceneSnapshot Scene()
    {
        var now = DateTimeOffset.UtcNow;
        return new(1, now, new(43.6532, -79.3832, 0, now),
            new(new(0, 50, -80), 0, 20, 0, 60, 0.1, 10000, 1), new(false), new(false),
            [new("world:ground", ThreeDPrimitiveKind.GroundPlane,
                new(ThreeDVector3.Zero, ThreeDVector3.Zero, new(500, 1, 500)), "#173126")], [],
            new("test", "Ready", "World", 0, 0), new("Software", false, true, false, null, 0, 0));
    }

    private sealed class TestSceneWorkflow(ThreeDSceneSnapshot current) : IThreeDSceneWorkflow
    {
        public event EventHandler? Changed;
        public ThreeDSceneSnapshot Current { get; } = current;
        public ThreeDRenderBackendPolicy BackendPolicy => ThreeDRenderBackendPolicy.Software;
        public Task SetBackendPolicyAsync(ThreeDRenderBackendPolicy policy, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetCameraAsync(ThreeDCameraSnapshot camera, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetCameraAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FitSceneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
