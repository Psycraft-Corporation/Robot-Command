namespace RobotCommand.Core;

public enum ThreeDRenderBackendPolicy
{
    Auto,
    Hardware,
    Software
}

public enum ThreeDPrimitiveKind
{
    Point,
    Cube,
    Sphere,
    Arrow,
    Marker,
    GroundPlane
}

public sealed record ThreeDVector3(double X, double Y, double Z)
{
    public static ThreeDVector3 Zero { get; } = new(0, 0, 0);
}

public sealed record ThreeDTransform(
    ThreeDVector3 Position,
    ThreeDVector3 RotationDegrees,
    ThreeDVector3 Scale)
{
    public static ThreeDTransform Identity { get; } = new(ThreeDVector3.Zero, ThreeDVector3.Zero, new(1, 1, 1));
}

public sealed record ThreeDPrimitiveSnapshot(
    string Id,
    ThreeDPrimitiveKind Kind,
    ThreeDTransform Transform,
    string Color,
    string? Label = null,
    bool Selected = false,
    bool TeamSelected = false,
    bool IsTarget = false);

public sealed record ThreeDLineSnapshot(
    string Id,
    IReadOnlyList<ThreeDVector3> Points,
    string Color,
    double Width = 1,
    bool Closed = false,
    bool Selected = false,
    bool TeamSelected = false,
    bool IsTarget = false);

public sealed record ThreeDCameraSnapshot(
    ThreeDVector3 Position,
    double YawDegrees,
    double PitchDegrees,
    double RollDegrees,
    double FieldOfViewDegrees,
    double NearClip,
    double FarClip,
    double MovementSpeed,
    bool OrbitMode = false,
    ThreeDVector3? OrbitTarget = null,
    double? OrbitDistance = null);

public sealed record ThreeDGridSnapshot(
    bool Visible = true,
    double SizeMetres = 500,
    double SpacingMetres = 10,
    string Color = "#2D3946");

public sealed record ThreeDAxisSnapshot(
    bool Visible = true,
    double LengthMetres = 50);

public sealed record ThreeDOriginSnapshot(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeMetres,
    DateTimeOffset CapturedAt);

public sealed record ThreeDHudSnapshot(
    string BackendLabel,
    string Status,
    string CameraLabel,
    double FramesPerSecond,
    double FrameTimeMilliseconds);

public sealed record ThreeDRendererStatus(
    string Backend,
    bool IsHardwareAccelerated,
    bool IsInitialized,
    bool IsDeviceLost,
    string? FallbackReason,
    double FramesPerSecond,
    double FrameTimeMilliseconds)
{
    public static ThreeDRendererStatus Uninitialized { get; } = new("None", false, false, false, null, 0, 0);
}

public sealed record ThreeDSceneSnapshot(
    long Revision,
    DateTimeOffset CapturedAt,
    ThreeDOriginSnapshot Origin,
    ThreeDCameraSnapshot Camera,
    ThreeDGridSnapshot Grid,
    ThreeDAxisSnapshot Axes,
    IReadOnlyList<ThreeDPrimitiveSnapshot> Primitives,
    IReadOnlyList<ThreeDLineSnapshot> Lines,
    ThreeDHudSnapshot Hud,
    ThreeDRendererStatus RendererStatus);

public sealed record ThreeDWorldEntryRequest(
    double LongitudeDegrees,
    double LatitudeDegrees,
    double ResolutionMetresPerPixel,
    double RotationDegrees = 0);

public interface IThreeDSceneWorkflow
{
    event EventHandler? Changed;
    ThreeDSceneSnapshot Current { get; }
    ThreeDRenderBackendPolicy BackendPolicy { get; }
    Task SetBackendPolicyAsync(ThreeDRenderBackendPolicy policy, CancellationToken cancellationToken = default);
    Task SetCameraAsync(ThreeDCameraSnapshot camera, CancellationToken cancellationToken = default);
    Task ResetCameraAsync(CancellationToken cancellationToken = default);
    Task FitSceneAsync(CancellationToken cancellationToken = default);
}

public interface IThreeDWorldSceneWorkflow : IThreeDSceneWorkflow
{
    Task ActivateAsync(ThreeDWorldEntryRequest request, CancellationToken cancellationToken = default);
}

public interface IThreeDRenderer : IAsyncDisposable
{
    ThreeDRendererStatus Status { get; }
    Task InitializeAsync(ThreeDRenderBackendPolicy policy, CancellationToken cancellationToken = default);
    Task ResizeAsync(int width, int height, CancellationToken cancellationToken = default);
    Task RenderAsync(ThreeDSceneSnapshot scene, CancellationToken cancellationToken = default);
}
