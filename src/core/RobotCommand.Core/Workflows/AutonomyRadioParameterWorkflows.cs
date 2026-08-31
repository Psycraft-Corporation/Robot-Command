namespace RobotCommand.Core;

/// <summary>
/// A short-lived, reviewed change. Plans are deliberately session-only: the
/// caller must explicitly execute a currently valid plan before a remote or
/// device mutation is made.
/// </summary>
public enum ReviewedOperationKind
{
    MissionPublish, TaskPublish, TaskAssign, MissionCommand, TaskCommand,
    BehaviourDeploy, BehaviourRemove, BehaviourBindingSet, BehaviourBindingClear,
    GeometryRemoteCreate, GeometryRemoteUpdate, GeometryRemoteDelete,
    AutonomyBundleImport, Px4ParameterApply, SikConfigure, SikPair,
    FlightMissionUpload, FlightMissionDownload, FlightMissionStart,
    FlightMissionPause, FlightMissionContinue, FlightMissionResume,
    FlightMissionRetain, FlightMissionRemove,
    Px4FenceUpload, Px4FenceDownload, Px4FenceClear,
    FenceUpload, FenceDownload, FenceClear,
    TeamFormationGoTo, TeamFormationAltitude, TeamFormationRotate, TeamFormationScale, TeamFormationHold,
    TeamFormationEntry,
    // Appended to preserve the numeric values of existing persisted operation kinds.
    ArduPilotParameterApply
}

public enum ReviewedOperationState { Ready, Executing, Succeeded, Cancelled, Expired, Failed, Unavailable }
public enum WorkflowFindingSeverity { Info, Warning, Blocking }

public sealed record WorkflowFinding(string Code, WorkflowFindingSeverity Severity, string Message);

public sealed record ReviewedOperationSnapshot(
    string Id,
    ReviewedOperationKind Kind,
    string Title,
    ReviewedOperationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<WorkflowFinding> Findings,
    IReadOnlyList<string> Impact,
    string Summary,
    IReadOnlyList<string> TargetIds)
{
    public bool CanExecute => State == ReviewedOperationState.Ready && ExpiresAt > DateTimeOffset.UtcNow &&
                              Findings.All(finding => finding.Severity != WorkflowFindingSeverity.Blocking);
}

public sealed record ReviewedOperationExecutionResult(
    string Id,
    ReviewedOperationState State,
    bool Succeeded,
    string Summary,
    IReadOnlyList<string> Details);

public interface IReviewedOperationWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<ReviewedOperationSnapshot> Operations { get; }
    bool TryGet(string id, out ReviewedOperationSnapshot? operation);
    Task<ReviewedOperationExecutionResult> ExecuteAsync(string id, CancellationToken cancellationToken = default);
    Task CancelAsync(string id, string message = "Operation cancelled by operator.", CancellationToken cancellationToken = default);
}

public sealed record WorkflowDocumentSnapshot(
    string Id, string Name, string ConnectionId, bool IsLocalDraft,
    string ValidationState, string ValidationSummary, string Summary,
    IReadOnlyList<string> RelatedIds, DateTimeOffset? UpdatedAt = null);

public sealed record WorkflowGatewaySnapshot(bool Available, string Message);
public sealed record WorkflowBundleSnapshot(string Name, string Path, string Summary, DateTimeOffset? CreatedAt = null);

public sealed record MissionCommandWorkflowRequest(string MissionId, string ConnectionId, string Command, string? Reason = null, bool Emergency = false);
public sealed record TaskCommandWorkflowRequest(string TaskId, string ConnectionId, string Command, string? Reason = null, bool Emergency = false);
public sealed record TaskAssignmentWorkflowRequest(string TaskId, string VehicleId, bool ValidateOnAssign = true);

public sealed record AutonomyWorkflowSnapshot(
    WorkflowGatewaySnapshot Gateway,
    IReadOnlyList<WorkflowDocumentSnapshot> Missions,
    IReadOnlyList<WorkflowDocumentSnapshot> Tasks,
    string? LastError = null);

