using System.Text.Json;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.State;

namespace RobotCommand.Services.Missions;

public sealed class OperationalRunService : IOperationalRunService, IDisposable
{
    private static readonly TimeSpan PreparationLifetime = TimeSpan.FromMinutes(5);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly IBehaviourWorkspaceService _behaviours;
    private readonly IBehaviourBindingWorkspaceService _bindings;
    private readonly IMissionTaskWorkspaceService _workspace;
    private readonly IEntityStore<string, MissionRecord> _missions;
    private readonly IEntityStore<string, OperationalTaskRecord> _tasks;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly ILogger<OperationalRunService> _logger;
    private readonly BehaviourParameterSchemaReader _parameterSchemas = new();
    private readonly Dictionary<string, OperationalRunPreparation> _preparations = new(StringComparer.Ordinal);

    public OperationalRunService(
        IBehaviourWorkspaceService behaviours,
        IBehaviourBindingWorkspaceService bindings,
        IMissionTaskWorkspaceService workspace,
        IEntityStore<string, MissionRecord> missions,
        IEntityStore<string, OperationalTaskRecord> tasks,
        IEntityStore<string, VehicleRecord> vehicles,
        ILogger<OperationalRunService> logger)
    {
        _behaviours = behaviours;
        _bindings = bindings;
        _workspace = workspace;
        _missions = missions;
        _tasks = tasks;
        _vehicles = vehicles;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<OperationalRunPreparation> Preparations
    {
        get
        {
            lock (_stateGate)
            {
                return _preparations.Values
                    .OrderByDescending(item => item.CreatedAt)
                    .ToArray();
            }
        }
    }

    public async Task<IReadOnlyList<BehaviourPackageOption>> ListCompatibleBehavioursAsync(
        string connectionId,
        string vehicleId,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var vehicle = GetVehicle(vehicleId);
        EnsureVehicleRoute(vehicle, connectionId);
        var snapshot = await RefreshBehaviourWorkspaceAsync(
            connectionId,
            vehicle,
            refresh,
            cancellationToken);
        EnsureAuthoritativeInventory(snapshot);

        var compatible = new List<BehaviourPackageOption>();
        foreach (var entry in snapshot.Entries.Where(item =>
                     item.Remote is not null &&
                     item.Compatible &&
                     !string.IsNullOrWhiteSpace(item.Identity.Version)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = await ToQuickRunOptionAsync(
                connectionId,
                entry.Remote!,
                entry.Local,
                refreshGeometry: refresh,
                cancellationToken: cancellationToken);
            if (package.SupportsQuickRun)
            {
                compatible.Add(package);
            }
        }

        return compatible
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<OperationalRunPreparation> PrepareAsync(
        OperationalRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var vehicle = GetVehicle(request.VehicleId);
        EnsureVehicleRoute(vehicle, request.ConnectionId);
        var resolved = await ResolveInstalledBehaviourAsync(
            request.ConnectionId,
            vehicle,
            request.BehaviourId,
            request.BehaviourVersion,
            refresh: true,
            cancellationToken: cancellationToken);
        var behaviour = resolved.Package;
        var geometryReadiness = resolved.BindingSnapshot.Readiness;
        var parametersJson = NormalizeParameters(request.ParametersJson);
        var operationId = $"quick-run-{Guid.NewGuid():N}";
        var suffix = operationId[^12..];
        var missionId = $"quick-mission-{suffix}";
        var taskId = $"quick-task-{suffix}";
        var blockers = BuildBlockers(vehicle, behaviour);
        var warnings = BuildWarnings(vehicle, behaviour);
        if (string.IsNullOrWhiteSpace(resolved.Remote.ContentSha256))
        {
            warnings.Add(
                "Logos did not report a package-wide SHA; Quick Run cannot detect in-place package changes before launch.");
        }
        var objective = request.Objective.Trim();
        var missionName = string.IsNullOrWhiteSpace(request.MissionName)
            ? $"Quick run · {behaviour.DisplayName}"
            : request.MissionName.Trim();
        var taskName = string.IsNullOrWhiteSpace(request.TaskName)
            ? behaviour.DisplayName
            : request.TaskName.Trim();
        var payloadJson = JsonSerializer.Serialize(new
        {
            kind = "logos.robot-command.quick-run.v1",
            operationId,
            behaviourId = behaviour.BehaviourId,
            behaviourVersion = behaviour.Version,
            vehicleId = vehicle.Id
        });

        var mission = new MissionRecord(
            missionId,
            missionName,
            "Draft",
            AssignedTeamId: vehicle.TeamId,
            AssignedVehicleId: vehicle.Id,
            ConnectionId: request.ConnectionId.Trim(),
            Objective: objective,
            Priority: NormalizePriority(request.Priority),
            PolicyId: request.PolicyId?.Trim() ?? string.Empty,
            GeometryIds: geometryReadiness?.GeometryIds ?? [],
            RequiredCapabilities: behaviour.RequiredCapabilities,
            PayloadJson: payloadJson,
            ValidationState: PlanValidationState.NotValidated,
            ValidationSummary: "Quick Run draft",
            IsLocalDraft: true);
        var task = new OperationalTaskRecord(
            taskId,
            taskName,
            "Draft",
            MissionId: missionId,
            AssignedVehicleId: vehicle.Id,
            ConnectionId: request.ConnectionId.Trim(),
            TeamId: vehicle.TeamId,
            AssignedLogosInstanceId: vehicle.LogosInstanceId,
            Objective: objective,
            TaskType: string.IsNullOrWhiteSpace(request.TaskType) ? "quick-run" : request.TaskType.Trim(),
            Priority: NormalizePriority(request.Priority),
            BehaviourId: behaviour.BehaviourId,
            BehaviourVersion: behaviour.Version,
            PackageId: behaviour.BehaviourId,
            ParametersJson: parametersJson,
            AssignmentState: "Planned",
            ValidationState: PlanValidationState.NotValidated,
            ValidationSummary: "Quick Run draft",
            IsLocalDraft: true,
            GeometryIds: geometryReadiness?.GeometryIds ?? []);

        _missions.Upsert(mission);
        _tasks.Upsert(task);

        var missionValidation = await _workspace.ValidateMissionAsync(missionId, cancellationToken);
        var taskValidation = await _workspace.ValidateTaskForVehicleAsync(taskId, vehicle.Id, cancellationToken);
        if (request.RejectOnWarnings)
        {
            if (missionValidation.State == PlanValidationState.Warning)
            {
                blockers.Add("Mission validation returned warnings and this run is configured to reject warnings.");
            }

            if (taskValidation.State == PlanValidationState.Warning)
            {
                blockers.Add("Task validation returned warnings and this run is configured to reject warnings.");
            }
        }

        if (!missionValidation.IsValid)
        {
            blockers.Add(missionValidation.Summary);
        }

        if (!taskValidation.IsValid)
        {
            blockers.Add(taskValidation.Summary);
        }

        var stage = blockers.Count == 0
            ? OperationalRunStage.Prepared
            : OperationalRunStage.Rejected;
        var preparation = new OperationalRunPreparation(
            operationId,
            request with { ParametersJson = parametersJson },
            behaviour,
            missionId,
            taskId,
            missionValidation,
            taskValidation,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            blockers.Distinct(StringComparer.Ordinal).ToArray(),
            stage,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(PreparationLifetime),
            geometryReadiness,
            resolved.Remote.ContentSha256 ?? string.Empty,
            BindingFingerprint(resolved.BindingSnapshot));
        SetPreparation(preparation);
        return preparation;
    }

    public async Task<OperationalRunResult> LaunchAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var preparation = GetPreparation(operationId);
        if (!preparation.CanLaunch)
        {
            return OperationalRunResult.Rejected(
                preparation,
                preparation.ExpiresAt <= DateTimeOffset.UtcNow
                    ? "The Quick Run preparation expired. Prepare the run again."
                    : string.Join(" ", preparation.Blockers.DefaultIfEmpty("The Quick Run preparation is not launchable.")));
        }

        await _launchGate.WaitAsync(cancellationToken);
        var steps = new List<OperationalRunStepResult>();
        GatewayCommandResult? missionStart = null;
        var cleanupAttempted = false;
        string? cleanupMessage = null;
        try
        {
            preparation = GetPreparation(operationId);
            if (!preparation.CanLaunch)
            {
                return OperationalRunResult.Rejected(preparation, "The Quick Run preparation is no longer launchable.");
            }

            var vehicle = GetVehicle(preparation.Request.VehicleId);
            EnsureVehicleRoute(vehicle, preparation.Request.ConnectionId);
            var resolved = await ResolveInstalledBehaviourAsync(
                preparation.Request.ConnectionId,
                vehicle,
                preparation.Behaviour.BehaviourId,
                preparation.Behaviour.Version,
                refresh: true,
                cancellationToken: cancellationToken);
            var installed = resolved.Package;
            var currentBlockers = BuildBlockers(vehicle, installed);
            if (!string.Equals(
                    preparation.BehaviourPackageSha256,
                    resolved.Remote.ContentSha256 ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
            {
                currentBlockers.Add("The installed behaviour package content changed after preparation. Prepare the run again.");
            }
            if (!string.Equals(
                    preparation.BehaviourBindingFingerprint,
                    BindingFingerprint(resolved.BindingSnapshot),
                    StringComparison.Ordinal))
            {
                currentBlockers.Add("Behaviour geometry bindings changed after preparation. Prepare the run again.");
            }
            if (currentBlockers.Count > 0)
            {
                return Reject(preparation, steps, string.Join(" ", currentBlockers));
            }

            var missionValidation = await _workspace.ValidateMissionAsync(preparation.MissionId, cancellationToken);
            var taskValidation = await _workspace.ValidateTaskForVehicleAsync(
                preparation.TaskId,
                vehicle.Id,
                cancellationToken);
            if (!missionValidation.IsValid || !taskValidation.IsValid)
            {
                return Reject(
                    preparation,
                    steps,
                    $"Final validation failed. Mission: {missionValidation.Summary} Task: {taskValidation.Summary}");
            }

            UpdateStage(preparation.OperationId, OperationalRunStage.PublishingMission);
            var publishMission = await _workspace.PublishMissionAsync(
                preparation.MissionId,
                Identity(preparation, "mission.publish"),
                cancellationToken: cancellationToken);
            steps.Add(ToStep("Publish mission", publishMission));
            if (!publishMission.Accepted)
            {
                return Reject(preparation, steps, publishMission.Message);
            }

            UpdateStage(preparation.OperationId, OperationalRunStage.PublishingTask);
            var publishTask = await _workspace.PublishTaskAsync(
                preparation.TaskId,
                Identity(preparation, "task.publish"),
                cancellationToken: cancellationToken);
            steps.Add(ToStep("Publish task", publishTask));
            if (!publishTask.Accepted)
            {
                return Reject(preparation, steps, publishTask.Message);
            }

            UpdateStage(preparation.OperationId, OperationalRunStage.ValidatingAssignment);
            taskValidation = await _workspace.ValidateTaskForVehicleAsync(
                preparation.TaskId,
                vehicle.Id,
                cancellationToken);
            if (!taskValidation.IsValid)
            {
                return Reject(preparation, steps, taskValidation.Summary);
            }

            UpdateStage(preparation.OperationId, OperationalRunStage.AssigningTask);
            var assignment = await _workspace.AssignTaskAsync(
                preparation.TaskId,
                vehicle.Id,
                Identity(preparation, "task.assign"),
                cancellationToken: cancellationToken);
            steps.Add(ToStep("Assign task", assignment));
            if (!assignment.Accepted)
            {
                return Reject(preparation, steps, assignment.Message);
            }

            UpdateStage(preparation.OperationId, OperationalRunStage.StartingMission);
            missionStart = await _workspace.ExecuteMissionCommandAsync(
                new MissionCommandRequest(
                    preparation.Request.ConnectionId,
                    preparation.MissionId,
                    "Start",
                    RequestId: Identity(preparation, "mission.start").RequestId,
                    CorrelationId: preparation.OperationId,
                    IdempotencyKey: $"{preparation.OperationId}:mission.start"),
                cancellationToken);
            steps.Add(ToStep("Start mission", missionStart));
            if (!missionStart.Accepted)
            {
                return Reject(preparation, steps, missionStart.Message);
            }

            UpdateStage(preparation.OperationId, OperationalRunStage.StartingTask);
            var task = GetTask(preparation.TaskId);
            var taskIdentity = Identity(preparation, "task.start");
            var taskStart = await _workspace.ExecuteTaskCommandAsync(
                new TaskCommandRequest(
                    preparation.Request.ConnectionId,
                    preparation.TaskId,
                    "Start",
                    task.TaskExecutionId ?? assignment.ExecutionId,
                    task.AssignedVehicleId,
                    task.AssignedMemberId,
                    task.AssignedLogosInstanceId,
                    RequestId: taskIdentity.RequestId,
                    CorrelationId: preparation.OperationId,
                    IdempotencyKey: taskIdentity.IdempotencyKey),
                cancellationToken);
            steps.Add(ToStep("Start task", taskStart));
            if (!taskStart.Accepted)
            {
                (cleanupAttempted, cleanupMessage) = await CleanupMissionAsync(
                    preparation,
                    missionStart.ExecutionId,
                    "Quick Run task start was rejected.");
                return Fail(
                    preparation,
                    steps,
                    taskStart.Message,
                    missionStart.ExecutionId,
                    taskStart.ExecutionId,
                    cleanupAttempted,
                    cleanupMessage);
            }

            UpdateStage(preparation.OperationId, OperationalRunStage.Running);
            return new OperationalRunResult(
                preparation.OperationId,
                preparation.MissionId,
                preparation.TaskId,
                OperationalRunStage.Running,
                true,
                $"{preparation.Behaviour.DisplayName} is running on {vehicle.Name}.",
                steps,
                missionStart.ExecutionId,
                taskStart.ExecutionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (missionStart?.Accepted == true)
            {
                (cleanupAttempted, cleanupMessage) = await CleanupMissionAsync(
                    preparation,
                    missionStart.ExecutionId,
                    "Quick Run launch was cancelled.");
            }

            UpdateStage(preparation.OperationId, OperationalRunStage.Cancelled);
            return new OperationalRunResult(
                preparation.OperationId,
                preparation.MissionId,
                preparation.TaskId,
                OperationalRunStage.Cancelled,
                false,
                "Quick Run launch was cancelled.",
                steps,
                missionStart?.ExecutionId,
                CleanupAttempted: cleanupAttempted,
                CleanupMessage: cleanupMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quick Run {OperationId} failed", preparation.OperationId);
            if (missionStart?.Accepted == true)
            {
                (cleanupAttempted, cleanupMessage) = await CleanupMissionAsync(
                    preparation,
                    missionStart.ExecutionId,
                    "Quick Run launch failed after mission start.");
            }

            return Fail(
                preparation,
                steps,
                ex.Message,
                missionStart?.ExecutionId,
                null,
                cleanupAttempted,
                cleanupMessage);
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public Task DiscardAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OperationalRunPreparation? preparation;
        lock (_stateGate)
        {
            _preparations.Remove(operationId, out preparation);
        }

        if (preparation is null)
        {
            return Task.CompletedTask;
        }

        if (_missions.TryGet(preparation.MissionId, out var mission) && mission is { IsLocalDraft: true })
        {
            _missions.Remove(preparation.MissionId);
        }

        if (_tasks.TryGet(preparation.TaskId, out var task) && task is { IsLocalDraft: true })
        {
            _tasks.Remove(preparation.TaskId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task<(bool Attempted, string Message)> CleanupMissionAsync(
        OperationalRunPreparation preparation,
        string? missionExecutionId,
        string reason)
    {
        try
        {
            var identity = Identity(preparation, "mission.cleanup.cancel");
            var result = await _workspace.ExecuteMissionCommandAsync(
                new MissionCommandRequest(
                    preparation.Request.ConnectionId,
                    preparation.MissionId,
                    "Cancel",
                    missionExecutionId,
                    reason,
                    RequestId: identity.RequestId,
                    CorrelationId: identity.CorrelationId,
                    IdempotencyKey: identity.IdempotencyKey),
                CancellationToken.None);
            return (true, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up Quick Run mission {MissionId}", preparation.MissionId);
            return (true, $"Cleanup failed: {ex.Message}");
        }
    }

    private OperationalRunResult Reject(
        OperationalRunPreparation preparation,
        IReadOnlyList<OperationalRunStepResult> steps,
        string message)
    {
        UpdateStage(preparation.OperationId, OperationalRunStage.Rejected);
        return new OperationalRunResult(
            preparation.OperationId,
            preparation.MissionId,
            preparation.TaskId,
            OperationalRunStage.Rejected,
            false,
            message,
            steps);
    }

    private OperationalRunResult Fail(
        OperationalRunPreparation preparation,
        IReadOnlyList<OperationalRunStepResult> steps,
        string message,
        string? missionExecutionId,
        string? taskExecutionId,
        bool cleanupAttempted,
        string? cleanupMessage)
    {
        UpdateStage(preparation.OperationId, OperationalRunStage.Failed);
        return new OperationalRunResult(
            preparation.OperationId,
            preparation.MissionId,
            preparation.TaskId,
            OperationalRunStage.Failed,
            false,
            message,
            steps,
            missionExecutionId,
            taskExecutionId,
            cleanupAttempted,
            cleanupMessage);
    }

    private static OperationalRunStepResult ToStep(string step, GatewayCommandResult result)
        => new(
            step,
            result.Accepted,
            result.State,
            result.Message,
            result.ExecutionId,
            result.LifecycleState);

    private static WorkspaceCommandIdentity Identity(
        OperationalRunPreparation preparation,
        string step)
        => WorkspaceCommandIdentity.Create(preparation.OperationId, step);

    private static void ValidateRequest(OperationalRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
        {
            throw new ArgumentException("A Logos connection is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.VehicleId))
        {
            throw new ArgumentException("A vehicle is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.BehaviourId))
        {
            throw new ArgumentException("A behaviour package is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Objective))
        {
            throw new ArgumentException("A Quick Run objective is required.", nameof(request));
        }
    }

    private static string NormalizeParameters(string? value)
    {
        var json = string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Quick Run parameters must be a JSON object.", nameof(value));
            }

            return document.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Quick Run parameters are not valid JSON: {ex.Message}", nameof(value), ex);
        }
    }

    private static string NormalizePriority(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Normal" : value.Trim();

    private async Task<BehaviourWorkspaceSnapshot> RefreshBehaviourWorkspaceAsync(
        string connectionId,
        VehicleRecord vehicle,
        bool refresh,
        CancellationToken cancellationToken)
        => await _behaviours.RefreshAsync(
            connectionId,
            BehaviourCompatibilityTarget.Create(
                vehicle.Id,
                vehicle.Name,
                vehicle.CapabilityKeys,
                vehicle.ProfileKey),
            refreshRemote: refresh,
            cancellationToken: cancellationToken);

    private static void EnsureAuthoritativeInventory(BehaviourWorkspaceSnapshot snapshot)
    {
        if (!snapshot.RemoteInventory.Available || snapshot.RemoteInventory.Stale)
        {
            throw new InvalidOperationException(snapshot.RemoteInventory.Summary);
        }
    }

    private async Task<(BehaviourPackageOption Package, BehaviourBindingWorkspaceSnapshot BindingSnapshot, RemoteBehaviourPackageRecord Remote)>
        ResolveInstalledBehaviourAsync(
            string connectionId,
            VehicleRecord vehicle,
            string behaviourId,
            string version,
            bool refresh,
            CancellationToken cancellationToken)
    {
        var identity = new BehaviourPackageIdentity(behaviourId, version);
        var snapshot = await RefreshBehaviourWorkspaceAsync(
            connectionId,
            vehicle,
            refresh,
            cancellationToken);
        EnsureAuthoritativeInventory(snapshot);
        var entry = snapshot.Entries.FirstOrDefault(item => item.Identity == identity)
                    ?? throw new KeyNotFoundException(
                        $"Behaviour package '{identity}' is not installed on Logos connection '{connectionId}'.");
        if (entry.Remote is null)
        {
            throw new InvalidOperationException(
                $"Behaviour package '{identity}' exists only in the local Robot Command library.");
        }

        if (entry.Compatibility?.Compatible == false)
        {
            throw new InvalidOperationException(entry.Compatibility.Summary);
        }

        var bindingSnapshot = await _bindings.InspectAsync(
            connectionId,
            identity,
            refreshPackages: false,
            refreshGeometry: refresh,
            cancellationToken: cancellationToken);
        var package = BehaviourBindingPackageOption.FromRemote(entry.Remote)
            .ToLegacyOption(bindingSnapshot.Readiness) with
        {
            ParameterSchema = await ReadParameterSchemaAsync(entry.Local, cancellationToken)
        };
        return (package, bindingSnapshot, entry.Remote);
    }

    private async Task<BehaviourPackageOption> ToQuickRunOptionAsync(
        string connectionId,
        RemoteBehaviourPackageRecord remote,
        LocalBehaviourPackageRecord? local,
        bool refreshGeometry,
        CancellationToken cancellationToken)
    {
        var bindings = await _bindings.InspectAsync(
            connectionId,
            remote.Identity,
            refreshPackages: false,
            refreshGeometry: refreshGeometry,
            cancellationToken: cancellationToken);
        return BehaviourBindingPackageOption.FromRemote(remote)
            .ToLegacyOption(bindings.Readiness) with
        {
            ParameterSchema = await ReadParameterSchemaAsync(local, cancellationToken)
        };
    }

    private async Task<BehaviourParameterSchema?> ReadParameterSchemaAsync(
        LocalBehaviourPackageRecord? local,
        CancellationToken cancellationToken)
    {
        if (local is null) return null;
        return await _parameterSchemas.ReadAsync(local, cancellationToken);
    }

    private static string BindingFingerprint(BehaviourBindingWorkspaceSnapshot snapshot)
        => string.Join("|", snapshot.Slots
            .OrderBy(item => item.SlotId, StringComparer.Ordinal)
            .Select(item =>
                $"{item.SlotId}={item.GeometryId ?? string.Empty}:{item.State}"));

    private static List<string> BuildBlockers(
        VehicleRecord vehicle,
        BehaviourPackageOption behaviour)
    {
        var blockers = new List<string>();
        if (vehicle.State is not AvailabilityState.Online and not AvailabilityState.Degraded)
        {
            blockers.Add($"Vehicle '{vehicle.Name}' is {vehicle.State}.");
        }

        var capabilities = (vehicle.CapabilityKeys ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = behaviour.RequiredCapabilities
            .Where(item => !capabilities.Contains(item))
            .ToArray();
        if (missing.Length > 0)
        {
            blockers.Add($"Vehicle '{vehicle.Name}' is missing required capabilities: {string.Join(", ", missing)}.");
        }

        if (behaviour.GeometrySlots.Any(item => item.RequiresResolvedGeometry))
        {
            if (behaviour.GeometryReadiness is null)
            {
                blockers.Add("Behaviour geometry bindings have not been inspected.");
            }
            else
            {
                blockers.AddRange(behaviour.GeometryReadiness.Blockers);
            }
        }

        return blockers;
    }

    private static List<string> BuildWarnings(
        VehicleRecord vehicle,
        BehaviourPackageOption behaviour)
    {
        var warnings = new List<string>();
        if (vehicle.State == AvailabilityState.Degraded)
        {
            warnings.Add($"Vehicle '{vehicle.Name}' is degraded; Logos validation will run again before launch.");
        }

        if (behaviour.GeometryReadiness?.Warnings is { Count: > 0 })
        {
            warnings.AddRange(behaviour.GeometryReadiness.Warnings);
        }

        if (behaviour.GeometryReadiness is { Ready: true } readiness && readiness.GeometryIds.Count > 0)
        {
            warnings.Add($"Quick Run will use registered geometry: {string.Join(", ", readiness.GeometryIds)}.");
        }

        return warnings;
    }

    private static void EnsureVehicleRoute(VehicleRecord vehicle, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) ||
            !vehicle.ConnectionIds.Contains(connectionId.Trim(), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Vehicle '{vehicle.Name}' is not available through Logos connection '{connectionId}'.");
        }
    }

    private VehicleRecord GetVehicle(string id)
        => _vehicles.TryGet(id, out var vehicle) && vehicle is not null
            ? vehicle
            : throw new KeyNotFoundException($"Vehicle '{id}' was not found.");

    private OperationalTaskRecord GetTask(string id)
        => _tasks.TryGet(id, out var task) && task is not null
            ? task
            : throw new KeyNotFoundException($"Task '{id}' was not found.");

    private OperationalRunPreparation GetPreparation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }

        lock (_stateGate)
        {
            return _preparations.TryGetValue(operationId, out var preparation)
                ? preparation
                : throw new KeyNotFoundException($"Quick Run preparation '{operationId}' was not found.");
        }
    }

    private void SetPreparation(OperationalRunPreparation preparation)
    {
        lock (_stateGate)
        {
            _preparations[preparation.OperationId] = preparation;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _launchGate.Dispose();

    private void UpdateStage(string operationId, OperationalRunStage stage)
    {
        lock (_stateGate)
        {
            if (_preparations.TryGetValue(operationId, out var preparation))
            {
                _preparations[operationId] = preparation with { Stage = stage };
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
