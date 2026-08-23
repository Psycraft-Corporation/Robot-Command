using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Operations;
using RobotCommand.Services.Simulation;
using RobotCommand.State;

namespace RobotCommand.Services.ManualControl;

/// <summary>
/// Owns one manual-control lease. Input sampling is deliberately separate from
/// Ghost physics and MAVLink transport so a slow controller or UI cannot mutate
/// vehicle state directly.
/// </summary>
public sealed class ManualControlService : IManualControlService, IHostedService
{
    private readonly IManualInputDeviceProvider _devices;
    private readonly IGhostUnitService _ghosts;
    private readonly IMavlinkConnectionRegistry _mavlinkConnections;
    private readonly IOperatorControlService _operations;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private readonly IUiDispatcher _dispatcher;
    private readonly ManualControlProfileStore _profileStore;
    private readonly IManualControlRegistry _registry;
    private readonly IFormationLockWorkflow _formation;
    private readonly ILogger<ManualControlService>? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;
    private ManualControlProfile _profile = new();
    private ManualControlSessionSnapshot _snapshot = ManualControlSessionSnapshot.Empty;
    private ManualInputReading? _latest;
    private string? _selectedDeviceId;
    private string? _sessionId;
    private string? _sessionCommandId;
    private ButtonCommand? _pendingButton;
    private ButtonState _previousButtons;
    private bool _requireDeadmanRelease;
    private bool _focusLost;
    private bool _mavlinkManualInputActive;
    private DateTimeOffset? _controllerStatusUntil;
    private ManualControlBackend _backend;
    private int _inputSamples;
    private DateTimeOffset _rateWindowStarted = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastIdleReadingPublishedAt;

    public ManualControlService(
        IManualInputDeviceProvider devices,
        IGhostUnitService ghosts,
        IMavlinkConnectionRegistry mavlinkConnections,
        IOperatorControlService operations,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, OperationalCommandRecord> commands,
        IUiDispatcher dispatcher,
        ManualControlProfileStore profileStore,
        IManualControlRegistry registry,
        IFormationLockWorkflow formation,
        ILogger<ManualControlService>? logger = null)
    {
        _devices = devices;
        _ghosts = ghosts;
        _mavlinkConnections = mavlinkConnections;
        _operations = operations;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _commands = commands;
        _dispatcher = dispatcher;
        _profileStore = profileStore;
        _registry = registry;
        _formation = formation;
        _logger = logger;
        _devices.DevicesChanged += (_, _) => PublishChanged();
    }

