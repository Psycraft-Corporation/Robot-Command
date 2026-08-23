using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Missions;
using RobotCommand.Services.Terrain;
using RobotCommand.State;

namespace RobotCommand.Services.Workflows;

public sealed class FlightMissionWorkflow : IFlightMissionWorkflow
{
    private readonly FlightMissionLibraryStore _store;
    private readonly IGeometryDocumentStore _geometry;
    private readonly IUnitObservationWorkflow _units;
    private readonly IMavlinkConnectionRegistry _connections;
    private readonly ReviewedOperationWorkflow _reviewed;
    private readonly IFlightMissionCompiler[] _compilers;
    private readonly IFlightMissionCompiler _defaultCompiler;
    private readonly ITerrainElevationService _terrain;
    private readonly FenceLibraryStore _fences;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private readonly IUiDispatcher _dispatcher;
    private readonly IReadOnlyList<IFlightMissionExecutor> _executors;
    private readonly Dictionary<string, FlightMissionExecutionSnapshot> _executions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlightMissionExecutionArtifact> _artifacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completionFinalizationStarted = new(StringComparer.Ordinal);
    private int _refreshingProgress;
    private string? _mapPreviewMissionId;

    public FlightMissionWorkflow(FlightMissionLibraryStore store, IGeometryDocumentStore geometry, IUnitObservationWorkflow units,
        IMavlinkConnectionRegistry connections, ReviewedOperationWorkflow reviewed, IEnumerable<IFlightMissionCompiler> compilers, ITerrainElevationService terrain,
        FenceLibraryStore fences, IEntityStore<string, OperationalCommandRecord> commands, IUiDispatcher dispatcher,
        IEnumerable<IFlightMissionExecutor> executors)
    {
        _store = store; _geometry = geometry; _units = units; _connections = connections; _reviewed = reviewed; _compilers = compilers.ToArray();
        _defaultCompiler = _compilers.FirstOrDefault(item => item.BackendKey.Equals("PX4", StringComparison.OrdinalIgnoreCase))
            ?? _compilers[0];
        _terrain = terrain; _fences = fences; _commands = commands; _dispatcher = dispatcher; _executors = executors.ToArray();
        _geometry.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _fences.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _units.Changed += (_, _) => RefreshProgress();
    }

    public event EventHandler? Changed;
    public IReadOnlyList<FlightMissionSnapshot> Missions => _store.Missions.Select(Project).ToArray();
    public IReadOnlyList<FlightMissionExecutionSnapshot> Executions => _executions.Values.OrderByDescending(item => item.UpdatedAt).ToArray();
    public FlightMissionSnapshot? MapPreview => _mapPreviewMissionId is not null && TryGet(_mapPreviewMissionId, out var mission) ? mission : null;
    public bool TryGet(string missionId, out FlightMissionSnapshot? mission)
    {
        if (_store.TryGet(missionId, out var document) && document is not null) { mission = Project(document); return true; }
        mission = null; return false;
    }

