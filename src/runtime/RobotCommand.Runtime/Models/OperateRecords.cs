namespace RobotCommand.Models;

public enum MapFrameKind
{
    Unknown,
    GlobalWgs84,
    LocalEnu,
    LocalNed
}

public readonly record struct OperationalPoint(double X, double Y, double Z = 0);

public sealed record GeometryOverlayRecord(
    string Id,
    string GeometryId,
    string ConnectionId,
    string? LogosInstanceId,
    string Name,
    string Kind,
    MapFrameKind Frame,
    bool Closed,
    IReadOnlyList<OperationalPoint> Points,
    IReadOnlyList<IReadOnlyList<OperationalPoint>> Rings,
    string PolicyKind,
    string PolicyConstraint,
    DateTimeOffset ObservedAt);

public sealed record PerceptionTrackRecord(
    string Id,
    string TrackId,
    string ConnectionId,
    string? LogosInstanceId,
    string ClassId,
    double Confidence,
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    int AgeFrames,
    int HitCount,
    int MissCount,
    string? CameraSourceId,
    string? FrameId,
    DateTimeOffset ObservedAt);

public sealed record CameraSourceRecord(
    string Id,
    string CameraSourceId,
    string ConnectionId,
    string? LogosInstanceId,
    string Name,
    string Kind,
    AvailabilityState State,
    string Health,
    string Readiness,
    bool Active,
    bool Fresh,
    bool HasImage,
    double FrameRateHz,
    double LatencyMilliseconds,
    uint Width,
    uint Height,
    string FrameId,
    string Code,
    string Message,
    DateTimeOffset ObservedAt,
    bool SupportsPhoto = false,
    bool SupportsVideo = false,
    bool SupportsGimbal = false,
    byte? GimbalComponentId = null);

/// <summary>
/// Normalized camera-definition data discovered through the MAVLink Camera
/// Protocol. The definition is runtime state only; camera settings are not
/// copied into mission documents or vehicle parameters.
/// </summary>
public sealed record MavlinkCameraDefinitionRecord(
    string Id,
    string ConnectionId,
    byte SystemId,
    byte ComponentId,
    uint CapabilityFlags,
    string VendorName,
    string ModelName,
    string FirmwareVersion,
    ushort DefinitionVersion,
    string DefinitionUri,
    IReadOnlyList<MavlinkCameraSettingRecord> Settings,
    DateTimeOffset ObservedAt,
    string Status = "Discovered")
{
    public string DisplayName => string.Join(" ", new[] { VendorName, ModelName }
        .Where(item => !string.IsNullOrWhiteSpace(item))).Trim();

    public string CapabilitySummary => string.Join(", ", new[]
    {
        (CapabilityFlags & 2) != 0 ? "Photo" : null,
        (CapabilityFlags & 1) != 0 ? "Video" : null,
        (CapabilityFlags & 4) != 0 ? "Modes" : null
    }.Where(item => item is not null));
}

public sealed record MavlinkCameraSettingRecord(
    string Name,
    string Label,
    string Type,
    string Units,
    string? CurrentValue = null,
    string? DefaultValue = null,
    double? Minimum = null,
    double? Maximum = null,
    double? Increment = null,
    IReadOnlyList<MavlinkCameraSettingOption>? Options = null)
{
    public IReadOnlyList<MavlinkCameraSettingOption> Values => Options ?? [];
    public string Summary => CurrentValue ?? DefaultValue ??
        (Values.Count > 0
            ? string.Join(" / ", Values.Select(item => item.Label))
            : Minimum is { } minimum && Maximum is { } maximum
                ? $"{minimum:0.###}–{maximum:0.###} {Units}"
                : Type);
}

public sealed record MavlinkCameraSettingOption(string Value, string Label);

public enum VideoProtocolPreference
{
    Automatic,
    WebRtc,
    Rtsp,
    Mjpeg,
    WebSocket,
    Hls
}

public sealed record CameraStreamOpenRequest(
    string CameraSourceId,
    VideoProtocolPreference Protocol = VideoProtocolPreference.Automatic,
    uint Width = 0,
    uint Height = 0,
    double FrameRateHz = 0,
    uint BitrateKbps = 0,
    string Codec = "",
    string NegotiationPayload = "");

public sealed record CameraStreamRecord(
    string Id,
    string StreamId,
    string CameraSourceId,
    string ConnectionId,
    string? LogosInstanceId,
    string Protocol,
    string State,
    string StreamUrl,
    string NegotiationPayload,
    string Codec,
    uint Width,
    uint Height,
    double FrameRateHz,
    uint BitrateKbps,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? ExpiresAt,
    string Code,
    string Message,
    DateTimeOffset ObservedAt);

public enum OperateLayoutMode
{
    MapFocus,
    Split,
    VideoFocus,
    UnitFocus
}

public enum MapViewportMode
{
    FitAll,
    FollowSelected
}

public enum MapOrientationMode
{
    NorthUp,
    CourseUp
}