    public IReadOnlyList<ManualInputDevice> Devices => _devices.Devices;
    public ManualControlProfile Profile => _profile;
    public ManualControlSessionSnapshot Snapshot => _snapshot;
    public ManualInputReading? LatestReading => _latest;
    public string? SelectedDeviceId => _selectedDeviceId;
    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _profile = await _profileStore.LoadAsync(cancellationToken);
        _selectedDeviceId ??= Devices.FirstOrDefault()?.Id;
        _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _loop = PollAsync(_loopCancellation.Token);
        PublishChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCancellation?.Cancel();
        await ReleaseAsync("Application shutdown.", cancellationToken);
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
    }

    public Task SetSelectedDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        if (_snapshot.IsActive && !string.Equals(deviceId, _selectedDeviceId, StringComparison.Ordinal))
            return Task.CompletedTask;
        _selectedDeviceId = Devices.Any(device => device.Id == deviceId) ? deviceId : Devices.FirstOrDefault()?.Id;
        PublishChanged();
        return Task.CompletedTask;
    }

    public async Task SaveProfileAsync(ManualControlProfile profile, CancellationToken cancellationToken = default)
    {
        _profile = profile.Normalize();
        await _profileStore.SaveAsync(_profile, cancellationToken);
        PublishChanged();
    }

    public async Task<bool> TakeControlAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            return await TakeControlCoreAsync(vehicleId, cancellationToken);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task<bool> TakeControlCoreAsync(string vehicleId, CancellationToken cancellationToken)
    {
        if (_snapshot.IsActive || _snapshot.State is ManualControlSessionState.Acquiring or ManualControlSessionState.Releasing) return false;
        if (!_vehicles.TryGet(vehicleId, out var vehicle) || vehicle is null)
        {
            SetSnapshot(ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Blocked, Status = "Select one supported unit in the Units rail." });
            return false;
        }

        SetSnapshot(ManualControlSessionSnapshot.Empty with
        {
            State = ManualControlSessionState.Acquiring,
            TargetVehicleId = vehicle.Id,
            TargetName = vehicle.Name,
            Status = "Taking manual control..."
        });

        var readiness = await _operations.GetManualControlReadinessAsync(vehicleId, cancellationToken);
        if (!readiness.IsReady)
        {
            SetSnapshot(ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Blocked, Status = readiness.Summary });
            return false;
        }

        var deviceId = _selectedDeviceId ?? Devices.FirstOrDefault()?.Id;
        if (deviceId is null || !_devices.TryGetReading(deviceId, _profile.MappingFor(deviceId), out var reading))
        {
            SetSnapshot(ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Blocked, Status = "Connect an Xbox-compatible controller or supported USB joystick first." });
            return false;
        }
        if (!IsNeutral(reading))
        {
            SetSnapshot(ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Blocked, Status = "Center all sticks before taking control." });
            return false;
        }

        var backend = ResolveBackend(vehicle);
        if (backend == ManualControlBackend.None)
        {
            SetSnapshot(ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Blocked, Status = "Manual control supports Ghosts and connected PX4 or ArduPilot MAVLink multicopters only." });
            return false;
        }

        var sessionId = $"manual-{Guid.NewGuid():N}";
        if (backend == ManualControlBackend.Ghost)
        {
            await _formation.HandleIndependentOperationAsync(vehicleId, OperatorWorkflowCommandKind.Hold, cancellationToken);
            if (!await _ghosts.BeginManualControlAsync(vehicleId, sessionId, cancellationToken))
            {
                SetSnapshot(ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Blocked, Status = "The Ghost is unavailable for manual control." });
                return false;
            }
        }
        else if (!await BeginMavlinkManualControlAsync(vehicle, cancellationToken))
        {
            return false;
        }

        _sessionId = sessionId;
        _backend = backend;
        _registry.Acquire(vehicleId);
        _selectedDeviceId = deviceId;
        _latest = reading;
        _previousButtons = ButtonState.From(reading);
        _requireDeadmanRelease = false;
        _focusLost = false;
        _mavlinkManualInputActive = backend is ManualControlBackend.Px4Mavlink or ManualControlBackend.ArduPilotMavlink;
        _controllerStatusUntil = null;
        _sessionCommandId = $"manual-control-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await _dispatcher.InvokeAsync(() => _commands.Upsert(new OperationalCommandRecord(
            _sessionCommandId, "ManualControl", "Vehicle", vehicle.Id, vehicle.ConnectionIds.FirstOrDefault(),
            OperationalCommandState.InProgress, $"Manual control {vehicle.Name}", "Manual control acquired.",
            sessionId, now, now, vehicle.Id, vehicle.LogosInstanceId, Reason: "Manual control session")), cancellationToken);
        var sessionSnapshot = new ManualControlSessionSnapshot(ManualControlSessionState.Hold, backend, vehicle.Id, vehicle.Name, deviceId,
            Devices.FirstOrDefault(item => item.Id == deviceId)?.Name ?? "Xbox controller", now, reading.Timestamp,
            false, true, "Manual hold. Hold LB with centered sticks to enable motion.", null, null, 0);
        SetSnapshot(ApplyMavlinkStatus(sessionSnapshot, vehicle.Id));
        return true;
    }

    public async Task ReleaseAsync(string reason = "Operator released manual control.", CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            await ReleaseCoreAsync(reason, cancellationToken);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task ReleaseCoreAsync(string reason, CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        var sessionId = _sessionId;
        if (!snapshot.IsActive || sessionId is null)
        {
            return;
        }
        SetSnapshot(snapshot with { State = ManualControlSessionState.Releasing, Status = "Releasing manual control..." });
        if (_pendingButton is { } pending)
            await _operations.CancelAsync(pending.Plan, reason, cancellationToken);
        MavlinkManualControlDispatchResult? releaseResult = null;
        if (sessionId is not null && snapshot.TargetVehicleId is not null)
        {
            if (_backend == ManualControlBackend.Ghost)
            {
                await _ghosts.EndManualControlAsync(snapshot.TargetVehicleId, sessionId, reason, cancellationToken);
            }
            else if (_backend is ManualControlBackend.Px4Mavlink or ManualControlBackend.ArduPilotMavlink &&
                     TryGetMavlinkConnection(snapshot.TargetVehicleId, out var connection, out _))
            {
                releaseResult = await connection.EndManualControlAsync(snapshot.TargetVehicleId, cancellationToken);
                if (!releaseResult.Accepted)
                {
                    _logger?.LogWarning("MAVLink manual-control release for {VehicleId} did not enter a safe mode: {Message}", snapshot.TargetVehicleId, releaseResult.Message);
                }
            }
        }
        if (_sessionCommandId is not null)
        {
            var commandId = _sessionCommandId;
            await _dispatcher.InvokeAsync(() =>
            {
                if (_commands.TryGet(commandId, out var record) && record is not null)
                    _commands.Upsert(record with { State = OperationalCommandState.Cancelled, Message = reason, Reason = "MANUAL_CONTROL_RELEASED", UpdatedAt = DateTimeOffset.UtcNow });
            }, cancellationToken);
        }
        _sessionId = null;
        _backend = ManualControlBackend.None;
        _mavlinkManualInputActive = false;
        if (snapshot.TargetVehicleId is not null) _registry.Release(snapshot.TargetVehicleId);
        _sessionCommandId = null;
        _pendingButton = null;
        _controllerStatusUntil = null;
        _previousButtons = default;
        if (releaseResult is { Accepted: false } failedRelease)
        {
            SetSnapshot(ManualControlSessionSnapshot.Empty with
            {
                State = ManualControlSessionState.Blocked,
                Backend = snapshot.Backend,
                TargetVehicleId = snapshot.TargetVehicleId,
                TargetName = snapshot.TargetName,
                Status = failedRelease.Message,
                SafeReleaseConfirmed = false,
                InterruptionReason = failedRelease.Message
            });
        }
        else
        {
            SetSnapshot(ManualControlSessionSnapshot.Empty);
        }
    }

    public void OnApplicationFocusChanged(bool focused)
    {
        if (focused || !_snapshot.IsActive) return;
        _focusLost = true;
        _requireDeadmanRelease = true;
        _ = SendNeutralAsync("Manual hold because the application lost focus.", ManualControlSessionState.Hold);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 60d));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_selectedDeviceId is null) continue;
                if (!_devices.TryGetReading(_selectedDeviceId, _profile.MappingFor(_selectedDeviceId), out var reading))
                {
                    if (_snapshot.IsActive) await ReleaseAsync("Controller disconnected.", cancellationToken);
                    continue;
                }
                _latest = reading;
                if (!_snapshot.IsActive)
                {
                    // Keep the device monitor and mapping editor live before a
                    // vehicle is selected, without asking Avalonia to redraw at
                    // the full 60 Hz control rate.
                    if (DateTimeOffset.UtcNow - _lastIdleReadingPublishedAt >= TimeSpan.FromMilliseconds(100))
                    {
                        _lastIdleReadingPublishedAt = DateTimeOffset.UtcNow;
                        PublishChanged();
                    }
                    continue;
                }
                _inputSamples++;
                await ProcessReadingAsync(reading, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ProcessReadingAsync(ManualInputReading reading, CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        if (snapshot.TargetVehicleId is null || _sessionId is null) return;
        var controllerCommandChanged = false;
        if (_pendingButton is { } pending && DateTimeOffset.UtcNow > pending.ExpiresAt)
        {
            await _operations.CancelAsync(pending.Plan, "Controller confirmation expired.", cancellationToken);
            _pendingButton = null;
            SetControllerStatus(
                snapshot with { PendingButtonAction = null, PendingButtonExpiresAt = null },
                "Controller command confirmation expired.");
            snapshot = _snapshot;
            controllerCommandChanged = true;
        }
        var targetVehicleId = snapshot.TargetVehicleId;
        if (string.IsNullOrWhiteSpace(targetVehicleId) ||
            !_vehicles.TryGet(targetVehicleId, out var vehicle) || vehicle is null)
        {
            await ReleaseAsync("Manual target was lost.", cancellationToken);
            return;
        }
        var readiness = await _operations.GetManualControlReadinessAsync(targetVehicleId, cancellationToken);
        if (!readiness.IsReady)
        {
            // A new ArduPilot PreArm message matters to arming and automated
            // movement, but must not revoke an already active safe manual
            // lease. The MAVLink input pump still owns mode, failsafe, link,
            // and transport safety checks.
            if (_backend == ManualControlBackend.ArduPilotMavlink &&
                readiness.Findings.Where(item => item.Severity == OperatorPreflightSeverity.Blocking)
                    .All(IsArduPilotPreArmFinding))
            {
                readiness = readiness with { IsReady = true };
            }
        }
        if (!readiness.IsReady)
        {
            await ReleaseAsync($"Manual target is no longer ready: {readiness.Summary}", cancellationToken);
            return;
        }
        var age = DateTimeOffset.UtcNow - reading.Timestamp;
        if (age > TimeSpan.FromSeconds(2)) { await ReleaseAsync("Controller input became stale.", cancellationToken); return; }
        if (age > TimeSpan.FromMilliseconds(250)) { await SendNeutralAsync("Controller input is stale.", ManualControlSessionState.InputStale, cancellationToken); return; }

        var buttons = ButtonState.From(reading);
        if (buttons.Menu && !_previousButtons.Menu) { await ReleaseAsync("Released from controller Menu button.", cancellationToken); return; }
        // Manual discrete operations use the same prepare/queue/execute path
        // as the in-app operator controls. A/RB/Y queue a plan, X executes the
        // queued plan, and B removes it. This deliberately avoids the old
        // per-command double-press behavior, which made the visible queued
        // operation disappear before it could be executed consistently.
        if (buttons.B && !_previousButtons.B)
        {
            await CancelPendingButtonAsync(cancellationToken);
            controllerCommandChanged = true;
        }
        if (buttons.X && !_previousButtons.X)
        {
            await ExecutePendingButtonAsync(cancellationToken);
            controllerCommandChanged = true;
        }
        if (buttons.A && !_previousButtons.A ||
            buttons.RightBumper && !_previousButtons.RightBumper ||
            buttons.Y && !_previousButtons.Y)
        {
            var command = ManualControlButtonMapper.QueuedCommandFor(
                buttons.A && !_previousButtons.A,
                buttons.RightBumper && !_previousButtons.RightBumper,
                buttons.Y && !_previousButtons.Y,
                IsArmed(targetVehicleId),
                IsLanded(targetVehicleId));
            if (command is { } queuedCommand)
            {
                var button = buttons.A && !_previousButtons.A
                    ? "A"
                    : buttons.RightBumper && !_previousButtons.RightBumper ? "RB" : "Y";
                await QueueDiscreteAsync(queuedCommand, button, cancellationToken);
                controllerCommandChanged = true;
            }
        }
        _previousButtons = buttons;

        // Queue/execute/cancel publishes its own complete snapshot. Do not let
        // the normal stick-update projection write an older snapshot over the
        // queued action on the same polling tick (the old behavior caused the
        // command text to flicker and disappear immediately).
        if (controllerCommandChanged)
            return;

        if (_requireDeadmanRelease)
        {
            if (!reading.LeftBumper) _requireDeadmanRelease = false;
            await SendNeutralAsync(_focusLost ? "Release and re-press LB after focus returns." : "Release and re-press LB before motion resumes.", ManualControlSessionState.Hold, cancellationToken);
            return;
        }
        _focusLost = false;
        var setpoint = BuildSetpoint(reading);
        var enteringDeadman = setpoint.DeadmanPressed && !snapshot.DeadmanPressed;
        if (!await SendSetpointAsync(targetVehicleId, setpoint, enteringDeadman, cancellationToken))
        {
            await ReleaseAsync("Manual-control transport failed.", cancellationToken);
            return;
        }
        var state = setpoint.DeadmanPressed ? ManualControlSessionState.Active : ManualControlSessionState.Hold;
        var now = DateTimeOffset.UtcNow;
        var preserveControllerStatus = _controllerStatusUntil is { } statusUntil && statusUntil > now;
        if (!preserveControllerStatus)
            _controllerStatusUntil = null;
        var updatedSnapshot = snapshot with
        {
            State = state,
            LastInputAt = reading.Timestamp,
            DeadmanPressed = reading.LeftBumper,
            InputNeutral = IsNeutral(reading),
            Status = preserveControllerStatus
                ? snapshot.Status
                : state == ManualControlSessionState.Active ? "Manual control active." : "Manual hold. Hold LB to enable motion.",
            InputRateHertz = InputRate()
        };
        SetSnapshot(ApplyMavlinkStatus(updatedSnapshot, targetVehicleId));
    }

    private async Task QueueDiscreteAsync(OperatorCommandKind command, string button, CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        if (snapshot.TargetVehicleId is null) return;
        var now = DateTimeOffset.UtcNow;
        if (_pendingButton is { } existing &&
            existing.Command == command &&
            now <= existing.ExpiresAt)
        {
            SetControllerStatus(snapshot,
                $"{OperatorControlRules.DisplayName(command)} is already queued. Press X to execute.");
            return;
        }

        if (_pendingButton is { } replaced)
            await _operations.CancelAsync(replaced.Plan, "Replaced by a different controller command.", cancellationToken);

        var parameters = command == OperatorCommandKind.Takeoff
            ? new OperatorCommandParameters(TakeoffAltitudeAglMetres: _profile.TakeoffAltitudeAglMetres)
            : OperatorCommandParameters.None;
        var plan = await _registry.RunManualSubmissionAsync(() =>
            _operations.PrepareAsync(snapshot.TargetVehicleId, command, "Xbox controller", parameters, cancellationToken));
        if (!plan.CanSubmit)
        {
            var unavailableMessage = $"{OperatorControlRules.DisplayName(command)} is unavailable: {plan.Findings.FirstOrDefault(item => item.Severity == OperatorPreflightSeverity.Blocking)?.Message ?? "preflight failed"}";
            SetControllerStatus(snapshot with
            {
                PendingButtonAction = null,
                PendingButtonExpiresAt = null
            }, unavailableMessage);
            _pendingButton = null;
            return;
        }

        var expiresAt = now.AddSeconds(5);
        _pendingButton = new ButtonCommand(command, button, expiresAt, plan);
        SetControllerStatus(snapshot with
        {
            PendingButtonAction = $"Queued action: {OperatorControlRules.DisplayName(command)}. Press X to execute.",
            PendingButtonExpiresAt = expiresAt,
            Status = $"{OperatorControlRules.DisplayName(command)} queued. Press X to execute."
        }, duration: TimeSpan.FromSeconds(5));
    }

    private async Task ExecutePendingButtonAsync(CancellationToken cancellationToken)
    {
        var pending = _pendingButton;
        if (pending is null)
        {
            SetControllerStatus(_snapshot, "No controller command is queued. Press A, RB, or Y first.");
            return;
        }

        if (DateTimeOffset.UtcNow > pending.ExpiresAt || DateTimeOffset.UtcNow >= pending.Plan.ExpiresAt)
        {
            await CancelPendingButtonAsync(cancellationToken, "Queued controller command expired.");
            return;
        }

        _pendingButton = null;
        _requireDeadmanRelease = true;
        await SendNeutralAsync("Manual hold while controller command is submitted.", ManualControlSessionState.Hold, cancellationToken);
        try
        {
            var result = pending.Plan.CanSubmit
                ? await _registry.RunManualSubmissionAsync(() => _operations.ExecuteAsync(pending.Plan, cancellationToken))
                : new OperatorCommandResult(false, OperationalCommandState.Rejected, "The queued controller command is no longer available.");
            if (result.Accepted &&
                (_backend is ManualControlBackend.Px4Mavlink or ManualControlBackend.ArduPilotMavlink))
            {
                // A discrete MAVLink operation can change the flight mode.  The
                // next LB engagement must re-establish the manual-input path
                // instead of assuming the old one is still valid.
                _mavlinkManualInputActive = false;
            }
            SetControllerStatus(_snapshot with
            {
                PendingButtonAction = null,
                PendingButtonExpiresAt = null,
                Status = result.Accepted
                    ? $"{OperatorControlRules.DisplayName(pending.Command)} executed. Release and re-press LB before motion resumes."
                    : $"{OperatorControlRules.DisplayName(pending.Command)} was rejected: {result.Message}"
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Manual controller {Command} command failed", pending.Command);
            SetControllerStatus(_snapshot with
            {
                PendingButtonAction = null,
                PendingButtonExpiresAt = null,
                Status = $"{OperatorControlRules.DisplayName(pending.Command)} failed: {ex.Message}"
            });
        }
    }

    private async Task CancelPendingButtonAsync(
        CancellationToken cancellationToken,
        string message = "Queued controller command removed.")
    {
        if (_pendingButton is not { } pending)
        {
            SetControllerStatus(_snapshot, "No controller command is queued.");
            return;
        }

        _pendingButton = null;
        await _operations.CancelAsync(pending.Plan, message, cancellationToken);
        SetControllerStatus(_snapshot with
        {
            PendingButtonAction = null,
            PendingButtonExpiresAt = null,
            Status = message
        });
    }

    private void SetControllerStatus(
        ManualControlSessionSnapshot snapshot,
        string? status = null,
        TimeSpan? duration = null)
    {
        _controllerStatusUntil = DateTimeOffset.UtcNow.Add(duration ?? TimeSpan.FromSeconds(3));
        SetSnapshot(snapshot with
        {
            Status = status ?? snapshot.Status
        });
    }

    private async Task SendNeutralAsync(
        string status,
        ManualControlSessionState state,
        CancellationToken cancellationToken = default)
    {
        if (_snapshot.TargetVehicleId is not null && _sessionId is not null)
        {
            _ = await SendSetpointAsync(
                _snapshot.TargetVehicleId,
                ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow),
                false,
                cancellationToken);
        }
        SetSnapshot(_snapshot with { State = state, DeadmanPressed = false, Status = status });
    }

    private ManualControlBackend ResolveBackend(VehicleRecord vehicle)
    {
        if (_ghosts.IsGhostVehicle(vehicle.Id))
        {
            return ManualControlBackend.Ghost;
        }
        if (!TryGetMavlinkConnection(vehicle.Id, out _, out var adapter))
            return ManualControlBackend.None;
        return adapter?.Profile == MavlinkAutopilotProfile.ArduPilot
            ? ManualControlBackend.ArduPilotMavlink
            : ManualControlBackend.Px4Mavlink;
    }

    private ManualControlSessionSnapshot ApplyMavlinkStatus(
        ManualControlSessionSnapshot snapshot,
        string vehicleId)
    {
        if (snapshot.Backend is not (ManualControlBackend.Px4Mavlink or ManualControlBackend.ArduPilotMavlink) ||
            !TryGetMavlinkConnection(vehicleId, out var connection, out _) ||
            !connection.TryGetManualControlStatus(vehicleId, out var status))
        {
            return snapshot;
        }

        return snapshot with
        {
            AutopilotMode = status.Mode,
            ModeClass = status.ModeClass.ToString(),
            AdmissionStatus = status.ParameterAdmissionVerified
                ? "Verified"
                : "Degraded: vehicle input parameters were not fully reported",
            TransportInputRateHertz = status.StreamRateHertz,
            LastInputSentAt = status.LastInputSentAt,
            InputEchoAvailable = status.InputEchoAvailable,
            SafeReleaseMode = status.SafeReleaseMode,
            SafeReleaseConfirmed = status.SafeReleaseConfirmed,
            InterruptionReason = status.Failure
        };
    }

    private static bool IsArduPilotPreArmFinding(OperatorPreflightFinding finding)
        => finding.Code.Contains("PREARM", StringComparison.OrdinalIgnoreCase) ||
           finding.Message.StartsWith("PreArm:", StringComparison.OrdinalIgnoreCase) ||
           finding.Message.Contains("PreArm:", StringComparison.OrdinalIgnoreCase);

    private bool TryGetMavlinkConnection(string vehicleId, out MavlinkConnection connection, out IMavlinkAutopilotAdapter? adapter)
    {
        connection = null!;
        adapter = null;
        if (!_vehicles.TryGet(vehicleId, out var vehicle) || vehicle is null)
        {
            return false;
        }
        foreach (var connectionId in vehicle.ConnectionIds)
        {
            if (_mavlinkConnections.TryGet(connectionId, out var candidate) &&
                candidate is not null &&
                candidate.TryGetVehicle(vehicleId, out _, out _, out _, out var candidateAdapter) &&
                candidateAdapter is not null)
            {
                connection = candidate;
                adapter = candidateAdapter;
                return true;
            }
        }
        return false;
    }

    private async Task<bool> BeginMavlinkManualControlAsync(VehicleRecord vehicle, CancellationToken cancellationToken)
    {
        if (!TryGetMavlinkConnection(vehicle.Id, out var connection, out _))
        {
            SetSnapshot(ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Blocked, Status = "The MAVLink connection is not active." });
            return false;
        }
        var result = await connection.BeginManualControlAsync(vehicle.Id, cancellationToken);
        if (result.Accepted)
        {
            return true;
        }
        SetSnapshot(ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Blocked, Status = result.Message });
        return false;
    }

    private async Task<bool> SendSetpointAsync(
        string vehicleId,
        ManualControlSetpoint setpoint,
        bool enteringDeadman,
        CancellationToken cancellationToken)
    {
        if (_backend == ManualControlBackend.Ghost && _sessionId is not null)
        {
            _ghosts.UpdateManualControl(vehicleId, _sessionId, setpoint);
            return true;
        }
        if (_backend is not (ManualControlBackend.Px4Mavlink or ManualControlBackend.ArduPilotMavlink) ||
            !TryGetMavlinkConnection(vehicleId, out var connection, out _))
        {
            return false;
        }
        if (enteringDeadman && !_mavlinkManualInputActive)
        {
            var engage = await connection.BeginManualControlAsync(vehicleId, cancellationToken);
            if (!engage.Accepted)
            {
                SetSnapshot(_snapshot with { Status = engage.Message });
                return false;
            }
            _mavlinkManualInputActive = true;
        }
        var result = await connection.SendManualControlAsync(vehicleId, setpoint, _profile, cancellationToken);
        if (!result.Accepted)
        {
            SetSnapshot(_snapshot with { Status = result.Message });
        }
        return result.Accepted;
    }

    private ManualControlSetpoint BuildSetpoint(ManualInputReading reading)
        => new(
            Scale(reading.RightY, _profile.MaximumHorizontalSpeedMetresPerSecond),
            Scale(reading.RightX, _profile.MaximumHorizontalSpeedMetresPerSecond),
            Scale(reading.LeftY, _profile.MaximumVerticalSpeedMetresPerSecond),
            Scale(reading.LeftX, _profile.MaximumYawRateDegreesPerSecond),
            reading.LeftBumper, reading.Timestamp);

    private double Scale(double value, double maximum)
    {
        var sign = Math.Sign(value);
        var magnitude = Math.Abs(value);
        if (magnitude <= _profile.DeadZone) return 0;
        var normalized = (magnitude - _profile.DeadZone) / (1 - _profile.DeadZone);
        var curved = ((1 - _profile.Expo) * normalized) + (_profile.Expo * normalized * normalized * normalized);
        return sign * curved * maximum;
    }

    private bool IsNeutral(ManualInputReading reading)
        => Math.Abs(reading.LeftX) <= _profile.DeadZone && Math.Abs(reading.LeftY) <= _profile.DeadZone &&
           Math.Abs(reading.RightX) <= _profile.DeadZone && Math.Abs(reading.RightY) <= _profile.DeadZone;

    private bool IsLanded(string vehicleId)
        => _telemetry.Items.FirstOrDefault(item => item.VehicleId == vehicleId)?.LandedState is "Landed";

    private bool IsArmed(string vehicleId)
        => _telemetry.Items.FirstOrDefault(item => item.VehicleId == vehicleId)?.Armed == true;

    private double InputRate()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _rateWindowStarted;
        if (elapsed.TotalSeconds >= 1)
        {
            var rate = _inputSamples / elapsed.TotalSeconds;
            _inputSamples = 0;
            _rateWindowStarted = now;
            return Math.Round(rate, 1);
        }
        return _snapshot.InputRateHertz;
    }

    private void SetSnapshot(ManualControlSessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        PublishChanged();
    }

    private void PublishChanged()
    {
        if (_dispatcher.CheckAccess()) Changed?.Invoke(this, EventArgs.Empty);
        else _ = _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private sealed record ButtonCommand(OperatorCommandKind Command, string Button, DateTimeOffset ExpiresAt, OperatorCommandPlan Plan);
    private readonly record struct ButtonState(bool A, bool B, bool X, bool Y, bool RightBumper, bool Menu)
    {
        public static ButtonState From(ManualInputReading reading) => new(reading.A, reading.B, reading.X, reading.Y, reading.RightBumper, reading.Menu);
    }
}
