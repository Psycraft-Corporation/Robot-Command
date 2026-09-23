using System.Collections.Concurrent;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Rendering;
using RobotCommand.State;

namespace RobotCommand.Services.Media;

public sealed record GhostCameraStreamSession(CameraStreamRecord Stream, IVideoFrameSource Frames);

public interface IGhostCameraStreamProvider
{
    Task<GhostCameraStreamSession> OpenAsync(CameraSourceRecord camera, CancellationToken cancellationToken = default);
    Task CloseAsync(CameraStreamRecord stream, CancellationToken cancellationToken = default);
}

/// <summary>Produces unit-scoped Ghost video from the current shared 3D world scene.</summary>
public sealed class GhostCameraStreamProvider : IGhostCameraStreamProvider, IAsyncDisposable
{
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, CameraSourceRecord> _cameraSources;
    private readonly IEntityStore<string, CameraStreamRecord> _streams;
    private readonly IThreeDSceneWorkflow _scene;
    private readonly IUiDispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public GhostCameraStreamProvider(
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, CameraSourceRecord> cameraSources,
        IEntityStore<string, CameraStreamRecord> streams,
        IThreeDSceneWorkflow scene,
        IUiDispatcher dispatcher)
    {
        _vehicles = vehicles;
        _telemetry = telemetry;
        _cameraSources = cameraSources;
        _streams = streams;
        _scene = scene;
        _dispatcher = dispatcher;
    }

