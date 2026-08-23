using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.Services.Simulation;

public sealed class GhostUnitService : IGhostUnitService, IHostedService
{
    private const double EarthRadiusMetres = 6378137d;
    // Match the desktop render cadence closely enough that map markers do not
    // visibly step between the coarser physics updates.
    private const double TickSeconds = 1d / 60d;
    // Physics remains 60 Hz, but observable store publication is coalesced.
    // Each publication fans out to map, unit, diagnostics, observer, and GUI
    // projections, so publishing every physics tick does not scale with a fleet.
    private static readonly TimeSpan TelemetryPublishInterval = TimeSpan.FromMilliseconds(50);
    private const int MaxMissionCaptureEvents = 1000;
    // Formation targets move continuously. Keep the positional correction
    // below normal navigation speed, then damp velocity error relative to the
    // transform feed-forward. This reaches a final hold point promptly
    // without chasing through the target after rotation or scaling completes.
    private const double FormationHorizontalPositionGain = 2.2d;
    private const double FormationVerticalPositionGain = 1d;
    private const double FormationHorizontalCorrectionLimit = 3d;
    private const double FormationVerticalCorrectionLimit = 1d;
    // Authored entry is a stationary-target approach, so it has no Team
    // translation feed-forward. Give it a faster, still profile-bounded
    // approach than the conservative steady-state correction cap.
    private const double FormationEntryHorizontalCorrectionLimit = 5d;
    private const double FormationEntryVerticalCorrectionLimit = 1.5d;
    private const double FormationVelocityDamping = 1.2d;
    private const double FormationFinalHoldHorizontalBraking = 20d;
    private const double FormationFinalHoldVerticalBraking = 8d;

    private readonly object _gate = new();
    private readonly Dictionary<string, GhostState> _ghosts = new(StringComparer.Ordinal);
    private readonly IEntityStore<string, ConnectionRecord> _connections;
    private readonly IEntityStore<string, RuntimeRecord> _runtimes;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, LinkRecord>? _links;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot> _diagnostics;
    private readonly IEntityStore<string, CameraSourceRecord>? _cameraSources;
    private readonly IEntityStore<string, CameraStreamRecord>? _cameraStreams;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private readonly ISelectionService _selection;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppConfiguration _configuration;
    private readonly IGhostProfileWorkflow? _profiles;
    private readonly ILogger<GhostUnitService>? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly IStorePublicationGate _storePublicationGate;
    private Task? _loop;
    private Task? _telemetryLoop;
    private int _telemetryDirty;
    private int _nextNumber;
    private long _lastSlowPublicationTimestamp;
    private bool _stopped;
    private int _disposed;

    public GhostUnitService(
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, RuntimeRecord> runtimes,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, OperationalCommandRecord> commands,
        ISelectionService selection,
        IUiDispatcher dispatcher,
        AppConfiguration configuration,
        ILogger<GhostUnitService>? logger = null,
        IStorePublicationGate? storePublicationGate = null,
        IEntityStore<string, VehicleDiagnosticsSnapshot>? diagnostics = null,
        IEntityStore<string, LinkRecord>? links = null,
        IEntityStore<string, CameraSourceRecord>? cameraSources = null,
        IEntityStore<string, CameraStreamRecord>? cameraStreams = null,
        IGhostProfileWorkflow? profiles = null)
    {
        _connections = connections;
        _runtimes = runtimes;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _links = links;
        _cameraSources = cameraSources;
        _cameraStreams = cameraStreams;
        _diagnostics = diagnostics ?? new EntityStore<string, VehicleDiagnosticsSnapshot>(item => item.Id, StringComparer.Ordinal);
        _commands = commands;
        _selection = selection;
        _dispatcher = dispatcher;
        _configuration = configuration;
        _profiles = profiles;
        _logger = logger;
        _storePublicationGate = storePublicationGate ?? new StorePublicationGate();
    }

