using System.Collections.Specialized;
using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Operations;
using RobotCommand.State;

namespace RobotCommand.Services.Workflows;

/// <summary>
/// Session-owned command queue shared by every front end.  It deliberately owns
/// logical queue state while <see cref="IOperatorControlService"/> remains the
/// authority for backend preparation, policy, and dispatch.
/// </summary>
public sealed class OperatorCommandWorkflow : IOperatorCommandWorkflow, IDisposable
{
    private readonly IOperatorControlService _controls;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, QueueEntry> _queued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueueEntry> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelling = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer _refreshTimer;
    private bool _disposed;

    public OperatorCommandWorkflow(
        IOperatorControlService controls,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, OperationalCommandRecord> commands)
    {
        _controls = controls;
        _telemetry = telemetry;
        _commands = commands;
        ((INotifyCollectionChanged)_commands.Items).CollectionChanged += OnCommandsChanged;
        _refreshTimer = new System.Threading.Timer(_ => _ = RefreshAsync(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public event EventHandler? Changed;

    public OperatorCommandWorkflowStatus Status => new(_controls.GatewayStatus.Available, _controls.GatewayStatus.Message);

    public IReadOnlyList<OperatorCommandQueueSnapshot> Commands
        => _queued.Values.Concat(_active.Values).Select(ToSnapshot).OrderBy(item => item.CreatedAt).ToArray();

    public IReadOnlyList<OperatorCommandQueueSnapshot> QueuedCommands
        => _queued.Values.Select(ToSnapshot).OrderBy(item => item.CreatedAt).ToArray();

    public IReadOnlyList<OperatorCommandQueueSnapshot> ActiveCommands
        => _active.Values.Select(ToSnapshot).OrderBy(item => item.CreatedAt).ToArray();

    public bool TryGet(string queueOrCommandId, out OperatorCommandQueueSnapshot? command)
    {
        var entry = _queued.Values.Concat(_active.Values).FirstOrDefault(item =>
            string.Equals(item.QueueId, queueOrCommandId, StringComparison.Ordinal) ||
            string.Equals(item.Plan.CommandId, queueOrCommandId, StringComparison.Ordinal));
        command = entry is null ? null : ToSnapshot(entry);
        return command is not null;
    }

    public async Task<OperatorCommandBatchSnapshot> QueueAsync(
        OperatorCommandQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Targets.Count == 0) throw new ArgumentException("At least one unit is required.", nameof(request));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var batchId = $"batch-{Guid.NewGuid():N}";
            var entries = new List<QueueEntry>();
            foreach (var target in request.Targets.GroupBy(item => item.UnitId, StringComparer.Ordinal).Select(group => group.First()))
            {
                foreach (var existing in _queued.Values.Where(item => string.Equals(item.RequestUnitId, target.UnitId, StringComparison.Ordinal)).ToArray())
                {
                    await _controls.CancelAsync(existing.Plan, "Replaced by a newly queued operator command.", cancellationToken);
                    _queued.Remove(existing.QueueId);
                }

                var plan = await _controls.PrepareAsync(
                    target.UnitId,
                    ToRuntime(request.Command),
                    request.Reason,
                    ToRuntime(target.Parameters ?? OperatorWorkflowParameters.None),
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(request.DisplayName)) plan = plan with { DisplayName = request.DisplayName! };
                var entry = new QueueEntry(
                    $"queue-{Guid.NewGuid():N}", batchId, target.UnitId, plan,
                    request.RollbackAcceptedGoToOnPartialFailure, Assessment: null);
                _queued[entry.QueueId] = entry;
                entries.Add(entry);
            }

            RaiseChanged();
            return ToBatch(batchId, request.Command, request.DisplayName ?? DisplayName(request.Command), entries);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<OperatorCommandExecutionSnapshot>> ExecuteAsync(
        string queueOrBatchId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = FindQueued(queueOrBatchId);
            if (entries.Count == 0) throw new KeyNotFoundException($"Queued command or batch '{queueOrBatchId}' was not found.");
            var results = new List<OperatorCommandExecutionSnapshot>();
            foreach (var entry in entries)
            {
                var executable = await EnsureFreshPlanAsync(entry, cancellationToken);
                var result = await _controls.ExecuteAsync(executable.Plan, cancellationToken);
                results.Add(ToExecution(executable, result));
                if (result.Accepted)
                {
                    _queued.Remove(entry.QueueId);
                    if (result.State is OperationalCommandState.Submitting or OperationalCommandState.Accepted or OperationalCommandState.InProgress)
                        _active[executable.Plan.Target.VehicleId] = executable;
                }
            }

            if (entries.Any(item => item.RollbackAcceptedGoToOnPartialFailure) &&
                results.Any(item => item.Accepted) && results.Any(item => !item.Accepted))
            {
                foreach (var accepted in entries.Zip(results).Where(item => item.Second.Accepted).Select(item => item.First))
                {
                    var hold = await _controls.PrepareAsync(accepted.RequestUnitId, OperatorCommandKind.Hold,
                        "Hold accepted units after incomplete assembly", OperatorCommandParameters.None, cancellationToken);
                    if (hold.CanSubmit) await _controls.ExecuteAsync(hold, cancellationToken);
                }
            }

            RaiseChanged();
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(
        string queueOrBatchId,
        string message = "Queued command cleared by operator.",
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = FindQueued(queueOrBatchId);
            if (entries.Count == 0) throw new KeyNotFoundException($"Queued command or batch '{queueOrBatchId}' was not found.");
            foreach (var entry in entries)
            {
                await _controls.CancelAsync(entry.Plan, message, cancellationToken);
                _queued.Remove(entry.QueueId);
            }
            RaiseChanged();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<OperatorCommandExecutionSnapshot>> CancelActiveAsync(
        IReadOnlyList<string> unitIds,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var active = _active.Values.Where(entry => unitIds.Contains(entry.RequestUnitId, StringComparer.Ordinal) ||
                                                       unitIds.Contains(entry.Plan.Target.VehicleId, StringComparer.Ordinal)).ToArray();
            var results = new List<OperatorCommandExecutionSnapshot>();
            foreach (var entry in active)
            {
                _cancelling.Add(entry.Plan.Target.VehicleId);
                try
                {
                    var result = await CancelActiveEntryAsync(entry, cancellationToken);
                    results.Add(result);
                }
                finally
                {
                    _cancelling.Remove(entry.Plan.Target.VehicleId);
                }
            }
            RaiseChanged();
            return results;
        }
        finally { _gate.Release(); }
    }

    private async Task<QueueEntry> EnsureFreshPlanAsync(QueueEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Plan.CanSubmit &&
            (entry.Assessment is null || entry.Assessment.Availability is OperatorControlAvailability.Ready or OperatorControlAvailability.Warning))
            return entry;
        await _controls.CancelAsync(entry.Plan, "Queued preparation expired and was refreshed.", cancellationToken);
        var plan = await _controls.PrepareAsync(entry.RequestUnitId, entry.Plan.Command, entry.Plan.Reason,
            entry.Plan.Parameters ?? OperatorCommandParameters.None, cancellationToken);
        var refreshed = entry with { Plan = plan, Assessment = null };
        _queued[entry.QueueId] = refreshed;
        return refreshed;
    }

    private async Task<OperatorCommandExecutionSnapshot> CancelActiveEntryAsync(
        QueueEntry entry,
        CancellationToken cancellationToken)
    {
        var command = entry.Plan.Command;
        if (!CancellationRequiresHold(command))
        {
            await _controls.CancelAsync(entry.Plan,
                command == OperatorCommandKind.Hold
                    ? "Hold is already the vehicle's safe stationary state."
                    : $"{command} cancellation recorded; no Hold action is applicable.",
                cancellationToken);
            _active.Remove(entry.Plan.Target.VehicleId);
            return new(entry.QueueId, entry.Plan.CommandId, entry.RequestUnitId, true,
                OperatorWorkflowState.Cancelled,
                command == OperatorCommandKind.Hold
                    ? "Vehicle is already in Hold; active command cleared."
                    : $"{OperatorCommandLabel(command)} cancellation recorded.");
        }

        var telemetry = LatestTelemetry(entry.Plan.Target.VehicleId);
        if (telemetry is { Armed: false })
        {
            await _controls.CancelAsync(entry.Plan,
                "Cancelled by operator; the vehicle is disarmed.", cancellationToken);
            _active.Remove(entry.Plan.Target.VehicleId);
            return new(entry.QueueId, entry.Plan.CommandId, entry.RequestUnitId, true,
                OperatorWorkflowState.Cancelled,
                $"{OperatorCommandLabel(command)} cancelled; the vehicle is disarmed.");
        }

        var hold = await _controls.PrepareAsync(entry.RequestUnitId, OperatorCommandKind.Hold,
            $"Cancel active {OperatorCommandLabel(command)}", OperatorCommandParameters.None, cancellationToken);
        if (!hold.CanSubmit)
        {
            var finding = hold.Findings.FirstOrDefault(item => item.Severity == OperatorPreflightSeverity.Blocking);
            var message = finding?.Message ?? "Unable to prepare the vehicle Hold command.";
            return ToExecution(entry, new OperatorCommandResult(
                false, OperationalCommandState.Rejected,
                $"Could not cancel {OperatorCommandLabel(command)}: {message}"));
        }

        var requestedHold = await _controls.ExecuteAsync(hold, cancellationToken);
        if (!requestedHold.Accepted)
        {
            return ToExecution(entry, requestedHold with
            {
                Message = $"Could not cancel {OperatorCommandLabel(command)}: {requestedHold.Message}"
            });
        }

        var confirmedHold = await WaitForHoldConfirmationAsync(hold, requestedHold, cancellationToken);
        if (!confirmedHold.Accepted)
        {
            return ToExecution(entry, confirmedHold with
            {
                Message = $"{OperatorCommandLabel(command)} was not cancelled: {confirmedHold.Message}"
            });
        }

        await _controls.CancelAsync(entry.Plan,
            $"Cancelled by operator; Hold confirmed after {OperatorCommandLabel(command)}.", cancellationToken);
        _active.Remove(entry.Plan.Target.VehicleId);
        return ToExecution(entry, confirmedHold with
        {
            Message = $"Hold confirmed; {OperatorCommandLabel(command)} cancelled."
        });
    }

    private async Task<OperatorCommandResult> WaitForHoldConfirmationAsync(
        OperatorCommandPlan hold,
        OperatorCommandResult requested,
        CancellationToken cancellationToken)
    {
        if (requested.State == OperationalCommandState.Succeeded)
            return requested;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_commands.TryGet(hold.CommandId, out var record) && record is not null)
            {
                if (record.State == OperationalCommandState.Succeeded)
                {
                    return new(true, record.State, record.Message, requested.OperationId);
                }

                if (record.State is OperationalCommandState.Rejected or
                    OperationalCommandState.Failed or
                    OperationalCommandState.TimedOut or
                    OperationalCommandState.Cancelled)
                {
                    return new(false, record.State, record.Message, requested.OperationId);
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        return new(false, OperationalCommandState.TimedOut,
            "Hold was accepted, but the vehicle did not confirm Hold mode within 8 seconds. Check the vehicle mode and telemetry before retrying.",
            requested.OperationId);
    }

    private static bool CancellationRequiresHold(OperatorCommandKind command)
        => command is OperatorCommandKind.Takeoff or
            OperatorCommandKind.GoTo or
            OperatorCommandKind.Land or
            OperatorCommandKind.Recover or
            OperatorCommandKind.ChangeAltitude or
            OperatorCommandKind.SetHeading;

    private static string OperatorCommandLabel(OperatorCommandKind command) => command switch
    {
        OperatorCommandKind.Recover => "Return home",
        OperatorCommandKind.GoTo => "Go To",
        OperatorCommandKind.ChangeAltitude => "Change altitude",
        OperatorCommandKind.SetHeading => "Set heading",
        _ => command.ToString()
    };

    private async Task RefreshAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            foreach (var entry in _queued.Values.ToArray())
            {
                try
                {
                    var assessment = await _controls.AssessAsync(entry.RequestUnitId, entry.Plan.Command, entry.Plan.Reason,
                        entry.Plan.Parameters ?? OperatorCommandParameters.None);
                    _queued[entry.QueueId] = entry with { Assessment = assessment };
                }
                catch
                {
                    // Preserve the last assessment; the next scheduled pass will retry.
                }
            }
            RaiseChanged();
        }
        finally { _gate.Release(); }
    }

    private void OnCommandsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var terminal = _active.Where(pair => !_cancelling.Contains(pair.Key) &&
            _commands.TryGet(pair.Value.Plan.CommandId, out var command) && command is not null &&
            command.State is OperationalCommandState.Succeeded or OperationalCommandState.Cancelled or OperationalCommandState.Rejected or OperationalCommandState.Failed or OperationalCommandState.TimedOut)
            .Select(pair => pair.Key).ToArray();
        foreach (var vehicleId in terminal) _active.Remove(vehicleId);
        RaiseChanged();
    }

    private IReadOnlyList<QueueEntry> FindQueued(string queueOrBatchId)
        => _queued.Values.Where(entry => string.Equals(entry.QueueId, queueOrBatchId, StringComparison.Ordinal) ||
                                         string.Equals(entry.BatchId, queueOrBatchId, StringComparison.Ordinal) ||
                                         string.Equals(entry.Plan.CommandId, queueOrBatchId, StringComparison.Ordinal)).ToArray();

    private VehicleTelemetryRecord? LatestTelemetry(string vehicleId) => _telemetry.Items
        .Where(item => string.Equals(item.VehicleId, vehicleId, StringComparison.Ordinal))
        .OrderByDescending(item => item.ObservedAt).FirstOrDefault();

    private OperatorCommandBatchSnapshot ToBatch(string batchId, OperatorWorkflowCommandKind command, string displayName, IEnumerable<QueueEntry> entries)
        => new(batchId, command, displayName, entries.Select(ToSnapshot).ToArray());

    private static OperatorCommandExecutionSnapshot ToExecution(QueueEntry entry, OperatorCommandResult result)
        => new(entry.QueueId, entry.Plan.CommandId, entry.RequestUnitId, result.Accepted, ToWorkflow(result.State), result.Message, result.OperationId);

    private OperatorCommandQueueSnapshot ToSnapshot(QueueEntry entry)
    {
        var assessment = entry.Assessment ?? entry.Plan;
        var state = _active.ContainsKey(entry.Plan.Target.VehicleId) &&
                    _commands.TryGet(entry.Plan.CommandId, out var activeRecord) && activeRecord is not null
            ? ToWorkflow(activeRecord.State)
            : OperatorWorkflowState.Queued;
        return new(entry.QueueId, entry.BatchId, entry.Plan.CommandId, entry.RequestUnitId, entry.Plan.Target.VehicleId,
            entry.Plan.TargetName, ToWorkflow(entry.Plan.Command), entry.Plan.DisplayName, entry.Plan.Safety.ToString(), state,
            ToWorkflow(assessment.Availability), entry.Plan.Reason, ToCore(entry.Plan.Parameters ?? OperatorCommandParameters.None),
            assessment.Findings.Select(ToCore).ToArray(), assessment.Policy.Decision, assessment.Policy.Summary,
            entry.Plan.CreatedAt, entry.Plan.ExpiresAt,
            entry.Plan.ExpiresAt <= DateTimeOffset.UtcNow && state == OperatorWorkflowState.Queued
                ? "Queued preparation expired; execute will refresh it."
                : assessment.Availability is OperatorControlAvailability.Ready or OperatorControlAvailability.Warning
                    ? "Queued and ready for execution."
                    : assessment.Findings.FirstOrDefault(item => item.Severity == OperatorPreflightSeverity.Blocking)?.Message ?? "Command is unavailable.");
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Dispose();
        ((INotifyCollectionChanged)_commands.Items).CollectionChanged -= OnCommandsChanged;
        _gate.Dispose();
    }

    private sealed record QueueEntry(string QueueId, string BatchId, string RequestUnitId, OperatorCommandPlan Plan,
        bool RollbackAcceptedGoToOnPartialFailure, OperatorCommandPlan? Assessment);

    private static OperatorCommandKind ToRuntime(OperatorWorkflowCommandKind value) => value switch
    {
        OperatorWorkflowCommandKind.ReturnHome => OperatorCommandKind.Recover,
        _ => Enum.Parse<OperatorCommandKind>(value.ToString(), true)
    };

    private static OperatorWorkflowCommandKind ToWorkflow(OperatorCommandKind value) => value switch
    {
        OperatorCommandKind.Recover => OperatorWorkflowCommandKind.ReturnHome,
        _ => Enum.Parse<OperatorWorkflowCommandKind>(value.ToString(), true)
    };

    private static OperatorWorkflowAvailability ToWorkflow(OperatorControlAvailability value) => value switch
    {
        OperatorControlAvailability.Ready => OperatorWorkflowAvailability.Ready,
        OperatorControlAvailability.Warning => OperatorWorkflowAvailability.Warning,
        OperatorControlAvailability.Blocked => OperatorWorkflowAvailability.Blocked,
        _ => OperatorWorkflowAvailability.Unavailable
    };

    private static OperatorWorkflowState ToWorkflow(OperationalCommandState value) => value switch
    {
        OperationalCommandState.Draft => OperatorWorkflowState.Queued,
        OperationalCommandState.Submitting => OperatorWorkflowState.Submitting,
        OperationalCommandState.Accepted => OperatorWorkflowState.Accepted,
        OperationalCommandState.InProgress => OperatorWorkflowState.InProgress,
        OperationalCommandState.Succeeded => OperatorWorkflowState.Succeeded,
        OperationalCommandState.Cancelled => OperatorWorkflowState.Cancelled,
        OperationalCommandState.Rejected => OperatorWorkflowState.Rejected,
        OperationalCommandState.TimedOut => OperatorWorkflowState.TimedOut,
        _ => OperatorWorkflowState.Failed
    };

    private static OperatorWorkflowFinding ToCore(OperatorPreflightFinding value) => new(value.Code, value.Severity switch
    {
        OperatorPreflightSeverity.Warning => OperatorWorkflowSeverity.Warning,
        OperatorPreflightSeverity.Blocking => OperatorWorkflowSeverity.Blocking,
        _ => OperatorWorkflowSeverity.Info
    }, value.Message, value.Source);

    private static OperatorCommandParameters ToRuntime(OperatorWorkflowParameters value) => new(
        value.TakeoffAltitudeAglMetres,
        value.GoToTargetKind is null ? null : (OperatorGoToTargetKind)value.GoToTargetKind.Value,
        value.GoToLatitudeDegrees, value.GoToLongitudeDegrees, value.GoToAltitudeAmslMetres,
        value.GoToNorthMetres, value.GoToEastMetres, value.GoToDownMetres, value.GoToYawDegrees, value.GoToAcceptanceRadiusMetres,
        value.AltitudeTargetKind is null ? null : (OperatorAltitudeTargetKind)value.AltitudeTargetKind.Value,
        value.AltitudeAmslMetres, value.AltitudeAglMetres, value.AltitudeRelativeDeltaMetres,
        value.HeadingTargetKind is null ? null : (OperatorHeadingTargetKind)value.HeadingTargetKind.Value,
        value.HeadingDegrees, value.RelativeYawDegrees, value.AirborneDisarmConfirmed,
        value.GimbalPitchDegrees, value.GimbalYawDegrees, value.GimbalRollDegrees,
        value.GimbalZoomPercent, value.GimbalEarthFrame);

    private static OperatorWorkflowParameters ToCore(OperatorCommandParameters value) => new(
        value.TakeoffAltitudeAglMetres,
        value.GoToTargetKind is null ? null : (OperatorWorkflowGoToTargetKind)value.GoToTargetKind.Value,
        value.GoToLatitudeDegrees, value.GoToLongitudeDegrees, value.GoToAltitudeAmslMetres,
        value.GoToNorthMetres, value.GoToEastMetres, value.GoToDownMetres, value.GoToYawDegrees, value.GoToAcceptanceRadiusMetres,
        value.AltitudeTargetKind is null ? null : (OperatorWorkflowAltitudeTargetKind)value.AltitudeTargetKind.Value,
        value.AltitudeAmslMetres, value.AltitudeAglMetres, value.AltitudeRelativeDeltaMetres,
        value.HeadingTargetKind is null ? null : (OperatorWorkflowHeadingTargetKind)value.HeadingTargetKind.Value,
        value.HeadingDegrees, value.RelativeYawDegrees, value.AirborneDisarmConfirmed,
        value.GimbalPitchDegrees, value.GimbalYawDegrees, value.GimbalRollDegrees,
        value.GimbalZoomPercent, value.GimbalEarthFrame);

    private static string DisplayName(OperatorWorkflowCommandKind command) => command switch
    {
        OperatorWorkflowCommandKind.ReturnHome => "Return home",
        OperatorWorkflowCommandKind.GoTo => "Go to",
        OperatorWorkflowCommandKind.ChangeAltitude => "Change altitude",
        OperatorWorkflowCommandKind.SetHeading => "Set heading",
        _ => command.ToString()
    };
}