    public async Task<GhostCameraStreamSession> OpenAsync(CameraSourceRecord camera, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        cancellationToken.ThrowIfCancellationRequested();
        if (!camera.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal) || camera.DeviceState is null)
            throw new InvalidOperationException("The selected camera is not a configured Ghost camera.");
        var vehicle = _vehicles.Items.FirstOrDefault(item => item.IsGhost && item.ConnectionIds.Contains(camera.ConnectionId, StringComparer.Ordinal))
            ?? throw new InvalidOperationException("The Ghost camera is not associated with an available Ghost unit.");
        var telemetry = FindTelemetry(vehicle) ?? throw new InvalidOperationException("The Ghost unit has no current position telemetry.");
        var state = camera.DeviceState;
        var width = checked((int)(state.VideoWidth == 0 ? GhostCameraDefaults.DefaultWidth : state.VideoWidth));
        var height = checked((int)(state.VideoHeight == 0 ? GhostCameraDefaults.DefaultHeight : state.VideoHeight));
        var rate = Math.Clamp(state.VideoFramesPerSecond, 1u, 60u);
        var now = DateTimeOffset.UtcNow;
        var id = $"ghost-stream-{Guid.NewGuid():N}";
        var stream = new CameraStreamRecord(id, id, camera.CameraSourceId, camera.ConnectionId, camera.LogosInstanceId,
            "Ghost3D", "Opening", string.Empty, string.Empty, "BGRA", (uint)width, (uint)height, rate, 0,
            now, null, "GHOST_STREAM_OPENING", "Rendering the Ghost camera view.", now);
        var session = new Session(stream, new VideoFrameBuffer(), new CancellationTokenSource());
        if (!_sessions.TryAdd(id, session)) throw new InvalidOperationException("Could not allocate a Ghost video session.");
        await PublishAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            RenderFrame(session, camera, vehicle, telemetry, width, height);
            await UpdateStreamAsync(session, "Playing", "GHOST_STREAM_PLAYING", "Ghost camera view is live.").ConfigureAwait(false);
            session.Producer = ProduceAsync(session, camera, vehicle, width, height, rate);
            return new(session.Stream, session.Frames);
        }
        catch (Exception ex)
        {
            await UpdateStreamAsync(session, "Faulted", "GHOST_STREAM_RENDER_FAILED", ex.Message).ConfigureAwait(false);
            session.Cancellation.Dispose();
            _sessions.TryRemove(id, out _);
            throw;
        }
    }

    public async Task CloseAsync(CameraStreamRecord stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryRemove(stream.Id, out var session)) return;
        session.Cancellation.Cancel();
        if (session.Producer is not null)
        {
            try { await session.Producer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        session.Frames.Clear();
        await UpdateStreamAsync(session, "Closed", "GHOST_STREAM_CLOSED", "Ghost camera stream closed.").ConfigureAwait(false);
        session.Cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values.ToArray())
            await CloseAsync(session.Stream).ConfigureAwait(false);
    }

    private async Task ProduceAsync(Session session, CameraSourceRecord camera, VehicleRecord vehicle, int width, int height, uint rate)
    {
        var period = TimeSpan.FromSeconds(1d / rate);
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(session.Cancellation.Token).ConfigureAwait(false))
            {
                var telemetry = FindTelemetry(vehicle);
                var currentCamera = _cameraSources.Items.FirstOrDefault(item => item.Id == camera.Id) ?? camera;
                if (telemetry is null) throw new InvalidOperationException("Ghost camera telemetry is no longer available.");
                RenderFrame(session, currentCamera, vehicle, telemetry, width, height);
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await UpdateStreamAsync(session, "Faulted", "GHOST_STREAM_RENDER_FAILED", ex.Message).ConfigureAwait(false);
        }
    }

    private void RenderFrame(Session session, CameraSourceRecord camera, VehicleRecord vehicle, VehicleTelemetryRecord telemetry, int width, int height)
    {
        var world = _scene.Current;
        var cameraPose = CreateCameraPose(world, telemetry, camera.DeviceState, width, height);
        var frameScene = world with { Camera = cameraPose };
        var pixels = ThreeDSceneFrameRenderer.Render(frameScene, width, height, $"world:unit:{vehicle.Id}");
        session.Frames.Publish(pixels, width, height, checked(width * 4), DateTimeOffset.UtcNow);
    }

    public static ThreeDCameraSnapshot CreateCameraPose(
        ThreeDSceneSnapshot world,
        VehicleTelemetryRecord telemetry,
        CameraDeviceStateSnapshot? state,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var gimbal = state?.Gimbal;
        var position = ThreeDSceneMath.ToLocal(
            telemetry.LatitudeDegrees ?? world.Origin.LatitudeDegrees,
            telemetry.LongitudeDegrees ?? world.Origin.LongitudeDegrees,
            telemetry.AltitudeMslMetres ?? telemetry.AltitudeAglMetres ?? 0,
            world.Origin);
        var zoom = Math.Clamp(state?.ZoomMagnification ?? 1, 1, 10);
        const double baseHorizontalFov = 55;
        var verticalFov = 2 * Math.Atan(Math.Tan(baseHorizontalFov * Math.PI / 360) * height / width / zoom) * 180 / Math.PI;
        // Vehicle heading and gimbal yaw are compass-clockwise, while the 3D
        // projection's positive camera yaw turns counter-clockwise around +Z.
        var yaw = NormalizeHeading(-((telemetry.HeadingDegrees ?? 0) + (gimbal?.YawDegrees ?? telemetry.GimbalYawDegrees ?? 0)));
        return new(position, yaw,
            -(gimbal?.PitchDegrees ?? telemetry.GimbalPitchDegrees ?? 0),
            gimbal?.RollDegrees ?? telemetry.GimbalRollDegrees ?? 0,
            verticalFov, 0.1, 10000, 0);
    }

    private VehicleTelemetryRecord? FindTelemetry(VehicleRecord vehicle)
        => _telemetry.Items.FirstOrDefault(item => item.IsGhost && item.VehicleId == vehicle.Id)
           ?? _telemetry.Items.FirstOrDefault(item => item.VehicleId == vehicle.Id);

    private async Task UpdateStreamAsync(Session session, string state, string code, string message)
    {
        session.Stream = session.Stream with { State = state, Code = code, Message = message, ObservedAt = DateTimeOffset.UtcNow };
        await PublishAsync(session.Stream, CancellationToken.None).ConfigureAwait(false);
    }

    private Task PublishAsync(CameraStreamRecord stream, CancellationToken cancellationToken)
        => _dispatcher.InvokeAsync(() =>
            _streams.ReplaceAll(_streams.Items.Where(item => item.ConnectionId != stream.ConnectionId || item.Id != stream.Id).Append(stream)),
            cancellationToken);

    private static double NormalizeHeading(double degrees)
    {
        degrees %= 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private sealed class Session(CameraStreamRecord stream, VideoFrameBuffer frames, CancellationTokenSource cancellation)
    {
        public CameraStreamRecord Stream { get; set; } = stream;
        public VideoFrameBuffer Frames { get; } = frames;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Producer { get; set; }
    }
}