    public void SetMapPreviewMission(string? missionId)
    {
        var next = !string.IsNullOrWhiteSpace(missionId) && _store.TryGet(missionId, out _) ? missionId : null;
        if (string.Equals(_mapPreviewMissionId, next, StringComparison.Ordinal)) return;
        _mapPreviewMissionId = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<FlightMissionSnapshot> CreateAsync(FlightMissionCreateRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = string.IsNullOrWhiteSpace(request.Id) ? $"mission-{Guid.NewGuid():N}" : request.Id.Trim();
        var document = await _store.SaveAsync(new(FlightMissionDocument.CurrentSchemaVersion, id, request.Name.Trim(), request.RelativeAltitudeMetres, [], now, now), false, cancellationToken);
        return Publish(document);
    }

    public async Task RenameAsync(string missionId, string name, CancellationToken cancellationToken = default)
        => Publish(await UpdateAsync(missionId, document => document with { DisplayName = name.Trim(), UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken));

    public async Task<FlightMissionSnapshot> DuplicateAsync(string missionId, string? newName = null, CancellationToken cancellationToken = default)
    {
        var source = Require(missionId);
        var now = DateTimeOffset.UtcNow;
        return Publish(await _store.SaveAsync(source with { MissionId = $"mission-{Guid.NewGuid():N}", DisplayName = newName?.Trim() ?? $"{source.DisplayName} copy", CreatedAt = now, UpdatedAt = now, ContentSha256 = string.Empty }, false, cancellationToken));
    }
    public async Task DeleteAsync(string missionId, CancellationToken cancellationToken = default)
    {
        await _store.RemoveAsync(missionId, cancellationToken);
        _executions.Remove(missionId);
        if (string.Equals(_mapPreviewMissionId, missionId, StringComparison.Ordinal)) _mapPreviewMissionId = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task<FlightMissionSnapshot> ImportAsync(string path, bool replace = false, CancellationToken cancellationToken = default) => Publish(await _store.ImportAsync(path, replace, cancellationToken));
    public Task ExportAsync(string missionId, string path, CancellationToken cancellationToken = default) => _store.ExportAsync(missionId, path, cancellationToken);
    public async Task<FlightMissionSnapshot> SetAltitudeAsync(string missionId, double relativeAltitudeMetres, CancellationToken cancellationToken = default) => Publish(await UpdateAsync(missionId, document => document with { RelativeAltitudeMetres = relativeAltitudeMetres, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken));
    public Task<FlightMissionSnapshot> AddTakeoffAsync(string missionId, CancellationToken cancellationToken = default)
    {
        // Snapshot the mission default on the step so later mission-level
        // altitude changes never silently change an authored takeoff.
        var mission = Require(missionId);
        return AddStepAsync(missionId, new(
            Guid.NewGuid().ToString("N"),
            FlightMissionStepKind.Takeoff,
            RelativeAltitudeMetres: mission.RelativeAltitudeMetres), cancellationToken);
    }
    public Task<FlightMissionSnapshot> AddReturnToLaunchAsync(string missionId, CancellationToken cancellationToken = default) => AddStepAsync(missionId, new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.ReturnToLaunch), cancellationToken);
    public Task<FlightMissionSnapshot> AddLandAsync(string missionId, CancellationToken cancellationToken = default) => AddStepAsync(missionId, new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.Land), cancellationToken);

    public async Task<FlightMissionSnapshot> AddGeometryAsync(string missionId, string geometryId, CancellationToken cancellationToken = default)
    {
        if (!_geometry.TryGet(geometryId, out var geometry) || geometry is null) throw new KeyNotFoundException($"Geometry '{geometryId}' was not found.");
        var coordinates = geometry.Kind switch
        {
            GeometryDocumentKind.PointOfInterest => geometry.Points.Take(1),
            GeometryDocumentKind.WaypointSequence => geometry.Points,
            _ => throw new InvalidOperationException("Only saved PoIs and waypoint sequences can be added to a mission in this release.")
        };
        var kind = geometry.Kind == GeometryDocumentKind.PointOfInterest ? FlightMissionStepKind.PointOfInterest : FlightMissionStepKind.WaypointSequence;
        var step = new FlightMissionStep(Guid.NewGuid().ToString("N"), kind, geometry.GeometryId, geometry.DisplayName, geometry.ContentSha256,
            coordinates.Select(point => new FlightMissionCoordinate(point.LatitudeDegrees, point.LongitudeDegrees)).ToArray());
        return await AddStepAsync(missionId, step, cancellationToken);
    }
    public async Task<FlightMissionSnapshot> AddSurveyAsync(string missionId, string zoneGeometryId, FlightMissionSurveyOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!_geometry.TryGet(zoneGeometryId, out var geometry) || geometry is null || geometry.Kind != GeometryDocumentKind.Zone)
            throw new InvalidOperationException("Select a saved zone before adding a survey.");
        var ring = geometry.Rings.Count > 0 ? geometry.Rings[0].Points : geometry.Points;
        var coordinates = ring.Select(point => new FlightMissionCoordinate(point.LatitudeDegrees, point.LongitudeDegrees)).ToArray();
        return await AddStepAsync(missionId, new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.SurveyZone, geometry.GeometryId, geometry.DisplayName, geometry.ContentSha256, coordinates, Survey: options ?? new()), cancellationToken);
    }
    public async Task<FlightMissionSnapshot> AddCorridorAsync(string missionId, string waypointSequenceGeometryId, FlightMissionCorridorOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!_geometry.TryGet(waypointSequenceGeometryId, out var geometry) || geometry is null || geometry.Kind != GeometryDocumentKind.WaypointSequence)
            throw new InvalidOperationException("Select a saved waypoint sequence before adding a corridor scan.");
        var coordinates = geometry.Points.Select(point => new FlightMissionCoordinate(point.LatitudeDegrees, point.LongitudeDegrees)).ToArray();
        return await AddStepAsync(missionId, new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.CorridorScan, geometry.GeometryId, geometry.DisplayName, geometry.ContentSha256, coordinates, Corridor: options ?? new()), cancellationToken);
    }
    public async Task<FlightMissionSnapshot> AddTimedLoiterAsync(string missionId, string pointGeometryId, double durationSeconds, CancellationToken cancellationToken = default)
    {
        if (!_geometry.TryGet(pointGeometryId, out var geometry) || geometry is null || geometry.Kind != GeometryDocumentKind.PointOfInterest)
            throw new InvalidOperationException("Select a saved point of interest before adding a loiter.");
        if (geometry.Points.Count == 0) throw new InvalidOperationException("The selected point of interest has no coordinates.");
        var point = geometry.Points[0];
        return await AddStepAsync(missionId, new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.TimedLoiter, geometry.GeometryId, geometry.DisplayName, geometry.ContentSha256, [new(point.LatitudeDegrees, point.LongitudeDegrees)], LoiterDurationSeconds: durationSeconds), cancellationToken);
    }
    public Task<FlightMissionSnapshot> AddCameraIntentAsync(string missionId, FlightMissionCameraIntent intent, CancellationToken cancellationToken = default)
        => AddStepAsync(missionId, new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.CameraCaptureIntent, CameraIntent: intent), cancellationToken);

    public async Task<FlightMissionSnapshot> RemoveStepAsync(string missionId, string stepId, CancellationToken cancellationToken = default)
        => Publish(await UpdateAsync(missionId, document => document with { Steps = document.Steps.Where(step => !step.Id.Equals(stepId, StringComparison.Ordinal)).ToArray(), UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken));
    public async Task<FlightMissionSnapshot> MoveStepAsync(string missionId, string stepId, int targetIndex, CancellationToken cancellationToken = default)
    {
        var document = Require(missionId); var steps = document.Steps.ToList(); var step = steps.FirstOrDefault(item => item.Id == stepId) ?? throw new KeyNotFoundException("Mission step was not found.");
        steps.Remove(step); steps.Insert(Math.Clamp(targetIndex, 0, steps.Count), step);
        return Publish(await _store.SaveAsync(document with { Steps = steps, UpdatedAt = DateTimeOffset.UtcNow }, true, cancellationToken));
    }
    public Task<FlightMissionSnapshot> SetCruiseSpeedAsync(string missionId, double cruiseSpeedMetresPerSecond, CancellationToken cancellationToken = default)
        => UpdateAndPublishAsync(missionId, document => document with { CruiseSpeedMetresPerSecond = cruiseSpeedMetresPerSecond, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
    public Task<FlightMissionSnapshot> SetEndActionAsync(string missionId, FlightMissionEndAction endAction, CancellationToken cancellationToken = default)
        => UpdateAndPublishAsync(missionId, document => document with { EndAction = endAction, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
    public Task<FlightMissionSnapshot> SetStepOverridesAsync(string missionId, string stepId, double? relativeAltitudeMetres, double? cruiseSpeedMetresPerSecond, bool terrainFollowing, CancellationToken cancellationToken = default)
        => UpdateAndPublishAsync(missionId, document => document with { Steps = document.Steps.Select(step => step.Id == stepId ? step with { RelativeAltitudeMetres = relativeAltitudeMetres, CruiseSpeedMetresPerSecond = cruiseSpeedMetresPerSecond, TerrainFollowing = terrainFollowing } : step).ToArray(), UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
    public Task<FlightMissionSnapshot> SetStepOptionsAsync(string missionId, string stepId, FlightMissionSurveyOptions? survey, FlightMissionCorridorOptions? corridor, double? loiterDurationSeconds, FlightMissionCameraIntent? cameraIntent, CancellationToken cancellationToken = default)
        => UpdateAndPublishAsync(missionId, document => document with
        {
            Steps = document.Steps.Select(step => step.Id == stepId
                ? step with
                {
                    Survey = survey,
                    Corridor = corridor,
                    LoiterDurationSeconds = loiterDurationSeconds,
                    CameraIntent = cameraIntent
                }
                : step).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    public Task<FlightMissionSnapshot> SetTargetAssignmentAsync(string missionId, FlightMissionTargetAssignment? assignment, CancellationToken cancellationToken = default)
        => UpdateAndPublishAsync(missionId, document => document with { TargetAssignment = assignment, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
    public async Task<FlightMissionCompilationPreview> PreviewAsync(string missionId, string? vehicleId = null, CancellationToken cancellationToken = default)
    {
        var mission = Require(missionId); var target = Target(vehicleId ?? string.Empty); var preview = CompilerFor(target).Preview(mission, target);
        var findings = preview.Findings.ToList();
        if (target?.IsGhost == true)
            findings.RemoveAll(item => item.Code is "MISSION_BACKEND_UNSUPPORTED" or "MISSION_VEHICLE_UNSUPPORTED" or "MISSION_TELEMETRY_STALE" or "MISSION_GLOBAL_POSITION_UNAVAILABLE" or "MISSION_NAVIGATION_UNAVAILABLE");
        findings.AddRange(FenceFindings(mission, preview.Route, target));
        if (!mission.Steps.Any(step => step.TerrainFollowing))
            return preview with { Findings = findings.DistinctBy(item => item.Code).ToArray() };
        var terrainProfile = await BuildTerrainProfileAsync(mission, target, cancellationToken);
        findings.AddRange(terrainProfile.Findings);
        return preview with
        {
            Findings = findings.DistinctBy(item => item.Code).ToArray(),
            TerrainProfile = terrainProfile.Profile,
            ArtifactHash = terrainProfile.Profile?.ArtifactHash ?? preview.ArtifactHash,
            Summary = findings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking)
                ? "Terrain profile is not ready for upload."
                : terrainProfile.Profile is null ? preview.Summary : $"{preview.Summary} · terrain profile ready"
        };
    }
    public async Task<IReadOnlyList<WorkflowFinding>> ValidateAsync(string missionId, string? vehicleId = null, CancellationToken cancellationToken = default)
        => (await PreviewAsync(missionId, vehicleId, cancellationToken)).Findings;

    public Task<ReviewedOperationSnapshot> PlanUploadAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default, bool allowTerrainFallback = false)
        => PlanAsync(ReviewedOperationKind.FlightMissionUpload, "Upload mission", missionId, ResolveConnectionId(connectionId, vehicleId), vehicleId, allowTerrainFallback, async token =>
        {
            var mission = Require(missionId); var preview = await PreviewForExecutionAsync(missionId, vehicleId, allowTerrainFallback, token); var findings = preview.Findings; if (Blocked(findings)) return Failure("Mission upload is no longer available.", findings);
            var targetVehicleId = ResolveCommandVehicleId(vehicleId);
            var executor = Executor(vehicleId);
            var fence = mission.TargetAssignment?.ActiveFenceId is { } fenceId && _fences.TryGet(fenceId, out var fenceSnapshot) && fenceSnapshot is not null
                ? new Px4FenceSnapshot(new Px4FenceDocument(Px4FenceDocument.CurrentSchemaVersion, fenceSnapshot.FenceId, fenceSnapshot.DisplayName,
                    fenceSnapshot.Kind == FenceKind.Inclusion ? Px4FenceKind.Inclusion : Px4FenceKind.Exclusion, fenceSnapshot.Coordinates,
                    fenceSnapshot.SourceGeometryId, fenceSnapshot.SourceGeometryName, fenceSnapshot.SourceGeometryHash,
                    fenceSnapshot.MinimumAltitudeMetres, fenceSnapshot.MaximumAltitudeMetres, fenceSnapshot.CreatedAt, fenceSnapshot.UpdatedAt, fenceSnapshot.ContentSha256), [])
                : null;
            var artifact = BuildArtifact(mission, preview, executor.ExecutorKind, allowTerrainFallback, fence);
            var target = Target(vehicleId);
            var wire = executor is Px4MissionExecutor or ArduPilotMissionExecutor ? CompilerFor(target).Compile(mission, target, preview.TerrainProfile) : null;
            var result = await executor.UploadAsync(ResolveConnectionId(connectionId, vehicleId), targetVehicleId, artifact, wire, token);
            if (result.Succeeded)
            {
                _completionFinalizationStarted.Remove(ExecutionKey(missionId, vehicleId));
                _artifacts[ExecutionKey(missionId, vehicleId)] = artifact;
                SetExecution(missionId, ResolveConnectionId(connectionId, vehicleId), vehicleId, FlightMissionExecutionState.Uploaded, null, artifact.Items.Count, result.Summary, executor.ExecutorKind, artifact.TerrainFallbackUsed, artifact.TerrainWarning, postLandingState: FlightMissionPostLandingState.None, resumeItemIndex: null);
            }
            return new("", result.Succeeded ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Succeeded, result.Summary, []);
        });
    public Task<ReviewedOperationSnapshot> PlanDownloadAsync(string connectionId, string vehicleId, string name, CancellationToken cancellationToken = default)
        => PlanAsync(ReviewedOperationKind.FlightMissionDownload, "Download mission", null, ResolveConnectionId(connectionId, vehicleId), vehicleId, false, async token =>
        {
            var result = await Client(ResolveMavlinkConnectionId(connectionId, vehicleId)).DownloadMissionAsync(ResolveCommandVehicleId(vehicleId), token);
            if (!result.Succeeded) return new("", ReviewedOperationState.Failed, false, result.Summary, []);
            FlightMissionDocument document;
            try { document = CompilerFor(Target(vehicleId)).Decompile(name, result.Items, DateTimeOffset.UtcNow); }
            catch (Exception ex) { return new("", ReviewedOperationState.Failed, false, $"Downloaded mission is not supported by the native library: {ex.Message}", []); }
            var saved = await _store.SaveAsync(document, false, token); Publish(saved);
            return new("", ReviewedOperationState.Succeeded, true, result.Summary, [saved.MissionId]);
        });
    public Task<ReviewedOperationSnapshot> PlanStartAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default, bool allowTerrainFallback = false) => PlanModeAsync(ReviewedOperationKind.FlightMissionStart, "Start mission", missionId, ResolveConnectionId(connectionId, vehicleId), vehicleId, false, allowTerrainFallback);
    public Task<ReviewedOperationSnapshot> PlanPauseAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default) => PlanModeAsync(ReviewedOperationKind.FlightMissionPause, "Pause mission", missionId, ResolveConnectionId(connectionId, vehicleId), vehicleId, true, false);
    public Task<ReviewedOperationSnapshot> PlanContinueAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default) => PlanModeAsync(ReviewedOperationKind.FlightMissionContinue, "Continue mission", missionId, ResolveConnectionId(connectionId, vehicleId), vehicleId, false, false, continueExisting: true);
    public Task<ReviewedOperationSnapshot> PlanResumeAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default) => PlanResumeAfterLandingAsync(missionId, ResolveConnectionId(connectionId, vehicleId), vehicleId, cancellationToken);
    public Task<ReviewedOperationSnapshot> PlanRetainAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => PlanPostLandingDecisionAsync(ReviewedOperationKind.FlightMissionRetain, "Retain onboard mission", missionId, ResolveConnectionId(connectionId, vehicleId), vehicleId, token =>
        {
            if (!TryGetAwaitingPostLanding(missionId, vehicleId, out var execution))
                return Task.FromResult(new ReviewedOperationExecutionResult("", ReviewedOperationState.Failed, false, "There is no post-landing mission decision waiting for this vehicle.", []));
            SetExecution(missionId, execution.ConnectionId, vehicleId, execution.State, execution.CurrentItemIndex, execution.ItemCount, "Onboard mission retained.", execution.ExecutorKind, execution.TerrainFallbackUsed, null, postLandingState: FlightMissionPostLandingState.Retained, resumeItemIndex: execution.ResumeItemIndex);
            return Task.FromResult(new ReviewedOperationExecutionResult("", ReviewedOperationState.Succeeded, true, "Onboard mission retained.", []));
        });
    public Task<ReviewedOperationSnapshot> PlanRemoveAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => PlanPostLandingDecisionAsync(ReviewedOperationKind.FlightMissionRemove, "Remove onboard mission", missionId, ResolveConnectionId(connectionId, vehicleId), vehicleId, async token =>
        {
            if (!TryGetAwaitingPostLanding(missionId, vehicleId, out var execution))
                return new ReviewedOperationExecutionResult("", ReviewedOperationState.Failed, false, "There is no post-landing mission decision waiting for this vehicle.", []);
            var result = await Executor(vehicleId).RemoveAsync(execution.ConnectionId, ResolveCommandVehicleId(vehicleId), token);
            if (!result.Succeeded) return new ReviewedOperationExecutionResult("", ReviewedOperationState.Failed, false, result.Summary, []);
            SetExecution(missionId, execution.ConnectionId, vehicleId, execution.State, execution.CurrentItemIndex, execution.ItemCount, "Onboard mission removed.", execution.ExecutorKind, execution.TerrainFallbackUsed, null, postLandingState: FlightMissionPostLandingState.Removed, resumeItemIndex: execution.ResumeItemIndex);
            return new ReviewedOperationExecutionResult("", ReviewedOperationState.Succeeded, true, "Onboard mission removed.", []);
        });

    private Task<ReviewedOperationSnapshot> PlanResumeAfterLandingAsync(string missionId, string connectionId, string vehicleId, CancellationToken cancellationToken)
        => PlanPostLandingDecisionAsync(ReviewedOperationKind.FlightMissionResume, "Resume mission after landing", missionId, connectionId, vehicleId, async token =>
        {
            if (!TryGetAwaitingPostLanding(missionId, vehicleId, out var execution) || execution.ResumeItemIndex is not { } resumeIndex)
                return new ReviewedOperationExecutionResult("", ReviewedOperationState.Failed, false, "There is no resumable landed mission waiting for this vehicle.", []);
            if (!_artifacts.TryGetValue(ExecutionKey(missionId, vehicleId), out var artifact))
                return new ReviewedOperationExecutionResult("", ReviewedOperationState.Failed, false, "The uploaded mission artifact is no longer available in this session.", []);

            var executor = Executor(vehicleId);
            var targetVehicleId = ResolveCommandVehicleId(vehicleId);
            var target = Target(vehicleId);
            var wire = executor is Px4MissionExecutor or ArduPilotMissionExecutor
                ? CompilerFor(target).Compile(Require(missionId), target)
                : null;
            var result = await executor.PrepareResumeAsync(execution.ConnectionId, targetVehicleId, artifact, wire, resumeIndex, token);
            if (!result.Succeeded) return new ReviewedOperationExecutionResult("", ReviewedOperationState.Failed, false, result.Summary, []);
            SetExecution(missionId, execution.ConnectionId, vehicleId, FlightMissionExecutionState.Uploaded, resumeIndex, artifact.Items.Count, "Mission rebuilt and uploaded for resume.", execution.ExecutorKind, execution.TerrainFallbackUsed, null, postLandingState: FlightMissionPostLandingState.ResumePrepared, resumeItemIndex: resumeIndex);
            return new ReviewedOperationExecutionResult("", ReviewedOperationState.Succeeded, true, "Mission rebuilt and uploaded. Execute Start to continue from the last meaningful item.", []);
        });

    private Task<ReviewedOperationSnapshot> PlanPostLandingDecisionAsync(
        ReviewedOperationKind kind,
        string title,
        string missionId,
        string connectionId,
        string vehicleId,
        Func<CancellationToken, Task<ReviewedOperationExecutionResult>> execute)
        => PlanAsync(kind, title, missionId, connectionId, vehicleId, false, execute);

    private bool TryGetAwaitingPostLanding(string missionId, string vehicleId, out FlightMissionExecutionSnapshot execution)
    {
        execution = _executions.GetValueOrDefault(missionId)!;
        return execution is not null && execution.VehicleId == vehicleId && execution.PostLandingState == FlightMissionPostLandingState.AwaitingDecision;
    }

    private Task<ReviewedOperationSnapshot> PlanModeAsync(ReviewedOperationKind kind, string title, string missionId, string connectionId, string vehicleId, bool paused, bool allowTerrainFallback, bool continueExisting = false)
        => PlanAsync(kind, title, missionId, connectionId, vehicleId, allowTerrainFallback, async token =>
        {
            var execution = _executions.TryGetValue(missionId, out var state) ? state : null;
            if (kind == ReviewedOperationKind.FlightMissionStart && execution?.State != FlightMissionExecutionState.Uploaded)
            {
                var targetName = Target(vehicleId)?.Name ?? vehicleId;
                return new("", ReviewedOperationState.Failed, false,
                    $"Mission '{Require(missionId).DisplayName}' has not been uploaded to {targetName} in this session. Upload the mission and execute that operation before starting it.",
                    [$"No uploaded mission artifact is available for {targetName}."]);
            }
            var executor = Executor(vehicleId);
            if (!paused && kind == ReviewedOperationKind.FlightMissionStart && Target(vehicleId)?.IsGhost == true && Target(vehicleId)?.ArmState != "Armed")
                return new("", ReviewedOperationState.Failed, false, "Arm the Ghost before starting the mission.", []);
            if (!paused && kind == ReviewedOperationKind.FlightMissionStart && Target(vehicleId) is { } startTarget &&
                startTarget.ProfileKey.Contains("ardupilot", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(startTarget.ArmState, "Armed", StringComparison.OrdinalIgnoreCase))
                return new("", ReviewedOperationState.Failed, false, "Arm the ArduPilot vehicle before starting the mission.", []);
            if (continueExisting && execution?.State != FlightMissionExecutionState.Paused)
                return new("", ReviewedOperationState.Failed, false, "Continue is available only for a paused mission.", ["MISSION_NOT_PAUSED: Pause the mission before continuing it."]);
            var result = paused
                ? await executor.PauseAsync(connectionId, ResolveCommandVehicleId(vehicleId), token)
                : continueExisting || execution?.State == FlightMissionExecutionState.Paused
                    ? await executor.ResumeAsync(connectionId, ResolveCommandVehicleId(vehicleId), token)
                    : await executor.StartAsync(connectionId, ResolveCommandVehicleId(vehicleId), token);
            if (result.Succeeded) SetExecution(missionId, connectionId, vehicleId, paused ? FlightMissionExecutionState.Paused : FlightMissionExecutionState.Running, execution?.CurrentItemIndex, execution?.ItemCount ?? 0, result.Summary, executor.ExecutorKind, execution?.TerrainFallbackUsed ?? false, null, postLandingState: FlightMissionPostLandingState.None, resumeItemIndex: null);
            return new("", result.Succeeded ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Succeeded, result.Summary, []);
        });

    private async Task<ReviewedOperationSnapshot> PlanAsync(ReviewedOperationKind kind, string title, string? missionId, string connectionId, string vehicleId, bool allowTerrainFallback, Func<CancellationToken, Task<ReviewedOperationExecutionResult>> execute)
    {
        var findings = missionId is null ? TargetFindings(vehicleId) : (await PreviewForExecutionAsync(missionId, vehicleId, allowTerrainFallback)).Findings.Concat(TargetFindings(vehicleId)).DistinctBy(item => item.Code).ToArray();
        if (kind == ReviewedOperationKind.FlightMissionStart && _units.TryGet(vehicleId, out var target) && target?.IsGhost == true && !string.Equals(target.ArmState, "Armed", StringComparison.OrdinalIgnoreCase))
            findings = findings.Append(new WorkflowFinding("GHOST_NOT_ARMED", WorkflowFindingSeverity.Blocking, "Arm the Ghost before starting the mission.")).DistinctBy(item => item.Code).ToArray();
        var plan = _reviewed.Plan(kind, title, findings, [connectionId, vehicleId], missionId is null ? title : $"{title}: {Require(missionId).DisplayName}", [connectionId, vehicleId], execute);
        return plan;
    }
    private IReadOnlyList<WorkflowFinding> TargetFindings(string vehicleId)
    {
        if (!_units.TryGet(vehicleId, out var target) || target is null)
            return [new("MISSION_TARGET_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "Select an online mission target.")];
        if (target.ProfileKey.Contains("logos", StringComparison.OrdinalIgnoreCase) || target.Domain.Contains("logos", StringComparison.OrdinalIgnoreCase))
            return [new("MISSION_EXECUTOR_UNSUPPORTED", WorkflowFindingSeverity.Blocking, "Logos mission execution is not available yet.")];
        if (_executors.Any(executor => executor is not UnsupportedMissionExecutor && executor.Supports(target))) return [];
        var backend = target.IsGhost ? "Ghost" : target.ProfileKey.Contains("ardupilot", StringComparison.OrdinalIgnoreCase) ? "ArduPilot" : target.ProfileKey.Contains("logos", StringComparison.OrdinalIgnoreCase) ? "Logos" : "This vehicle";
        return [new("MISSION_EXECUTOR_UNSUPPORTED", WorkflowFindingSeverity.Blocking, $"{backend} mission execution is not available yet.")];
    }

    private IFlightMissionExecutor Executor(string vehicleId)
    {
        var target = Target(vehicleId) ?? throw new InvalidOperationException("The selected mission target is no longer available.");
        return _executors.FirstOrDefault(executor => executor.Supports(target))
            ?? throw new InvalidOperationException("No mission executor supports the selected target.");
    }

    private async Task<FlightMissionCompilationPreview> PreviewForExecutionAsync(string missionId, string vehicleId, bool allowTerrainFallback, CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAsync(missionId, vehicleId, cancellationToken);
        var terrainCodes = new[] { "MISSION_TERRAIN_UNAVAILABLE", "MISSION_TERRAIN_VERTICAL_REFERENCE", "MISSION_TERRAIN_CLIMB_LIMIT", "MISSION_TERRAIN_HOME_UNAVAILABLE" };
        if (!allowTerrainFallback || !preview.Findings.Any(item => terrainCodes.Contains(item.Code, StringComparer.Ordinal)))
            return preview;

        var findings = preview.Findings
            .Where(item => !terrainCodes.Contains(item.Code, StringComparer.Ordinal))
            .Append(new WorkflowFinding("MISSION_TERRAIN_FALLBACK", WorkflowFindingSeverity.Warning, "Terrain data was unavailable; this run will use relative-home altitudes without terrain correction."))
            .ToArray();
        return preview with { Findings = findings, Summary = $"{preview.Summary} · terrain fallback acknowledged" };
    }

    private static FlightMissionExecutionArtifact BuildArtifact(FlightMissionDocument mission, FlightMissionCompilationPreview preview, string executorKind, bool terrainFallback, Px4FenceSnapshot? fence)
    {
        var items = new List<FlightMissionCompiledItem>();
        foreach (var step in mission.Steps)
        {
            var altitude = step.RelativeAltitudeMetres ?? mission.RelativeAltitudeMetres;
            var speed = step.CruiseSpeedMetresPerSecond ?? mission.CruiseSpeedMetresPerSecond;
            var profile = preview.TerrainProfile?.PointsByStepId.TryGetValue(step.Id, out var terrain) == true ? terrain : null;
            var coordinates = step.Kind switch
            {
                FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan => Px4FlightMissionCompiler.NavigationCoordinates(step),
                FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.TimedLoiter => Px4FlightMissionCompiler.NavigationCoordinates(step),
                _ => []
            };
            if (step.Kind is FlightMissionStepKind.CameraCaptureIntent)
            {
                items.Add(new(items.Count, step.Id, step.Kind, null, altitude, speed, SimulatedCapture: true));
                continue;
            }
            if (coordinates.Count > 0)
            {
                for (var index = 0; index < coordinates.Count; index++)
                {
                    var pointAltitude = profile is { Count: > 0 } && index < profile.Count ? profile[index].RelativeHomeAltitudeMetres : altitude;
                    items.Add(new(items.Count, step.Id, step.Kind, coordinates[index], pointAltitude, speed, step.Kind == FlightMissionStepKind.TimedLoiter ? step.LoiterDurationSeconds : null, step.CameraIntent is not null || step.Survey?.CameraIntent is not null || step.Corridor?.CameraIntent is not null));
                }
            }
            else items.Add(new(items.Count, step.Id, step.Kind, null, altitude, speed));
        }
        if (mission.EndAction == FlightMissionEndAction.ReturnToLaunch && (mission.Steps.Count == 0 || mission.Steps[^1].Kind is not (FlightMissionStepKind.ReturnToLaunch or FlightMissionStepKind.Land)))
            items.Add(new(items.Count, $"{mission.MissionId}:end-rtl", FlightMissionStepKind.ReturnToLaunch, null, mission.RelativeAltitudeMetres, mission.CruiseSpeedMetresPerSecond));
        var material = string.Join("|", items.Select(item => $"{item.Index}:{item.StepId}:{item.Kind}:{item.Coordinate?.LatitudeDegrees:R}:{item.Coordinate?.LongitudeDegrees:R}:{item.RelativeAltitudeMetres:R}:{item.CruiseSpeedMetresPerSecond:R}"));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        return new(mission.MissionId, executorKind, items, hash, terrainFallback, terrainFallback ? "Terrain fallback acknowledged for this run." : null, fence?.Document.Kind, fence?.Document.Coordinates);
    }

    private async Task<(FlightMissionTerrainProfile? Profile, IReadOnlyList<WorkflowFinding> Findings)> BuildTerrainProfileAsync(FlightMissionDocument mission, UnitObservationSnapshot? target, CancellationToken token)
    {
        if (target?.Telemetry?.LatitudeDegrees is not { } homeLatitude || target.Telemetry.LongitudeDegrees is not { } homeLongitude)
            return (null, [new("MISSION_TERRAIN_HOME_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "Terrain following requires a current PX4 global position to establish its home terrain datum.")]);

        var terrainSteps = mission.Steps.Where(step => step.TerrainFollowing).ToArray();
        var navigation = terrainSteps.ToDictionary(step => step.Id, step => Px4FlightMissionCompiler.NavigationCoordinates(step), StringComparer.Ordinal);
        var queryCoordinates = new List<TerrainCoordinate> { new(homeLatitude, homeLongitude) };
        queryCoordinates.AddRange(navigation.Values.SelectMany(points => points).Select(point => new TerrainCoordinate(point.LatitudeDegrees, point.LongitudeDegrees)));
        var result = await _terrain.GetElevationsAsync(queryCoordinates, new(TimeSpan.FromHours(24), AllowStale: false, ResolutionMetres: 30), token);
        if (result.Status != TerrainQueryStatus.Available || result.Samples.Count != queryCoordinates.Count || result.Samples.Any(sample => sample.IsStale || sample.VerticalReference is TerrainVerticalReference.Unknown or TerrainVerticalReference.ProviderNative))
            return (null, [new("MISSION_TERRAIN_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "Terrain following requires fresh, complete elevation data with a known vertical reference.")]);

        var verticalReference = result.Samples[0].VerticalReference;
        if (result.Samples.Any(sample => sample.VerticalReference != verticalReference))
            return (null, [new("MISSION_TERRAIN_VERTICAL_REFERENCE", WorkflowFindingSeverity.Blocking, "Terrain samples use incompatible vertical references.")]);

        const double maximumClimbGradient = 0.5; // 1 m vertical per 2 m horizontal is deliberately conservative for this slice.
        var homeElevation = result.Samples[0].ElevationMetres;
        var cursor = 1;
        var pointsByStep = new Dictionary<string, IReadOnlyList<FlightMissionTerrainPoint>>(StringComparer.Ordinal);
        foreach (var step in terrainSteps)
        {
            var clearance = step.RelativeAltitudeMetres ?? mission.RelativeAltitudeMetres;
            var values = navigation[step.Id].Select(point =>
            {
                var sample = result.Samples[cursor++];
                return new FlightMissionTerrainPoint(point, clearance + sample.ElevationMetres - homeElevation);
            }).ToArray();
            var smoothed = SmoothTerrainProfile(values, maximumClimbGradient);
            if (smoothed is null)
                return (null, [new("MISSION_TERRAIN_CLIMB_LIMIT", WorkflowFindingSeverity.Blocking, "Terrain following would exceed the configured PX4 climb/descent profile limit.")]);
            pointsByStep[step.Id] = smoothed;
        }
        var material = string.Join("|", pointsByStep.OrderBy(item => item.Key).SelectMany(item => item.Value.Select(point => $"{item.Key}:{point.Coordinate.LatitudeDegrees:R},{point.Coordinate.LongitudeDegrees:R},{point.RelativeHomeAltitudeMetres:R}")));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        return (new FlightMissionTerrainProfile(mission.MissionId, pointsByStep, result.Samples[0].VerticalDatum, hash, homeElevation, result.Samples.Count, $"{result.Samples.Count - 1} terrain samples"), []);
    }
    private static FlightMissionTerrainPoint[]? SmoothTerrainProfile(FlightMissionTerrainPoint[] raw, double maximumGradient)
    {
        if (raw.Length < 2) return raw;
        var altitude = raw.Select(point => point.RelativeHomeAltitudeMetres).ToArray();
        // Raising a lower point (rather than lowering a terrain-clearance point) is
        // the only safe smoothing operation: it preserves the requested clearance.
        for (var pass = 0; pass < 4; pass++)
        {
            for (var index = 1; index < altitude.Length; index++)
                altitude[index] = Math.Max(altitude[index], altitude[index - 1] - maximumGradient * DistanceMetres(raw[index - 1].Coordinate, raw[index].Coordinate));
            for (var index = altitude.Length - 2; index >= 0; index--)
                altitude[index] = Math.Max(altitude[index], altitude[index + 1] - maximumGradient * DistanceMetres(raw[index].Coordinate, raw[index + 1].Coordinate));
        }
        for (var index = 1; index < altitude.Length; index++)
            if (Math.Abs(altitude[index] - altitude[index - 1]) > maximumGradient * DistanceMetres(raw[index - 1].Coordinate, raw[index].Coordinate) + 0.01)
                return null;
        return raw.Select((point, index) => point with { RelativeHomeAltitudeMetres = altitude[index] }).ToArray();
    }
    private IReadOnlyList<WorkflowFinding> FenceFindings(
        FlightMissionDocument mission,
        IReadOnlyList<FlightMissionCoordinate> route,
        UnitObservationSnapshot? target)
    {
        if (string.IsNullOrWhiteSpace(mission.TargetAssignment?.ActiveFenceId)) return [];
        if (!_fences.TryGet(mission.TargetAssignment.ActiveFenceId, out var fence) || fence is null)
            return [new("MISSION_FENCE_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "The mission's selected PX4 fence asset is no longer available.")];

        // Fence validation must inspect the flight legs, not just their endpoints.
        // Otherwise a long leg could leave and re-enter an inclusion fence (or cross
        // an exclusion fence) without either endpoint proving the breach.
        var samples = SampleRouteForFence(route, target).ToArray();
        var enters = samples.Any(point => Contains(fence.Coordinates, point.LatitudeDegrees, point.LongitudeDegrees));
        if (fence.Kind == FenceKind.Inclusion && samples.Any(point => !Contains(fence.Coordinates, point.LatitudeDegrees, point.LongitudeDegrees)))
            return [new("MISSION_FENCE_EXIT", WorkflowFindingSeverity.Blocking, "The compiled mission route leaves its selected inclusion fence.")];
        if (fence.Kind == FenceKind.Exclusion && enters)
            return [new("MISSION_FENCE_ENTRY", WorkflowFindingSeverity.Blocking, "The compiled mission route enters its selected exclusion fence.")];
        return [];
    }
    private static IEnumerable<FlightMissionCoordinate> SampleRouteForFence(
        IReadOnlyList<FlightMissionCoordinate> route,
        UnitObservationSnapshot? target)
    {
        FlightMissionCoordinate? previous = target?.Telemetry?.LatitudeDegrees is { } latitude &&
                                             target.Telemetry.LongitudeDegrees is { } longitude
            ? new(latitude, longitude)
            : null;
        foreach (var point in route)
        {
            if (previous is { } start)
            {
                var pieces = Math.Max(1, (int)Math.Ceiling(DistanceMetres(start, point) / 25d));
                for (var part = 1; part <= pieces; part++)
                {
                    var fraction = (double)part / pieces;
                    yield return new(
                        start.LatitudeDegrees + (point.LatitudeDegrees - start.LatitudeDegrees) * fraction,
                        start.LongitudeDegrees + (point.LongitudeDegrees - start.LongitudeDegrees) * fraction);
                }
            }
            else
            {
                yield return point;
            }
            previous = point;
        }
    }
    private static bool Contains(IReadOnlyList<FlightMissionCoordinate> polygon, double latitude, double longitude)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var current = polygon[index]; var prior = polygon[previous];
            if ((current.LatitudeDegrees > latitude) != (prior.LatitudeDegrees > latitude) && longitude < (prior.LongitudeDegrees - current.LongitudeDegrees) * (latitude - current.LatitudeDegrees) / (prior.LatitudeDegrees - current.LatitudeDegrees) + current.LongitudeDegrees) inside = !inside;
        }
        return inside;
    }
    private static double DistanceMetres(FlightMissionCoordinate a, FlightMissionCoordinate b)
    {
        const double radius = 6_371_000; const double radians = Math.PI / 180d;
        var latitude = (b.LatitudeDegrees - a.LatitudeDegrees) * radians; var longitude = (b.LongitudeDegrees - a.LongitudeDegrees) * radians;
        var value = Math.Sin(latitude / 2) * Math.Sin(latitude / 2) + Math.Cos(a.LatitudeDegrees * radians) * Math.Cos(b.LatitudeDegrees * radians) * Math.Sin(longitude / 2) * Math.Sin(longitude / 2);
        return 2 * radius * Math.Atan2(Math.Sqrt(value), Math.Sqrt(1 - value));
    }
    private IMavlinkMissionClient Client(string connectionId) => _connections.TryGet(connectionId, out var connection) && connection is IMavlinkMissionClient client ? client : throw new InvalidOperationException("The selected MAVLink connection is unavailable.");
    private string ResolveMavlinkConnectionId(string requestedConnectionId, string unitId)
    {
        if (_connections.TryGet(requestedConnectionId, out var requested) && requested is IMavlinkMissionClient)
            return requestedConnectionId;
        var target = Target(unitId);
        var resolved = target?.ConnectionIds.FirstOrDefault(connectionId =>
            _connections.TryGet(connectionId, out var connection) && connection is IMavlinkMissionClient);
        return resolved ?? requestedConnectionId;
    }
    private UnitObservationSnapshot? Target(string vehicleId) { _units.TryGet(vehicleId, out var target); return target; }
    private string ResolveCommandVehicleId(string unitId) => Target(unitId)?.CommandAuthorityVehicleId ?? unitId;
    private FlightMissionDocument Require(string id) => _store.TryGet(id, out var mission) && mission is not null ? mission : throw new KeyNotFoundException($"Mission '{id}' was not found.");
    private async Task<FlightMissionSnapshot> AddStepAsync(string missionId, FlightMissionStep step, CancellationToken token)
        => Publish(await UpdateAsync(missionId, document =>
        {
            var steps = document.Steps.ToList();
            var terminalIndex = steps.FindIndex(item => item.Kind is FlightMissionStepKind.ReturnToLaunch or FlightMissionStepKind.Land);

            if (step.Kind == FlightMissionStepKind.Takeoff)
            {
                if (steps.Any(item => item.Kind == FlightMissionStepKind.Takeoff))
                    throw new InvalidOperationException("This mission already has a Takeoff step.");

                // Takeoff is always inserted at the safe, valid position. This lets an
                // operator add it after route steps without producing an invalid document.
                steps.Insert(0, step);
            }
            else if (step.Kind == FlightMissionStepKind.ReturnToLaunch)
            {
                if (steps.Any(item => item.Kind == FlightMissionStepKind.ReturnToLaunch))
                    throw new InvalidOperationException("This mission already has an RTL step.");

                // An RTL may be followed by Land. If Land was added first, retain it
                // and insert RTL immediately before it.
                var landIndex = steps.FindIndex(item => item.Kind == FlightMissionStepKind.Land);
                if (landIndex >= 0) steps.Insert(landIndex, step);
                else steps.Add(step);
            }
            else if (step.Kind == FlightMissionStepKind.Land)
            {
                if (steps.Any(item => item.Kind == FlightMissionStepKind.Land))
                    throw new InvalidOperationException("This mission already has a Land step.");

                // Landing remains final, optionally following an existing RTL.
                steps.Add(step);
            }
            else if (terminalIndex >= 0)
            {
                // Route geometry belongs before the terminal RTL/Land action.
                steps.Insert(terminalIndex, step);
            }
            else
            {
                steps.Add(step);
            }

            return document with { Steps = steps, UpdatedAt = DateTimeOffset.UtcNow };
        }, token));
    private async Task<FlightMissionDocument> UpdateAsync(string id, Func<FlightMissionDocument, FlightMissionDocument> mutate, CancellationToken token) => await _store.SaveAsync(mutate(Require(id)), true, token);
    private async Task<FlightMissionSnapshot> UpdateAndPublishAsync(string id, Func<FlightMissionDocument, FlightMissionDocument> mutate, CancellationToken token) => Publish(await UpdateAsync(id, mutate, token));
    private FlightMissionSnapshot Publish(FlightMissionDocument document) { Changed?.Invoke(this, EventArgs.Empty); return Project(document); }
    private FlightMissionSnapshot Project(FlightMissionDocument document)
    {
        var findings = _defaultCompiler.Validate(document, null);
        return new(document.MissionId, document.DisplayName, document.RelativeAltitudeMetres, document.Steps,
            Blocked(findings) ? "Invalid" : "Ready", findings, document.UpdatedAt, document.ContentSha256,
            document.CruiseSpeedMetresPerSecond, document.TargetAssignment, document.EndAction);
    }

    private IFlightMissionCompiler CompilerFor(UnitObservationSnapshot? target)
        => target is null
            ? _defaultCompiler
            : _compilers.FirstOrDefault(item => item.SupportsTarget(target)) ?? _defaultCompiler;
    private static bool Blocked(IEnumerable<WorkflowFinding> findings) => findings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking);
    private static ReviewedOperationExecutionResult Failure(string summary, IEnumerable<WorkflowFinding> findings) => new("", ReviewedOperationState.Failed, false, summary, findings.Select(item => item.Message).ToArray());
    private void SetExecution(string missionId, string connectionId, string vehicleId, FlightMissionExecutionState state, int? index, int count, string summary, string executorKind = "PX4", bool terrainFallback = false, string? terrainWarning = null, string? activeStepId = null, string? activeStepName = null, string? lastEvent = null, IReadOnlyList<FlightMissionCaptureEvent>? captures = null, FlightMissionPostLandingState postLandingState = FlightMissionPostLandingState.None, int? resumeItemIndex = null)
    {
        var now = DateTimeOffset.UtcNow;
        _executions[missionId] = new(missionId, connectionId, vehicleId, state, index, count, terrainWarning is null ? summary : $"{summary} {terrainWarning}", now, executorKind, activeStepId, activeStepName, lastEvent, terrainFallback, captures, postLandingState, resumeItemIndex);
        var commandState = state switch
        {
            FlightMissionExecutionState.Uploaded => OperationalCommandState.Accepted,
            FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused => OperationalCommandState.InProgress,
            FlightMissionExecutionState.Completed => OperationalCommandState.Succeeded,
            FlightMissionExecutionState.Interrupted => OperationalCommandState.Cancelled,
            FlightMissionExecutionState.Failed => OperationalCommandState.Failed,
            _ => OperationalCommandState.Draft
        };
        var commandId = $"flight-mission:{missionId}";
        _ = _dispatcher.InvokeAsync(() => _commands.Upsert(new OperationalCommandRecord(
            commandId, "FlightMission", "Vehicle", vehicleId, connectionId, commandState,
            Require(missionId).DisplayName, summary, commandId, now, now, vehicleId,
            Reason: state.ToString())));
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void RefreshProgress()
    {
        // Unit observation is also updated by SetExecution (the mission command
        // record is projected into the unit snapshot). The headless dispatcher is
        // synchronous, so without this guard the resulting Changed event would
        // recursively call RefreshProgress until the process stack-overflows.
        if (Interlocked.Exchange(ref _refreshingProgress, 1) != 0) return;
        try
        {
            foreach (var execution in _executions.Values.Where(item => item.State is FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused).ToArray())
                if (TryGetExecutor(execution.VehicleId, out var executor) && executor.TryGetProgress(execution.ConnectionId, ResolveCommandVehicleId(execution.VehicleId), out var progress))
                {
                    var wasActive = execution.State is FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused;
                    var postLandingState = progress.PostLandingDecisionAvailable ? FlightMissionPostLandingState.AwaitingDecision : FlightMissionPostLandingState.None;
                    SetExecution(execution.MissionId, execution.ConnectionId, execution.VehicleId, progress.State, progress.CurrentItemIndex, progress.ItemCount, progress.Summary, execution.ExecutorKind, execution.TerrainFallbackUsed, null, progress.ActiveStepId, progress.ActiveStepName, progress.LastEvent, progress.CaptureEvents, postLandingState, progress.ResumeItemIndex);
                    if (wasActive && progress.State == FlightMissionExecutionState.Completed && _completionFinalizationStarted.Add(ExecutionKey(execution.MissionId, execution.VehicleId)))
                        _ = FinalizeCompletedMissionAsync(execution, executor);
                }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Volatile.Write(ref _refreshingProgress, 0);
        }
    }

    private async Task FinalizeCompletedMissionAsync(FlightMissionExecutionSnapshot execution, IFlightMissionExecutor executor)
    {
        try
        {
            var mission = Require(execution.MissionId);
            await executor.CompleteAsync(execution.ConnectionId, ResolveCommandVehicleId(execution.VehicleId), mission.EndAction);
        }
        catch
        {
            // Completion was already confirmed by the vehicle. A best-effort
            // post-completion Hold must not turn it into a false failure.
        }
    }

    private static string ExecutionKey(string missionId, string vehicleId) => $"{missionId}:{vehicleId}";

    private bool TryGetExecutor(string vehicleId, out IFlightMissionExecutor executor)
    {
        var target = Target(vehicleId);
        executor = target is null ? null! : _executors.FirstOrDefault(item => item.Supports(target))!;
        return executor is not null;
    }

    private string ResolveConnectionId(string requestedConnectionId, string unitId)
    {
        if (_connections.TryGet(requestedConnectionId, out _)) return requestedConnectionId;
        var target = Target(unitId);
        return target?.ConnectionIds.FirstOrDefault(id => _connections.TryGet(id, out _)) ?? requestedConnectionId;
    }
}