public interface IAutonomyWorkflow
{
    event EventHandler? Changed;
    AutonomyWorkflowSnapshot Current { get; }
    Task RefreshAsync(string? connectionId = null, CancellationToken cancellationToken = default);
    Task<WorkflowDocumentSnapshot> ImportMissionAsync(string path, CancellationToken cancellationToken = default);
    Task<WorkflowDocumentSnapshot> ImportTaskAsync(string path, CancellationToken cancellationToken = default);
    Task ExportMissionAsync(string missionId, string path, CancellationToken cancellationToken = default);
    Task ExportTaskAsync(string taskId, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowFinding>> ValidateMissionAsync(string missionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowFinding>> ValidateTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanMissionPublishAsync(string missionId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanTaskPublishAsync(string taskId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanTaskAssignmentAsync(TaskAssignmentWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanMissionCommandAsync(MissionCommandWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanTaskCommandAsync(TaskCommandWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<WorkflowBundleSnapshot> ExportBundleAsync(string destinationPath, IReadOnlyList<string> missionIds, IReadOnlyList<string> taskIds, IReadOnlyList<string> behaviourIds, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default);
    Task<WorkflowBundleSnapshot> InspectBundleAsync(string archivePath, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanBundleImportAsync(string archivePath, CancellationToken cancellationToken = default);
}

public sealed record BehaviourWorkflowSnapshot(
    string Id, string Name, string Version, string State, string Summary,
    string? ConnectionId = null, IReadOnlyList<WorkflowFinding>? Findings = null);

public sealed record BehaviourBindingWorkflowSnapshot(
    string ConnectionId, string BehaviourId, string BehaviourVersion,
    IReadOnlyDictionary<string, string> Bindings, string Summary);

public interface IBehaviourWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<BehaviourWorkflowSnapshot> LocalPackages { get; }
    IReadOnlyList<string> LibraryIssues { get; }
    Task RefreshAsync(string connectionId, bool refreshLocal = false, bool refreshRemote = false, CancellationToken cancellationToken = default);
    Task<BehaviourWorkflowSnapshot> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(string packageId, string destinationPath, CancellationToken cancellationToken = default);
    Task RemoveLocalAsync(string packageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowFinding>> ValidateAsync(string packageId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanDeployAsync(string connectionId, string packageId, string operation = "install", CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanRemoveAsync(string connectionId, string packageId, CancellationToken cancellationToken = default);
    Task<BehaviourBindingWorkflowSnapshot> InspectBindingsAsync(string connectionId, string packageId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanSetBindingAsync(string connectionId, string packageId, string bindingName, string geometryId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanClearBindingAsync(string connectionId, string packageId, string bindingName, CancellationToken cancellationToken = default);
}

public sealed record GeometryWorkflowSnapshot(
    string Id, string Name, string Kind, string Origin, string ValidationState,
    string ValidationSummary, string? ConnectionId = null, string? Revision = null);
public sealed record GeometryRemoteWorkflowSnapshot(string ConnectionId, string Id, string Name, string Kind, string State, string Summary);
public sealed record GeometryCreateWorkflowRequest(string Kind, string? Id = null, string? Name = null);
public sealed record GeometryGroupWorkflowSnapshot(string Name, int GeometryCount);
public sealed record GeometrySetExportWorkflowRequest(string Path, string Name, IReadOnlyList<string> GeometryIds);
public sealed record GeometrySelectionWorkflowSnapshot(
    IReadOnlyList<string> GeometryIds,
    string? AnchorGeometryId)
{
    public static GeometrySelectionWorkflowSnapshot Empty { get; } = new([], null);
    public bool HasSelection => GeometryIds.Count > 0;
}

/// <summary>
/// Session-only geometry selection shared by map rendering and local-library
/// clients. Geometry documents remain persisted separately; this interface
/// deliberately stores only IDs and never affects unit selection by itself.
/// </summary>
#pragma warning disable CA1716 // Set is a frozen public compatibility member; renaming it would break existing clients.
public interface IGeometrySelectionWorkflow
{
    event EventHandler? Changed;
    GeometrySelectionWorkflowSnapshot Current { get; }
    void Set(IReadOnlyList<string> geometryIds, string? anchorGeometryId = null);
    void Clear();
}
/// <summary>
/// Canonical JSON for a geometry document produced by a declarative client.
/// Pointer-driven editors remain a GUI concern; the workflow owns validation
/// and persistence of the resulting document.
/// </summary>
public sealed record GeometrySaveWorkflowRequest(string DocumentJson);

public interface IGeometryWorkflow
{
    event EventHandler? Changed;
    WorkflowGatewaySnapshot Gateway { get; }
    IReadOnlyList<GeometryWorkflowSnapshot> LocalDocuments { get; }
    IReadOnlyList<GeometryRemoteWorkflowSnapshot> RemoteDocuments { get; }
    IReadOnlyList<GeometryGroupWorkflowSnapshot> Groups { get; }
    IReadOnlyList<string> LibraryIssues { get; }
    Task RefreshAsync(string? connectionId = null, CancellationToken cancellationToken = default);
    Task<GeometryWorkflowSnapshot> CreateAsync(GeometryCreateWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<GeometryWorkflowSnapshot> ImportAsync(string path, bool replace = false, CancellationToken cancellationToken = default);
    Task ImportSetAsync(string path, bool replace = false, CancellationToken cancellationToken = default);
    Task ExportAsync(string geometryId, string path, CancellationToken cancellationToken = default);
    Task ExportSetAsync(GeometrySetExportWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<GeometryWorkflowSnapshot> SaveAsync(GeometrySaveWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<GeometryWorkflowSnapshot> DuplicateAsync(string geometryId, string newGeometryId, CancellationToken cancellationToken = default);
    Task RemoveAsync(string geometryId, CancellationToken cancellationToken = default);
    Task CreateGroupAsync(string name, CancellationToken cancellationToken = default);
    Task RenameGroupAsync(string name, string replacement, CancellationToken cancellationToken = default);
    Task DeleteGroupAsync(string name, CancellationToken cancellationToken = default);
    Task AssignGroupAsync(string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default);
    Task RemoveFromGroupAsync(string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default);
    IReadOnlyList<string> GetGroups(string geometryId);
    Task<GeometryWorkflowSnapshot> PullAsync(string connectionId, string geometryId, bool replace = false, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanRemoteAsync(string action, string connectionId, string geometryId, CancellationToken cancellationToken = default);
}

public sealed record Px4ParameterProfileWorkflowSnapshot(
    string Id, string Name, string FileName, DateTimeOffset CreatedAt, string Hash,
    int ParameterCount, string? FirmwareVersion, string? SourceFileName);
public sealed record Px4ParameterDiffWorkflowSnapshot(string Name, string ProfileValue, string? LiveValue, string State, string Detail);

public interface IMavlinkParameterProfileWorkflow
{
    event EventHandler? Changed;
    string LibraryPath { get; }
    IReadOnlyList<Px4ParameterProfileWorkflowSnapshot> Profiles { get; }
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<Px4ParameterProfileWorkflowSnapshot> ImportAsync(string path, string? name = null, CancellationToken cancellationToken = default);
    Task ExportAsync(string profileId, string path, CancellationToken cancellationToken = default);
    Task<Px4ParameterProfileWorkflowSnapshot> DownloadAsync(string connectionId, string vehicleId, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Px4ParameterDiffWorkflowSnapshot>> CompareAsync(string connectionId, string vehicleId, string profileId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanApplyAsync(string connectionId, string vehicleId, string profileId, CancellationToken cancellationToken = default);
}
#pragma warning restore CA1716

// Compatibility name retained for existing PX4 integrations. The workflow is
// shared by PX4 and ArduPilot MAVLink targets.
public interface IPx4ParameterProfileWorkflow : IMavlinkParameterProfileWorkflow
{
}

public sealed record SikRadioDeviceWorkflowSnapshot(
    string Id, string FriendlyName, string PortName, string Manufacturer,
    string HardwareId, string State, string Detail);
public sealed record SikRadioProbeWorkflowSnapshot(
    string DeviceId, string PortName, bool Identified, string Identity, string Summary,
    IReadOnlyDictionary<int, int> LocalSettings, IReadOnlyDictionary<int, int> RemoteSettings, DateTimeOffset ProbedAt);

public interface ISikRadioWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<SikRadioDeviceWorkflowSnapshot> Devices { get; }
    Task RefreshDevicesAsync(CancellationToken cancellationToken = default);
    Task<SikRadioProbeWorkflowSnapshot> ProbeAsync(string deviceIdOrPort, CancellationToken cancellationToken = default);
    bool TryGetProbe(string deviceIdOrPort, out SikRadioProbeWorkflowSnapshot? probe);
    Task<ReviewedOperationSnapshot> PlanConfigureAsync(string deviceIdOrPort, IReadOnlyDictionary<int, int> settings, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanPairAsync(string sourceDeviceIdOrPort, string targetDeviceIdOrPort, CancellationToken cancellationToken = default);
}

// Native flight missions deliberately have a separate contract from Logos
// mission/task documents. They are the portable Robot Command planning model
// that future PX4, ArduPilot, and Logos compilers can target.
public enum FlightMissionStepKind
{
    // These numeric values are persisted in robotcommand.flight-mission.v2
    // JSON. Keep the original v2 values stable; newer kinds must be appended.
    Takeoff = 0,
    PointOfInterest = 1,
    WaypointSequence = 2,
    SurveyZone = 3,
    TimedLoiter = 4,
    CameraCaptureIntent = 5,
    ReturnToLaunch = 6,
    Land = 7,
    CorridorScan = 8
}
public enum FlightMissionExecutionState { NotUploaded, Uploaded, Running, Paused, Completed, Interrupted, Failed, Unknown }

/// <summary>Action PX4 should take after the final authored mission item.</summary>
public enum FlightMissionEndAction
{
    Hold,
    ReturnToLaunch
}

/// <summary>Session-only decision about the mission still stored on a vehicle after landing.</summary>
public enum FlightMissionPostLandingState
{
    None,
    AwaitingDecision,
    Retained,
    Removed,
    ResumePrepared
}

public sealed record FlightMissionCoordinate(double LatitudeDegrees, double LongitudeDegrees);

/// <summary>Metadata-only capture intent. PX4 trigger commands are deliberately not emitted yet.</summary>
public sealed record FlightMissionCameraIntent(
    string Mode = "None",
    double? TriggerDistanceMetres = null,
    double? TriggerIntervalSeconds = null,
    string? CameraName = null,
    string? Notes = null);

public sealed record FlightMissionSurveyOptions(
    double LineSpacingMetres = 25,
    double BearingDegrees = 0,
    double TurnaroundDistanceMetres = 0,
    bool ReverseEntry = false,
    FlightMissionCameraIntent? CameraIntent = null);

/// <summary>
/// A local-first corridor survey definition over a frozen directional route.
/// Spacing and trigger distance are manual values, while overlap percentages
/// retain the intended camera coverage for a future camera-aware compiler.
/// </summary>
public enum FlightMissionCorridorEntrySide { Left, Right }

public sealed record FlightMissionCorridorOptions(
    double CorridorWidthMetres = 50,
    double LineSpacingMetres = 25,
    double TurnaroundDistanceMetres = 0,
    bool ReverseDirection = false,
    FlightMissionCorridorEntrySide EntrySide = FlightMissionCorridorEntrySide.Left,
    double FrontLapPercent = 70,
    double SideLapPercent = 70,
    bool TakeImagesInTurnarounds = false,
    FlightMissionCameraIntent? CameraIntent = null);

/// <summary>
/// Target preferences remain advisory so a mission stays portable. An active
/// fence is a local safety reference; it never instructs an upload or change
/// to the vehicle fence.
/// </summary>
public sealed record FlightMissionTargetAssignment(
    string? PreferredProfile = null,
    string? PreferredVehicleClass = null,
    string? ActiveFenceId = null);

public sealed record FlightMissionStep(
    string Id,
    FlightMissionStepKind Kind,
    string? SourceGeometryId = null,
    string? SourceGeometryName = null,
    string? SourceGeometryHash = null,
    IReadOnlyList<FlightMissionCoordinate>? Coordinates = null,
    double? RelativeAltitudeMetres = null,
    double? CruiseSpeedMetresPerSecond = null,
    bool TerrainFollowing = false,
    double? LoiterDurationSeconds = null,
    FlightMissionSurveyOptions? Survey = null,
    FlightMissionCameraIntent? CameraIntent = null,
    FlightMissionCorridorOptions? Corridor = null)
{
    public IReadOnlyList<FlightMissionCoordinate> FrozenCoordinates => Coordinates ?? [];
    public bool HasSourceGeometry => !string.IsNullOrWhiteSpace(SourceGeometryName);
    public string DisplayName => Kind switch
    {
        FlightMissionStepKind.ReturnToLaunch => "RTL",
        FlightMissionStepKind.CorridorScan => "Corridor scan",
        _ => Kind.ToString()
    };
    public bool NeedsGeometryBinding => Kind is FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan or FlightMissionStepKind.TimedLoiter
        && FrozenCoordinates.Count == 0;
    public string ContextSummary => Kind switch
    {
        _ when NeedsGeometryBinding && Kind == FlightMissionStepKind.TimedLoiter && LoiterDurationSeconds is { } duration
            => $"{duration:0.#} s loiter · geometry required",
        _ when NeedsGeometryBinding => "Geometry required",
        FlightMissionStepKind.TimedLoiter when LoiterDurationSeconds is { } duration
            => $"{duration:0.#} s loiter",
        FlightMissionStepKind.PointOfInterest => "1 point",
        FlightMissionStepKind.WaypointSequence => $"{FrozenCoordinates.Count} route points",
        FlightMissionStepKind.CorridorScan => $"{FrozenCoordinates.Count} route points",
        FlightMissionStepKind.SurveyZone => $"{FrozenCoordinates.Count} zone points",
        _ => string.Empty
    };
    public bool HasContextSummary => !string.IsNullOrWhiteSpace(ContextSummary);
}

public sealed record FlightMissionDocument(
    string SchemaVersion,
    string MissionId,
    string DisplayName,
    double RelativeAltitudeMetres,
    IReadOnlyList<FlightMissionStep> Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ContentSha256 = "",
    string? SourceSummary = null,
    double CruiseSpeedMetresPerSecond = 5,
    FlightMissionCameraIntent? CameraIntent = null,
    FlightMissionTargetAssignment? TargetAssignment = null,
    FlightMissionEndAction EndAction = FlightMissionEndAction.Hold)
{
    public const string CurrentSchemaVersion = "robotcommand.flight-mission.v2";
    public const string LegacySchemaVersion = "robotcommand.flight-mission.v1";
}

public sealed record FlightMissionSnapshot(
    string Id,
    string Name,
    double RelativeAltitudeMetres,
    IReadOnlyList<FlightMissionStep> Steps,
    string ValidationState,
    IReadOnlyList<WorkflowFinding> Findings,
    DateTimeOffset UpdatedAt,
    string Hash,
    double CruiseSpeedMetresPerSecond = 5,
    FlightMissionTargetAssignment? TargetAssignment = null,
    FlightMissionEndAction EndAction = FlightMissionEndAction.Hold);

public sealed record FlightMissionExecutionSnapshot(
    string MissionId,
    string ConnectionId,
    string VehicleId,
    FlightMissionExecutionState State,
    int? CurrentItemIndex,
    int ItemCount,
    string Summary,
    DateTimeOffset UpdatedAt,
    string ExecutorKind = "PX4",
    string? ActiveStepId = null,
    string? ActiveStepName = null,
    string? LastEvent = null,
    bool TerrainFallbackUsed = false,
    IReadOnlyList<FlightMissionCaptureEvent>? CaptureEvents = null,
    FlightMissionPostLandingState PostLandingState = FlightMissionPostLandingState.None,
    int? ResumeItemIndex = null);

public sealed record FlightMissionCaptureEvent(
    string StepId,
    DateTimeOffset CapturedAt,
    string Summary);

/// <summary>Session-only compiled mission item consumed by a backend executor.</summary>
public sealed record FlightMissionCompiledItem(
    int Index,
    string StepId,
    FlightMissionStepKind Kind,
    FlightMissionCoordinate? Coordinate,
    double RelativeAltitudeMetres,
    double CruiseSpeedMetresPerSecond,
    double? DurationSeconds = null,
    bool SimulatedCapture = false);

public sealed record FlightMissionExecutionArtifact(
    string MissionId,
    string ExecutorKind,
    IReadOnlyList<FlightMissionCompiledItem> Items,
    string ArtifactHash,
    bool TerrainFallbackUsed = false,
    string? TerrainWarning = null,
    Px4FenceKind? FenceKind = null,
    IReadOnlyList<FlightMissionCoordinate>? FenceCoordinates = null);

public sealed record FlightMissionExecutorResult(
    bool Succeeded,
    string Summary,
    string? FailureCode = null,
    int? ItemCount = null);

public sealed record FlightMissionExecutorProgress(
    FlightMissionExecutionState State,
    int? CurrentItemIndex,
    int ItemCount,
    string Summary,
    string? ActiveStepId = null,
    string? ActiveStepName = null,
    string? LastEvent = null,
    IReadOnlyList<FlightMissionCaptureEvent>? CaptureEvents = null,
    bool PostLandingDecisionAvailable = false,
    int? ResumeItemIndex = null);

public sealed record FlightMissionCreateRequest(string Name, double RelativeAltitudeMetres = 20, string? Id = null);

#pragma warning disable CA1068 // These frozen overloads keep the reviewed-operation compatibility signature.
public interface IFlightMissionWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<FlightMissionSnapshot> Missions { get; }
    IReadOnlyList<FlightMissionExecutionSnapshot> Executions { get; }
    FlightMissionSnapshot? MapPreview { get; }
    bool TryGet(string missionId, out FlightMissionSnapshot? mission);
    void SetMapPreviewMission(string? missionId);
    Task<FlightMissionSnapshot> CreateAsync(FlightMissionCreateRequest request, CancellationToken cancellationToken = default);
    Task RenameAsync(string missionId, string name, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> DuplicateAsync(string missionId, string? newName = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(string missionId, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> ImportAsync(string path, bool replace = false, CancellationToken cancellationToken = default);
    Task ExportAsync(string missionId, string path, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> SetAltitudeAsync(string missionId, double relativeAltitudeMetres, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> AddTakeoffAsync(string missionId, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> AddGeometryAsync(string missionId, string geometryId, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> AddSurveyAsync(string missionId, string? zoneGeometryId, FlightMissionSurveyOptions? options = null, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> AddCorridorAsync(string missionId, string? waypointSequenceGeometryId, FlightMissionCorridorOptions? options = null, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> AddTimedLoiterAsync(string missionId, string? pointGeometryId, double durationSeconds, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> AddCameraIntentAsync(string missionId, FlightMissionCameraIntent intent, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> AddReturnToLaunchAsync(string missionId, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> AddLandAsync(string missionId, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> RemoveStepAsync(string missionId, string stepId, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> MoveStepAsync(string missionId, string stepId, int targetIndex, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> SetCruiseSpeedAsync(string missionId, double cruiseSpeedMetresPerSecond, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> SetEndActionAsync(string missionId, FlightMissionEndAction endAction, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> SetStepOverridesAsync(string missionId, string stepId, double? relativeAltitudeMetres, double? cruiseSpeedMetresPerSecond, bool terrainFollowing, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> SetStepGeometryAsync(string missionId, string stepId, string geometryId, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> SetStepOptionsAsync(string missionId, string stepId, FlightMissionSurveyOptions? survey, FlightMissionCorridorOptions? corridor, double? loiterDurationSeconds, FlightMissionCameraIntent? cameraIntent, CancellationToken cancellationToken = default);
    Task<FlightMissionSnapshot> SetTargetAssignmentAsync(string missionId, FlightMissionTargetAssignment? assignment, CancellationToken cancellationToken = default);
    Task<FlightMissionCompilationPreview> PreviewAsync(string missionId, string? vehicleId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowFinding>> ValidateAsync(string missionId, string? vehicleId = null, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanUploadAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default, bool allowTerrainFallback = false);
    Task<ReviewedOperationSnapshot> PlanDownloadAsync(string connectionId, string vehicleId, string name, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanStartAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default, bool allowTerrainFallback = false);
    Task<ReviewedOperationSnapshot> PlanPauseAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanContinueAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanResumeAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanRetainAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanRemoveAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default);
}
#pragma warning restore CA1068

public sealed record FlightMissionCompilationPreview(
    string MissionId,
    IReadOnlyList<FlightMissionCoordinate> Route,
    double DistanceMetres,
    double EstimatedDurationSeconds,
    int MissionItemCount,
    string ArtifactHash,
    IReadOnlyList<WorkflowFinding> Findings,
    string Summary,
    FlightMissionTerrainProfile? TerrainProfile = null,
    int SurveyLineCount = 0,
    double SurveyAreaSquareMetres = 0);

/// <summary>
/// A reviewed, non-persistent terrain conversion used only for the upload that
/// produced it. Values are PX4 relative-to-home altitudes in metres.
/// </summary>
public sealed record FlightMissionTerrainPoint(
    FlightMissionCoordinate Coordinate,
    double RelativeHomeAltitudeMetres);

public sealed record FlightMissionTerrainProfile(
    string MissionId,
    IReadOnlyDictionary<string, IReadOnlyList<FlightMissionTerrainPoint>> PointsByStepId,
    string VerticalDatum,
    string ArtifactHash,
    double HomeElevationMetres,
    int SampleCount,
    string Summary);

public enum Px4FenceKind { Inclusion, Exclusion }
public sealed record Px4FenceDocument(
    string SchemaVersion,
    string FenceId,
    string DisplayName,
    Px4FenceKind Kind,
    IReadOnlyList<FlightMissionCoordinate> Coordinates,
    string? SourceGeometryId,
    string? SourceGeometryName,
    string? SourceGeometryHash,
    double? MinimumAltitudeMetres,
    double? MaximumAltitudeMetres,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ContentSha256 = "")
{
    public const string CurrentSchemaVersion = "robotcommand.px4-fence.v1";
}

public sealed record Px4FenceSnapshot(Px4FenceDocument Document, IReadOnlyList<WorkflowFinding> Findings);
public interface IPx4GeofenceWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<Px4FenceSnapshot> Fences { get; }
    Task<Px4FenceSnapshot> CreateFromZoneAsync(string geometryId, string? name = null, Px4FenceKind kind = Px4FenceKind.Inclusion, CancellationToken cancellationToken = default);
    Task DeleteAsync(string fenceId, CancellationToken cancellationToken = default);
    Task<Px4FenceSnapshot> ImportAsync(string path, bool replace = false, CancellationToken cancellationToken = default);
    Task ExportAsync(string fenceId, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowFinding>> ValidateAsync(string fenceId, string? vehicleId = null, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanUploadAsync(string fenceId, string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanDownloadAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<ReviewedOperationSnapshot> PlanClearAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
}