    public IReadOnlyList<string> GhostVehicleIds
    {
        get
        {
            lock (_gate)
            {
                return _ghosts.Keys.ToArray();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _loop = Task.Run(() => SimulationLoopAsync(_shutdown.Token), _shutdown.Token);
        _telemetryLoop = Task.Run(() => TelemetryPublicationLoopAsync(_shutdown.Token), _shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            lock (_gate)
            {
                foreach (var ghost in _ghosts.Values)
                {
                    if (ghost.Mission is { State: FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused } mission)
                    {
                        mission.State = FlightMissionExecutionState.Interrupted;
                        mission.LastEvent = "Ghost mission interrupted by shutdown.";
                    }
                }
            }
            _shutdown.Cancel();
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { }
            }
            if (_telemetryLoop is not null)
            {
                try { await _telemetryLoop.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public bool IsGhostConnection(string connectionId)
        => connectionId.StartsWith("ghost-", StringComparison.Ordinal);

    public bool IsGhostVehicle(string vehicleId)
    {
        lock (_gate) return _ghosts.ContainsKey(vehicleId);
    }

    public Task ApplyFenceAsync(string vehicleId, FenceDocument fence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FenceLibraryStore.Validate(fence);
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost))
                throw new KeyNotFoundException("Ghost unit was not found.");
            ghost.ActiveFence = fence;
            ghost.FenceBreachReported = false;
        }
        Volatile.Write(ref _telemetryDirty, 1);
        return Task.CompletedTask;
    }

    public Task<FenceDocument?> DownloadFenceAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost))
                throw new KeyNotFoundException("Ghost unit was not found.");
            return Task.FromResult(ghost.ActiveFence);
        }
    }

    public Task ClearFenceAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost))
                throw new KeyNotFoundException("Ghost unit was not found.");
            ghost.ActiveFence = null;
            ghost.FenceBreachReported = false;
        }
        Volatile.Write(ref _telemetryDirty, 1);
        return Task.CompletedTask;
    }

    public async Task<VehicleRecord> CreateAsync(
        MapViewportSnapshot? viewport = null,
        CancellationToken cancellationToken = default)
        => await CreateAsync("dracula", viewport, 90, cancellationToken);

    public Task<VehicleRecord> CreateAsync(
        string profileId,
        MapViewportSnapshot? viewport = null,
        CancellationToken cancellationToken = default)
        => CreateAsync(profileId, viewport, 90, cancellationToken);

    public async Task<VehicleRecord> CreateAsync(
        MapViewportSnapshot? viewport,
        double headingDegrees,
        CancellationToken cancellationToken = default)
        => await CreateAsync("dracula", viewport, headingDegrees, cancellationToken);

    public async Task<VehicleRecord> CreateAsync(
        string profileId,
        MapViewportSnapshot? viewport,
        double headingDegrees,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(headingDegrees)) throw new ArgumentOutOfRangeException(nameof(headingDegrees));
        var profile = ResolveProfile(profileId);
        GhostState ghost;
        lock (_gate)
        {
            var number = ++_nextNumber;
            var id = $"ghost-{number}";
            var center = viewport is { LatitudeDegrees: >= -90 and <= 90, LongitudeDegrees: >= -180 and <= 180 }
                ? viewport
                : new MapViewportSnapshot(
                    _configuration.MapDefaultLongitude,
                    _configuration.MapDefaultLatitude,
                    _configuration.MapDefaultResolution,
                    0);
            ghost = new GhostState(
                id,
                $"Ghost {number}",
                $"ghost-connection-{number}",
                profile,
                center.LatitudeDegrees,
                center.LongitudeDegrees,
                center.LatitudeDegrees,
                center.LongitudeDegrees,
                0,
                0,
                NormalizeHeading(headingDegrees),
                false,
                true);
            _ghosts.Add(id, ghost);
        }

        await PublishAsync(cancellationToken);
        return ToVehicle(ghost);
    }

    private GhostProfileSnapshot ResolveProfile(string profileId)
    {
        var profile = _profiles?.Find(profileId) ?? (string.Equals(profileId, GhostProfileDefaults.Dracula.Id, StringComparison.OrdinalIgnoreCase) ? GhostProfileDefaults.Dracula : null);
        return profile ?? throw new ArgumentException($"Unknown Ghost profile '{profileId}'.", nameof(profileId));
    }

    public async Task DeleteAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        GhostState? ghost;
        lock (_gate)
        {
            if (!_ghosts.Remove(vehicleId, out ghost)) return;
            if (ghost.Operation is not null)
            {
                UpdateCommand(
                    ghost.Operation.CommandId,
                    OperationalCommandState.Cancelled,
                    "Ghost unit deleted while the operation was active.",
                    "GHOST_DELETED");
                ghost.Operation = null;
            }
            if (ghost.Mission is { State: FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused } mission)
            {
                mission.State = FlightMissionExecutionState.Interrupted;
                mission.LastEvent = "Ghost mission interrupted because the unit was deleted.";
            }
        }

        var remainingSelection = _selection.SelectedUnitIds
            .Where(id => !string.Equals(id, vehicleId, StringComparison.Ordinal))
            .ToArray();
        if (remainingSelection.Length != _selection.SelectedUnitIds.Count)
        {
            var selections = remainingSelection
                .Select(id => _vehicles.TryGet(id, out var vehicle) ? vehicle : null)
                .Where(vehicle => vehicle is not null)
                .Cast<VehicleRecord>()
                .Select(SelectionFactory.From)
                .ToArray();
            var anchor = string.Equals(_selection.UnitSelectionAnchorId, vehicleId, StringComparison.Ordinal)
                ? remainingSelection.FirstOrDefault()
                : _selection.UnitSelectionAnchorId;
            _selection.SetUnitSelection(selections, anchor);
        }

        await PublishAsync(cancellationToken);
    }

    public Task DeleteByConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        string? vehicleId;
        lock (_gate)
        {
            vehicleId = _ghosts.Values
                .Where(item => item.ConnectionId == connectionId)
                .Select(item => item.VehicleId)
                .FirstOrDefault();
        }

        return vehicleId is null ? Task.CompletedTask : DeleteAsync(vehicleId, cancellationToken);
    }

    public OperatorPolicyEvaluation EvaluatePolicy(OperatorPolicyRequest request)
        => new(
            true,
            true,
            "SIMULATED_ALLOW",
            "Ghost-unit operator action allowed by the local simulator.",
            [new("SIMULATED_POLICY", "Info", "No Logos policy server is contacted for a ghost unit.")]);

    public Task<OperatorCommandPreparationResult> PrepareAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(request.Target.VehicleId, out var ghost))
            {
                return Task.FromResult(OperatorCommandPreparationResult.Rejected("Ghost unit was deleted."));
            }

            var now = DateTimeOffset.UtcNow;
            var preparation = new PreparedVehicleOperation(
                new PreparedOperationReference($"ghost-prep-{Guid.NewGuid():N}", $"ghost-token-{Guid.NewGuid():N}"),
                new PreparedOperationTargetSnapshot(
                    $"ghost-runtime-{ghost.Number}", ghost.VehicleId, ghost.ConnectionId, null, null, null, null, 0),
                request.Command.ToString(),
                "SIMULATED_ALLOW",
                "READY",
                [],
                now,
                now.AddSeconds(30));
            return Task.FromResult(new OperatorCommandPreparationResult(
                true,
                "Ghost operation prepared locally.",
                preparation,
                []));
        }
    }

    public async Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GhostState? ghost;
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(request.Target.VehicleId, out ghost))
            {
                return new(false, OperationalCommandState.Rejected, "Ghost unit was deleted.");
            }

            if (ghost.Operation is not null)
            {
                UpdateCommand(ghost.Operation.CommandId, OperationalCommandState.Cancelled,
                    $"Replaced by ghost operation {request.CommandId}.", "OPERATOR_OVERRIDE");
            }
            if (ghost.Mission is { State: FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused } mission)
            {
                mission.State = FlightMissionExecutionState.Interrupted;
                mission.LastEvent = $"Ghost mission interrupted by {request.Command}.";
            }

            if (request.Command == OperatorCommandKind.Arm) ghost.Armed = true;
            if (request.Command == OperatorCommandKind.Disarm) ghost.Armed = false;
            ghost.Operation = new GhostOperation(request.CommandId, request.Command, request.Parameters ?? OperatorCommandParameters.None);
            if (request.Command is OperatorCommandKind.Arm or OperatorCommandKind.Disarm)
            {
                ghost.Operation = null;
                UpdateCommand(request.CommandId, OperationalCommandState.Succeeded, "Ghost arm state updated.");
            }
        }

        await PublishAsync(cancellationToken);
        return new(true, request.Command is OperatorCommandKind.Arm or OperatorCommandKind.Disarm
            ? OperationalCommandState.Succeeded
            : OperationalCommandState.InProgress,
            "Ghost operation started.", request.CommandId);
    }

    public async Task<bool> BeginManualControlAsync(string vehicleId, string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost))
                return false;

            if (ghost.Operation is { } operation)
            {
                UpdateCommand(operation.CommandId, OperationalCommandState.Cancelled,
                    "Cancelled because manual control took ownership.", "MANUAL_OVERRIDE");
                ghost.Operation = null;
            }
            if (ghost.Mission is { State: FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused } mission)
            {
                mission.State = FlightMissionExecutionState.Interrupted;
                mission.LastEvent = "Ghost mission interrupted by manual control.";
            }

            ghost.ManualSessionId = sessionId;
            ghost.ManualSetpoint = ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow);
            ghost.ManualHold = true;
            ghost.NorthVelocity = 0;
            ghost.EastVelocity = 0;
            ghost.VerticalVelocity = 0;
            ghost.YawRate = 0;
        }
        await PublishAsync(cancellationToken);
        return true;
    }

    public void UpdateManualControl(string vehicleId, string sessionId, ManualControlSetpoint setpoint)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost) ||
                !string.Equals(ghost.ManualSessionId, sessionId, StringComparison.Ordinal))
                return;
            ghost.ManualSetpoint = setpoint;
            ghost.ManualHold = !setpoint.DeadmanPressed;
        }
    }

    public async Task EndManualControlAsync(string vehicleId, string sessionId, string reason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost) ||
                !string.Equals(ghost.ManualSessionId, sessionId, StringComparison.Ordinal))
                return;
            ghost.ManualSessionId = null;
            ghost.ManualSetpoint = null;
            ghost.ManualHold = false;
            ghost.NorthVelocity = 0;
            ghost.EastVelocity = 0;
            ghost.VerticalVelocity = 0;
            ghost.YawRate = 0;
        }
        await PublishTelemetryAsync(cancellationToken);
    }

    public async Task SetFormationTargetAsync(string vehicleId, GhostFormationTarget target, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost))
                throw new KeyNotFoundException($"Ghost '{vehicleId}' was not found.");
            // FormationLockWorkflow refreshes the absolute member setpoint on
            // every physics tick. Preserve the current velocity while the
            // target belongs to the same lock; resetting it here would make
            // the bounded controller restart from zero every tick and the
            // member would barely move.
            var newFormationLock = ghost.FormationTarget is null ||
                !string.Equals(ghost.FormationTarget.LockId, target.LockId, StringComparison.Ordinal);
            ghost.FormationTarget = target;
            ghost.Operation = null;
            if (newFormationLock)
            {
                ghost.NorthVelocity = 0;
                ghost.EastVelocity = 0;
                ghost.VerticalVelocity = 0;
                ghost.YawRate = 0;
            }
        }
        Volatile.Write(ref _telemetryDirty, 1);
        await Task.CompletedTask;
    }

    public async Task ClearFormationTargetAsync(string vehicleId, string lockId, bool hold, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost) ||
                !string.Equals(ghost.FormationTarget?.LockId, lockId, StringComparison.Ordinal))
                return;
            ghost.FormationTarget = null;
            if (hold)
            {
                ghost.NorthVelocity = 0;
                ghost.EastVelocity = 0;
                ghost.VerticalVelocity = 0;
                ghost.YawRate = 0;
            }
        }
        Volatile.Write(ref _telemetryDirty, 1);
        await Task.CompletedTask;
    }

    public async Task<FlightMissionExecutorResult> PrepareMissionAsync(string vehicleId, FlightMissionExecutionArtifact artifact, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost))
                return new(false, "Ghost unit was deleted.", "GHOST_DELETED");
            ghost.Mission = new GhostMissionRuntime(artifact);
        }
        await PublishTelemetryAsync(cancellationToken);
        return new(true, artifact.TerrainFallbackUsed ? "Ghost mission artifact prepared with terrain fallback acknowledged." : "Ghost mission artifact prepared.");
    }

    public async Task<FlightMissionExecutorResult> StartMissionAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost)) return new(false, "Ghost unit was deleted.", "GHOST_DELETED");
            if (!ghost.Armed) return new(false, "Arm the Ghost before starting the mission.", "GHOST_NOT_ARMED");
            if (ghost.Mission is null) return new(false, "Prepare the Ghost mission before starting it.", "GHOST_MISSION_NOT_PREPARED");
            ghost.Mission.State = FlightMissionExecutionState.Running;
            ghost.Mission.LastEvent = "Ghost mission started.";
        }
        await PublishTelemetryAsync(cancellationToken);
        return new(true, "Ghost mission running.");
    }

    public async Task<FlightMissionExecutorResult> PauseMissionAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost)) return new(false, "Ghost unit was deleted.", "GHOST_DELETED");
            if (ghost.Mission is null) return new(false, "No prepared Ghost mission is available.", "GHOST_MISSION_NOT_PREPARED");
            ghost.Mission.State = FlightMissionExecutionState.Paused;
            ghost.Mission.LastEvent = "Ghost mission paused; vehicle is holding position.";
            ghost.NorthVelocity = ghost.EastVelocity = ghost.VerticalVelocity = ghost.YawRate = 0;
        }
        await PublishTelemetryAsync(cancellationToken);
        return new(true, "Ghost mission paused; vehicle is holding position.");
    }

    public async Task<FlightMissionExecutorResult> ResumeMissionAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost)) return new(false, "Ghost unit was deleted.", "GHOST_DELETED");
            if (ghost.Mission is null) return new(false, "No prepared Ghost mission is available.", "GHOST_MISSION_NOT_PREPARED");
            ghost.Mission.State = FlightMissionExecutionState.Running;
            ghost.Mission.LastEvent = "Ghost mission resumed.";
        }
        await PublishTelemetryAsync(cancellationToken);
        return new(true, "Ghost mission resumed.");
    }

    public async Task<FlightMissionExecutorResult> PrepareMissionForResumeAsync(string vehicleId, FlightMissionExecutionArtifact artifact, int resumeItemIndex, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost)) return new(false, "Ghost unit was deleted.", "GHOST_DELETED");
            ghost.Mission = new GhostMissionRuntime(artifact)
            {
                CurrentItemIndex = Math.Clamp(resumeItemIndex, 0, Math.Max(0, artifact.Items.Count - 1)),
                LastEvent = "Mission rebuilt for resume."
            };
        }
        await PublishTelemetryAsync(cancellationToken);
        return new(true, "Ghost mission rebuilt and prepared for resume.");
    }

    public async Task<FlightMissionExecutorResult> ClearMissionAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost)) return new(false, "Ghost unit was deleted.", "GHOST_DELETED");
            ghost.Mission = null;
        }
        await PublishTelemetryAsync(cancellationToken);
        return new(true, "Ghost onboard mission removed.");
    }

    public bool TryGetMissionProgress(string vehicleId, out FlightMissionExecutorProgress progress)
    {
        lock (_gate)
        {
            if (!_ghosts.TryGetValue(vehicleId, out var ghost) || ghost.Mission is not { } mission)
            {
                progress = default!;
                return false;
            }
            var item = mission.CurrentItemIndex < mission.Artifact.Items.Count ? mission.Artifact.Items[mission.CurrentItemIndex] : null;
            var awaitingDecision = mission.State == FlightMissionExecutionState.Completed && mission.Artifact.Items.LastOrDefault()?.Kind == FlightMissionStepKind.Land;
            progress = new(mission.State, mission.CurrentItemIndex, mission.Artifact.Items.Count, mission.LastEvent ?? "Ghost mission", item?.StepId, item?.Kind.ToString(), mission.LastEvent, mission.Captures.ToArray(), awaitingDecision, awaitingDecision ? FindResumeIndex(mission.Artifact.Items) : null);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None);
        _stopGate.Dispose();
        _publishGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task SimulationLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var changed = false;
                    lock (_gate)
                    {
                        // The simulation lock also guards create/delete, so a
                        // per-tick array copy is unnecessary. Avoiding that
                        // allocation matters once the fleet reaches dozens of
                        // moving Ghosts.
                        foreach (var ghost in _ghosts.Values)
                        {
                            changed |= Step(ghost);
                        }
                    }

                    if (changed)
                    {
                        // The physics state is still advanced at 60 Hz. A separate
                        // coalescing loop publishes telemetry at a bounded rate so
                        // moving several Ghosts cannot overwhelm observable stores.
                        Volatile.Write(ref _telemetryDirty, 1);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A render/store failure must not silently kill the physics
                    // loop and leave an operation permanently reporting GoTo.
                    _logger?.LogError(ex, "Ghost simulation tick failed");
                    FailActiveOperations(ex);
                    Volatile.Write(ref _telemetryDirty, 1);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TelemetryPublicationLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TelemetryPublishInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (Interlocked.Exchange(ref _telemetryDirty, 0) == 0)
                {
                    continue;
                }

                try
                {
                    await PublishTelemetryAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ghost telemetry publication failed");
                    Volatile.Write(ref _telemetryDirty, 1);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void FailActiveOperations(Exception exception)
    {
        lock (_gate)
        {
            foreach (var ghost in _ghosts.Values)
            {
                if (ghost.Mission is { State: FlightMissionExecutionState.Running } mission)
                {
                    mission.State = FlightMissionExecutionState.Interrupted;
                    mission.LastEvent = $"Ghost mission interrupted: {exception.Message}";
                }
                if (ghost.Operation is not { } operation)
                    continue;

                UpdateCommand(
                    operation.CommandId,
                    OperationalCommandState.Failed,
                    $"Ghost simulation failed: {exception.Message}",
                    "GHOST_SIMULATION_FAILED");
                ghost.Operation = null;
            }
        }
    }

    private bool Step(GhostState ghost)
    {
        if (EnforceActiveFence(ghost))
            return true;
        if (ghost.FormationTarget is { } formation)
            return StepFormation(ghost, formation);
        if (ghost.Mission is { State: FlightMissionExecutionState.Running } mission)
            return StepMission(ghost, mission);
        // Discrete controller commands use the ordinary Ghost operation path.
        // They temporarily own motion, then the retained manual lease resumes in Hold.
        if (ghost.Operation is null && ghost.ManualSessionId is not null)
            return StepManual(ghost);
        if (ghost.Operation is null) return false;
        var operation = ghost.Operation;
        var completed = false;
        var north = 0d;
        var east = 0d;
        switch (operation.Command)
        {
            case OperatorCommandKind.GoTo:
                if (operation.Parameters.GoToTargetKind == OperatorGoToTargetKind.GlobalWgs84 &&
                    operation.Parameters.GoToLatitudeDegrees is { } lat &&
                    operation.Parameters.GoToLongitudeDegrees is { } lon)
                {
                    north = (lat - ghost.Latitude) * Math.PI / 180d * EarthRadiusMetres;
                    east = (lon - ghost.Longitude) * Math.PI / 180d * EarthRadiusMetres *
                        Math.Cos(ghost.Latitude * Math.PI / 180d);
                }
                else
                {
                    north = (operation.Parameters.GoToNorthMetres ?? ghost.LocalNorth) - ghost.LocalNorth;
                    east = (operation.Parameters.GoToEastMetres ?? ghost.LocalEast) - ghost.LocalEast;
                }
                var distance = Math.Sqrt((north * north) + (east * east));
                if (distance <= Math.Max(0.1, operation.Parameters.GoToAcceptanceRadiusMetres ?? 2)) completed = true;
                else
                {
                    var travelHeading = NormalizeHeading(Math.Atan2(east, north) * 180d / Math.PI);
                    ghost.Heading = MoveHeading(ghost.Heading, travelHeading, ghost.Profile.Simulation.MaximumYawRateDegreesPerSecond * TickSeconds);
                    var step = Math.Min(distance, ghost.Profile.Simulation.MaximumHorizontalSpeedMetresPerSecond * TickSeconds);
                    ghost.Latitude += (north / distance) * step / EarthRadiusMetres * 180d / Math.PI;
                    ghost.Longitude += (east / distance) * step / (EarthRadiusMetres * Math.Cos(ghost.Latitude * Math.PI / 180d)) * 180d / Math.PI;
                    ghost.LocalNorth += (north / distance) * step;
                    ghost.LocalEast += (east / distance) * step;
                }
                break;
            case OperatorCommandKind.ChangeAltitude:
                var targetAltitude = operation.Parameters.AltitudeTargetKind switch
                {
                    OperatorAltitudeTargetKind.AltitudeAmsl => (operation.Parameters.AltitudeAmslMetres ?? ghost.GroundAltitude) - ghost.GroundAltitude,
                    OperatorAltitudeTargetKind.RelativeDelta => ghost.AltitudeAgl + (operation.Parameters.AltitudeRelativeDeltaMetres ?? 0),
                    _ => operation.Parameters.AltitudeAglMetres ?? ghost.AltitudeAgl
                };
                completed = MoveAltitude(ghost, targetAltitude);
                break;
            case OperatorCommandKind.Takeoff:
                ghost.Armed = true;
                completed = MoveAltitude(ghost, operation.Parameters.TakeoffAltitudeAglMetres ?? 5);
                break;
            case OperatorCommandKind.Land:
                completed = MoveAltitude(ghost, 0);
                if (completed) ghost.Armed = false;
                break;
            case OperatorCommandKind.SetHeading:
                var targetHeading = operation.Parameters.HeadingTargetKind == OperatorHeadingTargetKind.RelativeYaw
                    ? ghost.Heading + (operation.Parameters.RelativeYawDegrees ?? 0)
                    : operation.Parameters.HeadingDegrees ?? ghost.Heading;
                var delta = NormalizeSigned(targetHeading - ghost.Heading);
                var turn = Math.Min(Math.Abs(delta), ghost.Profile.Simulation.MaximumYawRateDegreesPerSecond * TickSeconds);
                ghost.Heading = NormalizeHeading(ghost.Heading + Math.Sign(delta) * turn);
                completed = Math.Abs(NormalizeSigned(targetHeading - ghost.Heading)) < 0.5;
                break;
            case OperatorCommandKind.Recover:
                north = (ghost.HomeLatitude - ghost.Latitude) * Math.PI / 180d * EarthRadiusMetres;
                east = (ghost.HomeLongitude - ghost.Longitude) * Math.PI / 180d * EarthRadiusMetres * Math.Cos(ghost.Latitude * Math.PI / 180d);
                var homeDistance = Math.Sqrt((north * north) + (east * east));
                // Robot Command's generic RTL/Recover action returns to the
                // launch position and then holds. It must not implicitly land
                // or disarm; Land remains the explicit landing command.
                completed = homeDistance <= 2;
                if (!completed && homeDistance > 0)
                {
                    var homeStep = Math.Min(homeDistance, ghost.Profile.Simulation.MaximumHorizontalSpeedMetresPerSecond * TickSeconds);
                    ghost.Latitude += north / homeDistance * homeStep / EarthRadiusMetres * 180d / Math.PI;
                    ghost.Longitude += east / homeDistance * homeStep / (EarthRadiusMetres * Math.Cos(ghost.Latitude * Math.PI / 180d)) * 180d / Math.PI;
                    ghost.NorthVelocity = north / homeDistance * homeStep / TickSeconds;
                    ghost.EastVelocity = east / homeDistance * homeStep / TickSeconds;
                }
                else
                {
                    ghost.NorthVelocity = ghost.EastVelocity = ghost.VerticalVelocity = 0;
                }
                break;
            case OperatorCommandKind.Hold:
                completed = true;
                break;
        }

        if (completed)
        {
            UpdateCommand(operation.CommandId, OperationalCommandState.Succeeded, "Ghost operation completed.");
            ghost.Operation = null;
            if (operation.Command is OperatorCommandKind.Land)
            {
                ghost.Armed = false;
            }
            return true;
        }

        return true;
    }

    private bool EnforceActiveFence(GhostState ghost)
    {
        var fence = ghost.ActiveFence;
        if (fence is null) return false;
        var point = new FlightMissionCoordinate(ghost.Latitude, ghost.Longitude);
        var inside = Contains(fence.Coordinates, point);
        var breach = fence.Kind == FenceKind.Inclusion ? !inside : inside;
        breach |= fence.MinimumAltitudeMetres is { } minimum && ghost.AltitudeAgl < minimum;
        breach |= fence.MaximumAltitudeMetres is { } maximum && ghost.AltitudeAgl > maximum;
        if (!breach) return false;

        ghost.FormationTarget = null;
        ghost.NorthVelocity = ghost.EastVelocity = ghost.VerticalVelocity = 0;
        if (ghost.Mission is { State: FlightMissionExecutionState.Running } mission)
        {
            mission.State = FlightMissionExecutionState.Interrupted;
            mission.LastEvent = "Ghost mission interrupted by a local fence breach.";
        }
        if (ghost.Operation is { } operation)
        {
            UpdateCommand(operation.CommandId, OperationalCommandState.Failed,
                "Ghost operation stopped by a local fence breach.", "FENCE_BREACH");
            ghost.Operation = null;
        }
        if (!ghost.FenceBreachReported)
            ghost.FenceBreachReported = true;
        return true;
    }

    private static bool StepFormation(GhostState ghost, GhostFormationTarget target)
    {
        // A formation setpoint is intentionally owned by this loop rather than
        // by a UI timer. The target is an absolute point derived from the
        // stable virtual Team position plus the member's captured offset.
        var north = (target.LatitudeDegrees - ghost.Latitude) * Math.PI / 180d * EarthRadiusMetres;
        var east = (target.LongitudeDegrees - ghost.Longitude) * Math.PI / 180d * EarthRadiusMetres * Math.Cos(ghost.Latitude * Math.PI / 180d);
        var horizontalDistance = Math.Sqrt(north * north + east * east);
        // Formation motion includes a controller-owned feed-forward velocity
        // (tangential during rotation and radial/up during scaling). Keep a
        // bounded positional correction so a Ghost tracks the shape without
        // cutting across a commanded circular arc.
        var correctionLimit = target.IsEntryTransition
            ? FormationEntryHorizontalCorrectionLimit
            : FormationHorizontalCorrectionLimit;
        var correctionSpeed = Math.Min(correctionLimit,
            horizontalDistance * FormationHorizontalPositionGain);
        var correctionNorth = horizontalDistance <= 0.05 ? 0 : north / horizontalDistance * correctionSpeed;
        var correctionEast = horizontalDistance <= 0.05 ? 0 : east / horizontalDistance * correctionSpeed;
        var desiredNorth = target.VelocityNorthMetresPerSecond + correctionNorth -
            FormationVelocityDamping * (ghost.NorthVelocity - target.VelocityNorthMetresPerSecond);
        var desiredEast = target.VelocityEastMetresPerSecond + correctionEast -
            FormationVelocityDamping * (ghost.EastVelocity - target.VelocityEastMetresPerSecond);
        var desiredMagnitude = Math.Sqrt(desiredNorth * desiredNorth + desiredEast * desiredEast);
        var maximumHorizontalSpeed = ghost.Profile.Simulation.MaximumHorizontalSpeedMetresPerSecond;
        var maximumVerticalSpeed = Math.Min(ghost.Profile.Simulation.MaximumClimbRateMetresPerSecond, ghost.Profile.Simulation.MaximumDescentRateMetresPerSecond);
        if (desiredMagnitude > maximumHorizontalSpeed)
        {
            desiredNorth = desiredNorth / desiredMagnitude * maximumHorizontalSpeed;
            desiredEast = desiredEast / desiredMagnitude * maximumHorizontalSpeed;
        }
        var altitudeError = target.AltitudeAglMetres - ghost.AltitudeAgl;
        var verticalCorrectionLimit = target.IsEntryTransition
            ? FormationEntryVerticalCorrectionLimit
            : FormationVerticalCorrectionLimit;
        var verticalCorrection = Math.Clamp(altitudeError * FormationVerticalPositionGain,
            -verticalCorrectionLimit, verticalCorrectionLimit);
        var desiredVertical = Math.Clamp(target.VelocityUpMetresPerSecond + verticalCorrection -
            FormationVelocityDamping * (ghost.VerticalVelocity - target.VelocityUpMetresPerSecond), -maximumVerticalSpeed, maximumVerticalSpeed);

        // Use the same bounded acceleration model as physical manual input.
        // Formation translation changes positions, not member yaw/orientation.
        // Formation targets can transition from a moving transform to a
        // stationary hold in one physics tick. Use a dedicated, bounded
        // formation response so retained tangential velocity is actively
        // braked instead of carrying the Ghost past the final shape.
        var finalHold = Math.Abs(target.VelocityNorthMetresPerSecond) < 0.001d &&
                        Math.Abs(target.VelocityEastMetresPerSecond) < 0.001d &&
                        Math.Abs(target.VelocityUpMetresPerSecond) < 0.001d;
        var horizontalAcceleration = finalHold ? FormationFinalHoldHorizontalBraking : ghost.Profile.Simulation.HorizontalAccelerationMetresPerSecondSquared;
        var verticalAcceleration = finalHold ? FormationFinalHoldVerticalBraking : ghost.Profile.Simulation.VerticalAccelerationMetresPerSecondSquared;
        var nextNorthVelocity = MoveTowards(ghost.NorthVelocity, desiredNorth, horizontalAcceleration * TickSeconds);
        var nextEastVelocity = MoveTowards(ghost.EastVelocity, desiredEast, horizontalAcceleration * TickSeconds);
        var nextVerticalVelocity = MoveTowards(ghost.VerticalVelocity, desiredVertical, verticalAcceleration * TickSeconds);
        ghost.YawRate = MoveTowards(ghost.YawRate, 0, 180 * TickSeconds);

        var movedNorth = nextNorthVelocity * TickSeconds;
        var movedEast = nextEastVelocity * TickSeconds;
        // A final hold is an absolute stop condition. Do not permit an
        // integration step to cross the fixed target and start another orbit.
        // Snapping only happens after feed-forward is zero, never while a
        // translation, rotation, or resize is actively under way.
        var remainingNorth = north - movedNorth;
        var remainingEast = east - movedEast;
        if (finalHold && (horizontalDistance <= 0.05d || north * remainingNorth + east * remainingEast <= 0d))
        {
            movedNorth = north;
            movedEast = east;
            nextNorthVelocity = 0d;
            nextEastVelocity = 0d;
        }
        var altitudeErrorAfterMove = altitudeError - nextVerticalVelocity * TickSeconds;
        if (finalHold && (Math.Abs(altitudeError) <= 0.01d || altitudeError * altitudeErrorAfterMove <= 0d))
        {
            nextVerticalVelocity = 0d;
        }

        ghost.NorthVelocity = nextNorthVelocity;
        ghost.EastVelocity = nextEastVelocity;
        ghost.VerticalVelocity = nextVerticalVelocity;
        ghost.Latitude += movedNorth / EarthRadiusMetres * 180d / Math.PI;
        ghost.Longitude += movedEast / (EarthRadiusMetres * Math.Cos(ghost.Latitude * Math.PI / 180d)) * 180d / Math.PI;
        ghost.LocalNorth += movedNorth;
        ghost.LocalEast += movedEast;
        ghost.AltitudeAgl = Math.Max(0, ghost.AltitudeAgl + ghost.VerticalVelocity * TickSeconds);
        if (finalHold && (Math.Abs(altitudeError) <= 0.01d || altitudeError * altitudeErrorAfterMove <= 0d))
            ghost.AltitudeAgl = Math.Max(0, target.AltitudeAglMetres);
        ghost.Landed = false;
        // A retained formation target owns the Ghost continuously. Report the
        // tick as changed even while close to the point so the coalesced
        // telemetry publisher reflects current position and velocity instead
        // of leaving the map on an early sample until another target arrives.
        return true;
    }

    private bool StepMission(GhostState ghost, GhostMissionRuntime mission)
    {
        if (ghost.ManualSessionId is not null)
        {
            mission.State = FlightMissionExecutionState.Interrupted;
            mission.LastEvent = "Ghost mission interrupted by manual control.";
            return StepManual(ghost);
        }
        if (mission.CurrentItemIndex >= mission.Artifact.Items.Count)
        {
            mission.State = FlightMissionExecutionState.Completed;
            mission.LastEvent = "Ghost mission completed.";
            return true;
        }

        if (mission.Artifact.FenceCoordinates is { Count: >= 3 } liveFence &&
            ((mission.Artifact.FenceKind == Px4FenceKind.Inclusion && !Contains(liveFence, new FlightMissionCoordinate(ghost.Latitude, ghost.Longitude))) ||
             (mission.Artifact.FenceKind == Px4FenceKind.Exclusion && Contains(liveFence, new FlightMissionCoordinate(ghost.Latitude, ghost.Longitude)))))
        {
            mission.State = FlightMissionExecutionState.Interrupted;
            mission.LastEvent = "Mission interrupted by a local geofence breach.";
            ghost.NorthVelocity = ghost.EastVelocity = ghost.VerticalVelocity = ghost.YawRate = 0;
            return true;
        }
        var item = mission.Artifact.Items[mission.CurrentItemIndex];
        if (mission.Artifact.FenceCoordinates is { Count: >= 3 } fence && item.Coordinate is { } planned &&
            ((mission.Artifact.FenceKind == Px4FenceKind.Inclusion && !Contains(fence, planned)) ||
             (mission.Artifact.FenceKind == Px4FenceKind.Exclusion && Contains(fence, planned))))
        {
            mission.State = FlightMissionExecutionState.Interrupted;
            mission.LastEvent = "Mission interrupted by a local geofence breach.";
            ghost.NorthVelocity = ghost.EastVelocity = ghost.VerticalVelocity = ghost.YawRate = 0;
            return true;
        }
        var complete = item.Kind switch
        {
            FlightMissionStepKind.Takeoff => MoveAltitude(ghost, item.RelativeAltitudeMetres),
            FlightMissionStepKind.Land => MoveAltitude(ghost, 0),
            FlightMissionStepKind.ReturnToLaunch => MoveHome(ghost),
            FlightMissionStepKind.TimedLoiter => StepLoiter(ghost, mission, item),
            FlightMissionStepKind.CameraCaptureIntent => Capture(mission, item),
            _ => MoveTo(ghost, item.Coordinate, item.RelativeAltitudeMetres, item.CruiseSpeedMetresPerSecond)
        };
        if (complete)
        {
            if (item.Kind == FlightMissionStepKind.Land) ghost.Armed = false;
            mission.CurrentItemIndex++;
            mission.ItemStartedAt = null;
            if (item.SimulatedCapture)
                AddCapture(mission, new(item.StepId, DateTimeOffset.UtcNow, "Simulated image capture; no image file was created."));
            if (mission.CurrentItemIndex >= mission.Artifact.Items.Count)
            {
                mission.State = FlightMissionExecutionState.Completed;
                if (item.Kind == FlightMissionStepKind.Land)
                {
                    ghost.Armed = false;
                    ghost.Landed = true;
                }
                mission.LastEvent = "Ghost mission completed.";
            }
            else
                mission.LastEvent = item.SimulatedCapture ? "Simulated camera capture." : $"Completed mission item {mission.CurrentItemIndex} of {mission.Artifact.Items.Count}.";
        }
        return true;
    }

    private static bool Contains(IReadOnlyList<FlightMissionCoordinate> polygon, FlightMissionCoordinate point)
    {
        var inside = false;
        for (var index = 0; index < polygon.Count; index++)
        {
            var previous = polygon[(index + polygon.Count - 1) % polygon.Count];
            var current = polygon[index];
            if ((current.LatitudeDegrees > point.LatitudeDegrees) != (previous.LatitudeDegrees > point.LatitudeDegrees) &&
                point.LongitudeDegrees < (previous.LongitudeDegrees - current.LongitudeDegrees) * (point.LatitudeDegrees - current.LatitudeDegrees) / (previous.LatitudeDegrees - current.LatitudeDegrees) + current.LongitudeDegrees)
                inside = !inside;
        }
        return inside;
    }

    private static bool Capture(GhostMissionRuntime mission, FlightMissionCompiledItem item) => true;

    private static int FindResumeIndex(IReadOnlyList<FlightMissionCompiledItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
            if (items[index].Kind is not (FlightMissionStepKind.Land or FlightMissionStepKind.ReturnToLaunch))
                return index;
        return 0;
    }

    private static void AddCapture(GhostMissionRuntime mission, FlightMissionCaptureEvent capture)
    {
        if (mission.Captures.Count >= MaxMissionCaptureEvents)
        {
            mission.Captures.RemoveAt(0);
        }

        mission.Captures.Add(capture);
    }

    private static bool StepLoiter(GhostState ghost, GhostMissionRuntime mission, FlightMissionCompiledItem item)
    {
        var atPoint = MoveTo(ghost, item.Coordinate, item.RelativeAltitudeMetres, item.CruiseSpeedMetresPerSecond);
        if (!atPoint) return false;
        mission.ItemStartedAt ??= DateTimeOffset.UtcNow;
        return DateTimeOffset.UtcNow - mission.ItemStartedAt.Value >= TimeSpan.FromSeconds(item.DurationSeconds ?? 0);
    }

    private static bool MoveHome(GhostState ghost)
    {
        var target = new FlightMissionCoordinate(ghost.HomeLatitude, ghost.HomeLongitude);
        return MoveTo(ghost, target, 0, ghost.Profile.Simulation.MaximumHorizontalSpeedMetresPerSecond);
    }

    private static bool MoveTo(GhostState ghost, FlightMissionCoordinate? coordinate, double altitude, double speed)
    {
        if (coordinate is not { } point) return MoveAltitude(ghost, altitude);
        var north = (point.LatitudeDegrees - ghost.Latitude) * Math.PI / 180d * EarthRadiusMetres;
        var east = (point.LongitudeDegrees - ghost.Longitude) * Math.PI / 180d * EarthRadiusMetres * Math.Cos(ghost.Latitude * Math.PI / 180d);
        var distance = Math.Sqrt(north * north + east * east);
        var arrived = distance <= 1.0 && MoveAltitude(ghost, altitude);
        if (distance > 1.0)
        {
            var direction = Math.Atan2(east, north);
            ghost.Heading = MoveHeading(ghost.Heading, NormalizeHeading(direction * 180d / Math.PI), ghost.Profile.Simulation.MaximumYawRateDegreesPerSecond * TickSeconds);
            var step = Math.Min(distance, Math.Max(0.1, speed) * TickSeconds);
            ghost.Latitude += north / distance * step / EarthRadiusMetres * 180d / Math.PI;
            ghost.Longitude += east / distance * step / (EarthRadiusMetres * Math.Cos(ghost.Latitude * Math.PI / 180d)) * 180d / Math.PI;
            ghost.LocalNorth += north / distance * step;
            ghost.LocalEast += east / distance * step;
        }
        return arrived;
    }

    private static bool StepManual(GhostState ghost)
    {
        var setpoint = ghost.ManualSetpoint ?? ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow);
        var active = setpoint.DeadmanPressed && ghost.Armed && !ghost.Landed;
        var headingRadians = ghost.Heading * Math.PI / 180d;
        var desiredNorth = active
            ? (setpoint.BodyForwardMetresPerSecond * Math.Cos(headingRadians)) - (setpoint.BodyRightMetresPerSecond * Math.Sin(headingRadians))
            : 0;
        var desiredEast = active
            ? (setpoint.BodyForwardMetresPerSecond * Math.Sin(headingRadians)) + (setpoint.BodyRightMetresPerSecond * Math.Cos(headingRadians))
            : 0;
        var desiredVertical = active ? setpoint.VerticalMetresPerSecond : 0;
        var desiredYaw = active ? setpoint.YawRateDegreesPerSecond : 0;

        ghost.NorthVelocity = MoveTowards(ghost.NorthVelocity, desiredNorth, ghost.Profile.Simulation.HorizontalAccelerationMetresPerSecondSquared * TickSeconds);
        ghost.EastVelocity = MoveTowards(ghost.EastVelocity, desiredEast, ghost.Profile.Simulation.HorizontalAccelerationMetresPerSecondSquared * TickSeconds);
        ghost.VerticalVelocity = MoveTowards(ghost.VerticalVelocity, desiredVertical, ghost.Profile.Simulation.VerticalAccelerationMetresPerSecondSquared * TickSeconds);
        ghost.YawRate = MoveTowards(ghost.YawRate, desiredYaw, ghost.Profile.Simulation.MaximumYawRateDegreesPerSecond * TickSeconds);
        ghost.Heading = NormalizeHeading(ghost.Heading + ghost.YawRate * TickSeconds);
        var north = ghost.NorthVelocity * TickSeconds;
        var east = ghost.EastVelocity * TickSeconds;
        ghost.Latitude += north / EarthRadiusMetres * 180d / Math.PI;
        ghost.Longitude += east / (EarthRadiusMetres * Math.Cos(ghost.Latitude * Math.PI / 180d)) * 180d / Math.PI;
        ghost.LocalNorth += north;
        ghost.LocalEast += east;
        ghost.AltitudeAgl = Math.Max(0, ghost.AltitudeAgl + ghost.VerticalVelocity * TickSeconds);
        if (ghost.AltitudeAgl <= 0.001)
        {
            ghost.AltitudeAgl = 0;
            ghost.Landed = true;
            ghost.VerticalVelocity = 0;
        }
        else ghost.Landed = false;
        ghost.ManualHold = !active;
        return true;
    }

    private static bool MoveAltitude(GhostState ghost, double target)
    {
        target = Math.Max(0, target);
        var difference = target - ghost.AltitudeAgl;
        var maximumRate = difference >= 0
            ? ghost.Profile.Simulation.MaximumClimbRateMetresPerSecond
            : ghost.Profile.Simulation.MaximumDescentRateMetresPerSecond;
        var step = Math.Min(Math.Abs(difference), maximumRate * TickSeconds);
        ghost.AltitudeAgl += Math.Sign(difference) * step;
        ghost.Landed = ghost.AltitudeAgl <= 0.001;
        return Math.Abs(target - ghost.AltitudeAgl) <= 0.01;
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        using var storeLease = await _storePublicationGate.EnterAsync(cancellationToken);
        await _publishGate.WaitAsync(cancellationToken);
        try
        {
            GhostState[] ghosts;
            lock (_gate)
            {
                ghosts = _ghosts.Values.ToArray();
                Volatile.Write(ref _telemetryDirty, 0);
            }

            await _dispatcher.InvokeAsync(() =>
            {
                _connections.ReplaceAll(_connections.Items.Where(item => !item.IsGhost).Concat(ghosts.Select(ToConnection)));
                _runtimes.ReplaceAll(_runtimes.Items.Where(item => !item.IsGhost).Concat(ghosts.Select(ToRuntime)));
                _vehicles.ReplaceAll(_vehicles.Items.Where(item => !item.IsGhost).Concat(ghosts.Select(ToVehicle)));
                _telemetry.ReplaceAll(_telemetry.Items.Where(item => !item.IsGhost).Concat(ghosts.Select(ToTelemetry)));
                var ghostVehicleIds = ghosts.Select(item => item.VehicleId).ToHashSet(StringComparer.Ordinal);
                _diagnostics.ReplaceAll(_diagnostics.Items.Where(item => !ghostVehicleIds.Contains(item.VehicleId)).Concat(ghosts.Select(ToDiagnostics)));
                if (_links is not null)
                    _links.ReplaceAll(_links.Items.Where(item => !item.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal)).Concat(ghosts.Select(ToLink)));
                if (_cameraSources is not null)
                    _cameraSources.ReplaceAll(_cameraSources.Items.Where(item => !item.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal)).Concat(ghosts.Select(ToCameraSource)));
                if (_cameraStreams is not null)
                    _cameraStreams.ReplaceAll(_cameraStreams.Items.Where(item => !item.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal)));
            }, cancellationToken);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async Task PublishTelemetryAsync(CancellationToken cancellationToken)
    {
        using var storeLease = await _storePublicationGate.EnterAsync(cancellationToken);
        await _publishGate.WaitAsync(cancellationToken);
        try
        {
            GhostState[] ghosts;
            lock (_gate) ghosts = _ghosts.Values.ToArray();
            var now = DateTimeOffset.UtcNow;
            var publishSlowState = now.ToUnixTimeMilliseconds() - Volatile.Read(ref _lastSlowPublicationTimestamp) >= 250;
            await _dispatcher.InvokeAsync(() =>
            {
                _telemetry.ReplaceAll(_telemetry.Items.Where(item => !item.IsGhost).Concat(ghosts.Select(ToTelemetry)));
                if (publishSlowState)
                {
                    var ghostVehicleIds = ghosts.Select(item => item.VehicleId).ToHashSet(StringComparer.Ordinal);
                    _diagnostics.ReplaceAll(_diagnostics.Items.Where(item => !ghostVehicleIds.Contains(item.VehicleId)).Concat(ghosts.Select(ToDiagnostics)));
                    // Link and camera identity are discovery state, not
                    // telemetry.  Rewriting them at 20 Hz caused every Ghost
                    // to trigger unrelated link/camera projections.
                    Volatile.Write(ref _lastSlowPublicationTimestamp, now.ToUnixTimeMilliseconds());
                }
            }, cancellationToken);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private void UpdateCommand(string commandId, OperationalCommandState state, string message, string? reason = null)
    {
        void UpdateOnUiThread()
        {
            if (_commands.TryGet(commandId, out var command) && command is not null)
            {
                _commands.Upsert(command with
                {
                    State = state,
                    Message = message,
                    Reason = reason ?? command.Reason,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        if (_dispatcher.CheckAccess())
        {
            UpdateOnUiThread();
            return;
        }

        // Completion and cancellation can be detected by the physics thread,
        // but command records are observed by Avalonia collections. Never
        // mutate those collections from the simulation thread.
        _ = _dispatcher.InvokeAsync(UpdateOnUiThread)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                    _logger?.LogError(task.Exception, "Ghost command status update failed for {CommandId}", commandId);
            }, TaskScheduler.Default);
    }

    private static ConnectionRecord ToConnection(GhostState ghost)
        => new(ghost.ConnectionId, $"{ghost.Name} connection", $"ghost://{ghost.VehicleId}", ConnectionMode.Ghost, AvailabilityState.Online, false, $"ghost-runtime-{ghost.Number}", "Ghost", ghost.CreatedAt, ghost.CreatedAt, ghost.CreatedAt, ghost.CreatedAt, null, true);

    private static LinkRecord ToLink(GhostState ghost)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            $"ghost-link-{ghost.Number}",
            $"ghost-link-{ghost.Number}",
            ghost.ConnectionId,
            $"ghost-runtime-{ghost.Number}",
            $"{ghost.Name} simulator link",
            "Simulator",
            "Bidirectional",
            "Online",
            "Healthy",
            "Ready",
            true,
            false,
            null,
            "Ghost simulator",
            null,
            null,
            null,
            1d,
            0d,
            0d,
            "SIMULATED_LINK",
            "Ghost simulator link is nominal.",
            now);
    }

    private static CameraSourceRecord ToCameraSource(GhostState ghost)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            $"ghost-camera-{ghost.Number}",
            $"ghost-camera-{ghost.Number}",
            ghost.ConnectionId,
            $"ghost-runtime-{ghost.Number}",
            $"{ghost.Name} camera",
            "Simulated horizon",
            AvailabilityState.Online,
            "Healthy",
            "Ready",
            true,
            true,
            true,
            30,
            0,
            960,
            540,
            $"ghost-frame-{ghost.Number}",
            "SIMULATED_CAMERA",
            "Dark simulated sky/ground horizon",
            now);
    }

    private static RuntimeRecord ToRuntime(GhostState ghost)
        => new($"ghost-runtime-{ghost.Number}", ghost.Name, [ghost.ConnectionId], AvailabilityState.Online, "Ghost", "Simulated", "multicopter", ghost.Profile.Id, "in-app", "Healthy", "Ready", ["operator_control", "arm", "disarm", "hold", "takeoff", "go_to", "change_altitude", "set_heading", "land", "return_home"], DateTimeOffset.UtcNow, ghost.VehicleId, ghost.Name, true);

    private static VehicleRecord ToVehicle(GhostState ghost)
        => new(ghost.VehicleId, ghost.Name, [ghost.ConnectionId], $"ghost-runtime-{ghost.Number}", null, "Multicopter", "Air", ghost.Profile.Id, AvailabilityState.Online, "Ready", ghost.Landed ? "Landed" : "Flying", ghost.Armed ? "Armed" : "Disarmed", "Healthy", ["operator_control", "arm", "disarm", "hold", "takeoff", "go_to", "change_altitude", "set_heading", "land", "return_home"], DateTimeOffset.UtcNow, true);

    private static VehicleDiagnosticsSnapshot ToDiagnostics(GhostState ghost)
    {
        var now = DateTimeOffset.UtcNow;
        var navigationOperations = new[]
        {
            OperatorCommandKind.Takeoff,
            OperatorCommandKind.GoTo,
            OperatorCommandKind.ChangeAltitude,
            OperatorCommandKind.SetHeading,
            OperatorCommandKind.Recover,
            OperatorCommandKind.Land
        };
        var checks = new[]
        {
            new VehicleDiagnosticCheck("GHOST_LINK", "Connection", "Simulator link", VehicleDiagnosticCheckState.Passed, "In-app Ghost simulation is connected."),
            new VehicleDiagnosticCheck("GHOST_TELEMETRY", "Connection", "Telemetry", VehicleDiagnosticCheckState.Passed, "Simulated telemetry is current."),
            new VehicleDiagnosticCheck("GHOST_POSITION", "Navigation", "Global position", VehicleDiagnosticCheckState.Passed, "Simulated global position is available.", navigationOperations),
            new VehicleDiagnosticCheck("GHOST_ESTIMATOR", "Estimator", "Estimator validity", VehicleDiagnosticCheckState.Passed, "Simulated attitude, velocity, and position are valid.", navigationOperations),
            new VehicleDiagnosticCheck("GHOST_POWER", "Power", "Power", VehicleDiagnosticCheckState.Passed, "The simulated power system is nominal."),
            new VehicleDiagnosticCheck("GHOST_PREFLIGHT", "Preflight", "Simulator preflight", VehicleDiagnosticCheckState.Passed, "Ghost simulator preflight checks passed.", new[] { OperatorCommandKind.Arm, OperatorCommandKind.Takeoff })
        };
        var mode = ghost.FormationTarget is not null
            ? "Formation"
            : ghost.ManualSessionId is not null
            ? ghost.ManualHold ? "Manual hold" : "Manual control"
            : ghost.Mission is { State: FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused } ? "Mission" : "Simulated";
        return new VehicleDiagnosticsSnapshot(
            $"ghost-diagnostics-{ghost.Number}",
            ghost.VehicleId,
            ghost.ConnectionId,
            "Ghost",
            VehicleDiagnosticStatus.Ready,
            "Ghost simulation is connected and ready for operations.",
            VehicleDiagnosticStatus.Ready,
            "Ghost simulation can evaluate arm readiness.",
            VehicleDiagnosticStatus.Ready,
            "Ghost simulation provides navigation and position readiness.",
            VehicleDiagnosticStatus.Ready,
            "Simulated telemetry is current.",
            checks,
            [],
            now,
            null,
            null,
            "Simulated",
            mode,
            ghost.Armed,
            ghost.Landed ? "Landed" : "Flying",
            100,
            16.8);
    }

    private static VehicleTelemetryRecord ToTelemetry(GhostState ghost)
        => new($"ghost-telemetry-{ghost.Number}", ghost.VehicleId, ghost.ConnectionId, $"ghost-runtime-{ghost.Number}", AvailabilityState.Online, ghost.Armed, ghost.Landed ? "Landed" : "Flying", "Multicopter", ghost.FormationTarget is not null ? "Formation" : ghost.ManualSessionId is null ? ghost.Mission is { State: FlightMissionExecutionState.Running } ? "Mission" : ghost.Mission is { State: FlightMissionExecutionState.Paused } ? "Mission hold" : ghost.Operation?.Command.ToString() ?? "Simulated" : ghost.ManualHold ? "Manual hold" : "Manual control", "Healthy", "Ready", ghost.Latitude, ghost.Longitude, ghost.GroundAltitude + ghost.AltitudeAgl, ghost.AltitudeAgl, ghost.LocalNorth, ghost.LocalEast, -ghost.AltitudeAgl, ghost.NorthVelocity, ghost.EastVelocity, -ghost.VerticalVelocity, ghost.Heading, false, "SIMULATED", "In-app ghost telemetry", DateTimeOffset.UtcNow, true);

    private static double NormalizeHeading(double heading) => (heading % 360 + 360) % 360;
    private static double MoveHeading(double current, double target, double maximumStep)
    {
        var delta = NormalizeSigned(target - current);
        return NormalizeHeading(current + Math.Sign(delta) * Math.Min(Math.Abs(delta), maximumStep));
    }
    private static double NormalizeSigned(double degrees)
    {
        var normalized = NormalizeHeading(degrees);
        return normalized > 180 ? normalized - 360 : normalized;
    }
    private static double MoveTowards(double current, double target, double maximumDelta)
        => Math.Abs(target - current) <= maximumDelta ? target : current + Math.Sign(target - current) * maximumDelta;

    private sealed class GhostState(
        string vehicleId,
        string name,
        string connectionId,
        GhostProfileSnapshot profile,
        double latitude,
        double longitude,
        double homeLatitude,
        double homeLongitude,
        double localNorth,
        double localEast,
        double heading,
        bool armed,
        bool landed)
    {
        public string VehicleId { get; } = vehicleId;
        public string Name { get; } = name;
        public string ConnectionId { get; } = connectionId;
        public GhostProfileSnapshot Profile { get; } = profile;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public int Number { get; } = int.Parse(vehicleId[6..]);
        public double Latitude { get; set; } = latitude;
        public double Longitude { get; set; } = longitude;
        public double HomeLatitude { get; } = homeLatitude;
        public double HomeLongitude { get; } = homeLongitude;
        public double LocalNorth { get; set; } = localNorth;
        public double LocalEast { get; set; } = localEast;
        public double GroundAltitude { get; } = 100;
        public double AltitudeAgl { get; set; }
        public double Heading { get; set; } = heading;
        public bool Armed { get; set; } = armed;
        public bool Landed { get; set; } = landed;
        public GhostOperation? Operation { get; set; }
        public GhostMissionRuntime? Mission { get; set; }
        public string? ManualSessionId { get; set; }
        public ManualControlSetpoint? ManualSetpoint { get; set; }
        public bool ManualHold { get; set; }
        public GhostFormationTarget? FormationTarget { get; set; }
        public double NorthVelocity { get; set; }
        public double EastVelocity { get; set; }
        public double VerticalVelocity { get; set; }
        public double YawRate { get; set; }
        public FenceDocument? ActiveFence { get; set; }
        public bool FenceBreachReported { get; set; }
    }

    private sealed record GhostOperation(string CommandId, OperatorCommandKind Command, OperatorCommandParameters Parameters);

    private sealed class GhostMissionRuntime(FlightMissionExecutionArtifact artifact)
    {
        public FlightMissionExecutionArtifact Artifact { get; } = artifact;
        public FlightMissionExecutionState State { get; set; } = FlightMissionExecutionState.Uploaded;
        public int CurrentItemIndex { get; set; }
        public DateTimeOffset? ItemStartedAt { get; set; }
        public string? LastEvent { get; set; }
        public List<FlightMissionCaptureEvent> Captures { get; } = [];
    }
}