public sealed record MapVehicleVisual(
    string VehicleId,
    string Name,
    double X,
    double Y,
    double? HeadingDegrees,
    AvailabilityState State,
    bool Selected,
    bool IsGhost = false,
    bool TeamSelected = false)
{
    public MapCameraConeVisual? CameraCone { get; init; }

    public double? GimbalPitchDegrees { get; init; }

    public double? GimbalYawDegrees { get; init; }

    public bool? GimbalYawInEarthFrame { get; init; }
}

/// <summary>Presentation-only footprint of a vehicle-mounted camera view.</summary>
public sealed record MapCameraConeVisual(
    double HorizontalFieldOfViewDegrees = 55,
    double RangeMetres = 150);

public sealed record MapGeometryVisual(
    string GeometryId,
    string Name,
    string Kind,
    bool Closed,
    IReadOnlyList<OperationalPoint> Points,
    IReadOnlyList<IReadOnlyList<OperationalPoint>> Rings,
    string PolicyConstraint)
{
    public string ConnectionId { get; init; } = string.Empty;

    public string PolicyKind { get; init; } = "none";

    public bool Highlighted { get; init; }

    public bool IsPolicy =>
        !string.Equals(PolicyKind, "none", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(PolicyConstraint) &&
         !string.Equals(PolicyConstraint, "none", StringComparison.OrdinalIgnoreCase));
}

public sealed record MapVehicleTrailVisual(
    string VehicleId,
    string Name,
    string ConnectionId,
    IReadOnlyList<OperationalPoint> Points,
    AvailabilityState State,
    bool Selected);

public sealed record OperationalMapScene(
    MapFrameKind Frame,
    string FrameLabel,
    IReadOnlyList<MapVehicleVisual> Vehicles,
    IReadOnlyList<MapGeometryVisual> Geometries,
    string? SelectedVehicleId,
    MapViewportMode ViewportMode,
    bool GeometryVisible)
{
    public static OperationalMapScene Empty { get; } =
        new(MapFrameKind.Unknown, "No compatible position data", [], [], null, MapViewportMode.FitAll, true);

    public long NavigationRevision { get; init; }

    public IReadOnlyList<MapVehicleTrailVisual> Trails { get; init; } = [];

    public bool PolicyVisible { get; init; } = true;

    public bool TrailsVisible { get; init; } = true;

    public bool VehicleLabelsVisible { get; init; } = true;

    public MapOrientationMode OrientationMode { get; init; } = MapOrientationMode.NorthUp;

    public MapViewportSnapshot? RequestedViewport { get; init; }

    public MapNavigationRequest? NavigationRequest { get; init; }

    public MapFollowState? Follow { get; init; }

    public MapGoToTargetVisual? GoToTarget { get; init; }

    public IReadOnlyList<MapGoToTargetVisual> GoToTargets { get; init; } = [];

    public IReadOnlyList<MapGoToTargetVisual> FormationPreviewTargets { get; init; } = [];

    public IReadOnlyList<FormationPreviewPath> FormationPreviewPaths { get; init; } = [];

    public MapTeamPositionVisual? LockedTeamPosition { get; init; }

    public OperatorLocationSnapshot OperatorLocation { get; init; } =
        OperatorLocationSnapshot.Unavailable();

    public bool HasData =>
        Vehicles.Count > 0 ||
        Trails.Any(item => item.Points.Count > 0) ||
        Geometries.Any(item =>
            item.Points.Count > 0 ||
            item.Rings.Any(ring => ring.Count > 0));
}

public sealed record VideoTrackVisual(
    string TrackId,
    string ClassId,
    double Confidence,
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    bool Selected = false);

public sealed record VideoOverlayScene(
    string? CameraSourceId,
    uint Width,
    uint Height,
    IReadOnlyList<VideoTrackVisual> Tracks)
{
    public static VideoOverlayScene Empty { get; } = new(null, 0, 0, []);
}

public enum VideoPlaybackState
{
    Detached,
    SessionReady,
    Connecting,
    Live,
    Degraded,
    Reconnecting,
    Fallback,
    Offline,
    Unsupported,
    AdapterUnavailable,
    Faulted
}

public sealed record VideoPlaybackStatus(
    VideoPlaybackState State,
    string Summary,
    string Detail,
    string? StreamId = null,
    string? Protocol = null,
    string? Endpoint = null,
    int ReconnectAttempt = 0,
    DateTimeOffset? UpdatedAt = null)
{
    public static VideoPlaybackStatus Detached { get; } =
        new(
            VideoPlaybackState.Detached,
            "No stream",
            "Open a camera stream to start playback.",
            UpdatedAt: DateTimeOffset.UtcNow);

    public bool Active => State is
        VideoPlaybackState.SessionReady or
        VideoPlaybackState.Connecting or
        VideoPlaybackState.Live or
        VideoPlaybackState.Degraded or
        VideoPlaybackState.Reconnecting or
        VideoPlaybackState.Fallback;
}
