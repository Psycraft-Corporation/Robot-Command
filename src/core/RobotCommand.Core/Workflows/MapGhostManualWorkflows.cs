namespace RobotCommand.Core;

/// <summary>UI-neutral multi-selection state shared by desktop and terminal front ends.</summary>
public sealed record SelectionWorkflowSnapshot(
    string? PrimaryUnitId,
    IReadOnlyList<string> UnitIds,
    string? AnchorUnitId,
    string Kind = "None",
    string? EntityId = null);

public interface ISelectionWorkflow
{
    event EventHandler? Changed;
    SelectionWorkflowSnapshot Current { get; }
    Task SetUnitsAsync(IReadOnlyList<string> unitIds, string? anchorUnitId = null, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record MapViewportWorkflowSnapshot(
    double LongitudeDegrees,
    double LatitudeDegrees,
    double ResolutionMetresPerPixel,
    double RotationDegrees);

public sealed record MapStyleWorkflowSnapshot(string Id, string Name, string Kind, bool Selected);

public sealed record MapOverlayWorkflowSnapshot(
    bool GeometriesVisible,
    bool PolicyVisible,
    bool TrailsVisible,
    bool DestinationsVisible,
    bool LabelsVisible);

public sealed record SavedMapViewWorkflowSnapshot(
    string Id,
    string Name,
    string? PackageKey,
    string? StyleId,
    MapViewportWorkflowSnapshot Viewport,
    string ViewportMode,
    string OrientationMode,
    MapOverlayWorkflowSnapshot Overlays,
    IReadOnlyList<string> SelectedUnitIds,
    string? SelectionAnchorUnitId,
    string LegacySelectionKind,
    string? LegacySelectionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MapViewWorkflowSnapshot(
    MapViewportWorkflowSnapshot? Viewport,
    IReadOnlyList<MapStyleWorkflowSnapshot> Styles,
    string? SelectedStyleId,
    MapOverlayWorkflowSnapshot Overlays,
    bool IsFollowing,
    IReadOnlyList<string> FollowUnitIds,
    string FollowLabel,
    string Status);

public sealed record MapSceneSnapshot(
    string Frame,
    string FrameLabel,
    MapViewportWorkflowSnapshot? Viewport,
    string? StyleId,
    string StyleLabel,
    MapOverlayWorkflowSnapshot Overlays,
    SelectionWorkflowSnapshot Selection,
    IReadOnlyList<MapSceneUnitSnapshot> Units,
    IReadOnlyList<MapSceneTrailSnapshot> Trails,
    IReadOnlyList<MapSceneDestinationSnapshot> Destinations,
    IReadOnlyList<MapSceneGeometrySnapshot> Geometries,
    IReadOnlyList<MapSceneFormationPathSnapshot> FormationPreviewPaths,
    MapSceneOperatorLocationSnapshot? OperatorLocation,
    bool HasFormationPreview,
    DateTimeOffset CapturedAt)
{
    public TeamSelectionWorkflowSnapshot TeamSelection { get; init; } = TeamSelectionWorkflowSnapshot.Empty;
    public FormationLockSnapshot FormationLock { get; init; } = FormationLockSnapshot.Unlocked;
}

public sealed record MapSceneUnitSnapshot(
    string UnitId, string Name, double X, double Y, double? HeadingDegrees, string State, bool Selected, bool IsGhost)
{
    /// <summary>True when the unit is highlighted because its Team is selected.</summary>
    public bool TeamSelected { get; init; }
}

public sealed record MapSceneTrailSnapshot(string UnitId, IReadOnlyList<MapScenePoint> Points, string State, bool Selected);
public sealed record MapSceneDestinationSnapshot(string UnitId, double LongitudeDegrees, double LatitudeDegrees, bool Active, bool Preview);
public sealed record MapSceneGeometrySnapshot(string Id, string Name, string Kind, bool IsPolicy, bool Highlighted);
public sealed record MapSceneFormationPathSnapshot(IReadOnlyList<MapScenePoint> Points, bool Closed);
public sealed record MapSceneOperatorLocationSnapshot(double LatitudeDegrees, double LongitudeDegrees, double? AccuracyMetres, DateTimeOffset? ObservedAt);
public sealed record MapScenePoint(double X, double Y);

public interface IMapViewWorkflow
{
    event EventHandler? Changed;
    MapViewWorkflowSnapshot Current { get; }
    IReadOnlyList<SavedMapViewWorkflowSnapshot> SavedViews { get; }
    Task SetViewportAsync(MapViewportWorkflowSnapshot viewport, CancellationToken cancellationToken = default);
    Task SelectStyleAsync(string styleId, CancellationToken cancellationToken = default);
    Task SetOverlayVisibleAsync(string overlay, bool visible, CancellationToken cancellationToken = default);
    Task JumpToSelectedAsync(CancellationToken cancellationToken = default);
    Task JumpToOperatorAsync(CancellationToken cancellationToken = default);
    Task FollowSelectedAsync(CancellationToken cancellationToken = default);
    Task StopFollowingAsync(CancellationToken cancellationToken = default);
    Task NotifyUserPannedAsync(CancellationToken cancellationToken = default);
    Task<SavedMapViewWorkflowSnapshot> SaveViewAsync(string name, CancellationToken cancellationToken = default);
    Task ApplySavedViewAsync(string id, CancellationToken cancellationToken = default);
    Task RemoveSavedViewAsync(string id, CancellationToken cancellationToken = default);
}

public interface IMapSceneObservationWorkflow
{
    event EventHandler? Changed;
    MapSceneSnapshot Current { get; }
}

public sealed record GhostUnitSnapshot(
    string UnitId,
    string Name,
    string ConnectionId,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeAglMetres,
    double HeadingDegrees,
    string VehicleState,
    string? ActiveOperation)
{
    public string ProfileId { get; init; } = "dracula";
    public string? ProfileName { get; init; }
    public string? ProfileModel { get; init; }
}

public sealed record GhostCreateRequest(
    string ProfileId,
    int Count = 1,
    double? LatitudeDegrees = null,
    double? LongitudeDegrees = null,
    double HeadingDegrees = 90);

public interface IGhostUnitWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<GhostUnitSnapshot> Ghosts { get; }
    Task<IReadOnlyList<GhostUnitSnapshot>> CreateAsync(GhostCreateRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(string unitId, CancellationToken cancellationToken = default);
}

public enum ManualControlOwnerKind { None, Gui, Cli }

public sealed record ManualInputDeviceSnapshot(string Id, string Name, bool IsConnected, bool Selected, string Kind = "XboxGamepad", int AxisCount = 0, int ButtonCount = 0);
public sealed record ManualJoystickMappingSnapshot(
    int ForwardAxis, bool ForwardInverted,
    int RightAxis, bool RightInverted,
    int VerticalAxis, bool VerticalInverted,
    int YawAxis, bool YawInverted,
    int DeadmanButton, int ArmButton, int TakeoffButton, int ExecuteButton, int CancelButton, int ReleaseButton);
public sealed record ManualControlProfileSnapshot(
    double DeadZone, double Expo, double MaximumHorizontalSpeedMetresPerSecond,
    double MaximumVerticalSpeedMetresPerSecond, double MaximumYawRateDegreesPerSecond,
    double HorizontalAccelerationMetresPerSecondSquared, double VerticalAccelerationMetresPerSecondSquared,
    double YawAccelerationDegreesPerSecondSquared, double TakeoffAltitudeAglMetres,
    IReadOnlyDictionary<string, ManualJoystickMappingSnapshot>? JoystickMappings = null);
public sealed record ManualControlProfileEntrySnapshot(
    string Id,
    string Name,
    ManualControlProfileSnapshot Settings,
    bool IsActive);
public sealed record ManualControlReadingSnapshot(
    double LeftX, double LeftY, double RightX, double RightY, bool DeadmanPressed, DateTimeOffset Timestamp,
    IReadOnlyList<double>? RawAxes = null);
public sealed record ManualControlSessionWorkflowSnapshot(
    string State, string Backend, string? TargetUnitId, string? TargetName,
    string? DeviceId, string? DeviceName, ManualControlOwnerKind OwnerKind, string? OwnerId,
    bool IsActive, bool DeadmanPressed, bool InputNeutral, string Status, string? PendingButtonAction,
    DateTimeOffset? PendingButtonExpiresAt, double InputRateHertz, DateTimeOffset? StartedAt, DateTimeOffset? LastInputAt,
    string? AutopilotMode = null, string? ModeClass = null, string? AdmissionStatus = null,
    double? TransportInputRateHertz = null, DateTimeOffset? LastInputSentAt = null,
    bool? InputEchoAvailable = null, string? SafeReleaseMode = null, bool? SafeReleaseConfirmed = null,
    string? InterruptionReason = null);

public interface IManualControlProfileWorkflow
{
    IReadOnlyList<ManualControlProfileEntrySnapshot> Profiles { get; }
    string ActiveProfileId { get; }
    Task<ManualControlProfileEntrySnapshot> CreateProfileAsync(string name, ManualControlProfileSnapshot? settings = null, CancellationToken cancellationToken = default);
    Task<ManualControlProfileEntrySnapshot> SelectProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ManualControlProfileEntrySnapshot> UpdateProfileAsync(string profileId, string? name, ManualControlProfileSnapshot? settings, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default);
}

public interface IManualControlWorkflow : IManualControlProfileWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<ManualInputDeviceSnapshot> Devices { get; }
    ManualControlProfileSnapshot Profile { get; }
    ManualControlSessionWorkflowSnapshot Session { get; }
    ManualControlReadingSnapshot? LatestReading { get; }
    Task SelectDeviceAsync(string? deviceId, CancellationToken cancellationToken = default);
    Task SaveProfileAsync(ManualControlProfileSnapshot profile, CancellationToken cancellationToken = default);
    Task<bool> TakeControlAsync(string unitId, ManualControlOwnerKind ownerKind, string ownerId, CancellationToken cancellationToken = default);
    Task ReleaseAsync(ManualControlOwnerKind ownerKind, string ownerId, string reason = "Operator released manual control.", CancellationToken cancellationToken = default);
    void NotifyHostActivity(bool active);
}
