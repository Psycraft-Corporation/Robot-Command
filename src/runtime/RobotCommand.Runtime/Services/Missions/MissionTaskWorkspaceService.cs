using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.Services.Missions;

public sealed class MissionTaskWorkspaceService : IMissionTaskWorkspaceService
{
    private readonly IMissionTaskDocumentService _documents;
    private readonly IMissionTaskGateway _gateway;
    private readonly IEntityStore<string, MissionRecord> _missions;
    private readonly IEntityStore<string, OperationalTaskRecord> _tasks;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;

    public MissionTaskWorkspaceService(
        IMissionTaskDocumentService documents,
        IMissionTaskGateway gateway,
        IEntityStore<string, MissionRecord> missions,
        IEntityStore<string, OperationalTaskRecord> tasks,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, OperationalCommandRecord> commands)
    {
        _documents = documents;
        _gateway = gateway;
        _missions = missions;
        _tasks = tasks;
        _vehicles = vehicles;
        _commands = commands;
    }

    public bool GatewayAvailable => _gateway.IsAvailable;

    public string GatewayStatus => _gateway.AvailabilityMessage;

    public async Task<MissionRecord> ImportMissionAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var document = await _documents.LoadMissionAsync(path, cancellationToken);
        var validation = _documents.Validate(document);
        var mission = ToRecord(document, path, validation);
        _missions.Upsert(mission);

        foreach (var taskDocument in document.Tasks)
        {
            taskDocument.MissionId ??= document.MissionId;
            taskDocument.TeamId ??= document.TeamId;
            taskDocument.ConnectionId ??= document.ConnectionId;
            var taskValidation = _documents.Validate(taskDocument);
            _tasks.Upsert(ToRecord(taskDocument, null, taskValidation));
        }

        return mission;
    }

    public async Task<OperationalTaskRecord> ImportTaskAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var document = await _documents.LoadTaskAsync(path, cancellationToken);
        var validation = _documents.Validate(document);
        var task = ToRecord(document, path, validation);
        _tasks.Upsert(task);
        return task;
    }

    public async Task ExportMissionAsync(
        string missionId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var mission = GetMission(missionId);
        var tasks = _tasks.Items.Where(item => string.Equals(item.MissionId, mission.Id, StringComparison.Ordinal));
        await _documents.SaveMissionAsync(path, mission, tasks, cancellationToken);
        _missions.Upsert(mission with { SourcePath = path });
    }

    public async Task ExportTaskAsync(
        string taskId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var task = GetTask(taskId);
        await _documents.SaveTaskAsync(path, task, cancellationToken);
        _tasks.Upsert(task with { SourcePath = path });
    }

    public async Task<DocumentValidationResult> ValidateMissionAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        var mission = GetMission(missionId);
        var localDocument = new MissionPackageDocument
        {
            MissionId = mission.Id,
            Name = mission.Name,
            Objective = mission.Objective,
            Priority = mission.Priority,
            PolicyId = mission.PolicyId,
            TeamId = mission.AssignedTeamId,
            VehicleId = mission.AssignedVehicleId,
            ConnectionId = mission.ConnectionId,
            GeometryIds = mission.GeometryIds?.ToList() ?? [],
            RequiredCapabilities = mission.RequiredCapabilities?.ToList() ?? [],
            PayloadJson = mission.PayloadJson
        };
        var local = _documents.Validate(localDocument);
        var result = local;

        if (local.IsValid && !string.IsNullOrWhiteSpace(mission.ConnectionId))
        {
            var remote = await _gateway.ValidateMissionAsync(mission.ConnectionId, mission, cancellationToken);
            result = MergeValidation(local, remote);
        }

        _missions.Upsert(mission with
        {
            ValidationState = result.State,
            ValidationSummary = result.Summary
        });
        return result;
    }

    public async Task<DocumentValidationResult> ValidateTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var task = GetTask(taskId);
        var local = _documents.Validate(new TaskPackageDocument
        {
            TaskId = task.Id,
            Name = task.Name,
            MissionId = task.MissionId,
            TeamId = task.TeamId,
            ConnectionId = task.ConnectionId,
            AssignedVehicleId = task.AssignedVehicleId,
            AssignedMemberId = task.AssignedMemberId,
            AssignedLogosInstanceId = task.AssignedLogosInstanceId,
            Objective = task.Objective,
            TaskType = task.TaskType,
            Priority = task.Priority,
            BehaviourId = task.BehaviourId,
            BehaviourVersion = task.BehaviourVersion,
            PackageId = task.PackageId,
            GeometryIds = task.GeometryIds?.ToList() ?? [],
            ParametersJson = task.ParametersJson
        });
        var result = local;

        if (local.IsValid && !string.IsNullOrWhiteSpace(task.ConnectionId))
        {
            var remote = await _gateway.ValidateTaskAsync(task.ConnectionId, task, cancellationToken);
            result = MergeValidation(local, remote);
        }

        _tasks.Upsert(task with
        {
            ValidationState = result.State,
            ValidationSummary = result.Summary
        });
        return result;
    }

    public Task<GatewayCommandResult> PublishMissionAsync(
        string missionId,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default)
        => PublishMissionAsync(
            missionId,
            WorkspaceCommandIdentity.Create($"robot-command-{Guid.NewGuid():N}", "mission.publish"),
            validateOnCreate,
            allowReplaceDraft,
            cancellationToken);

    public async Task<GatewayCommandResult> PublishMissionAsync(
        string missionId,
        WorkspaceCommandIdentity identity,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var mission = GetMission(missionId);
        var connectionId = RequireConnectionId(mission.ConnectionId, "mission", mission.Id);
        var validation = await ValidateMissionAsync(mission.Id, cancellationToken);
        if (!validation.IsValid)
        {
            return await RecordRejectedCommandAsync(
                "Publish",
                "Mission",
                mission.Id,
                connectionId,
                validation.Summary,
                cancellationToken,
                identity);
        }

        mission = GetMission(missionId);
        var commandId = NormalizeCommandIdentity(identity.RequestId, "cmd");
        var correlationId = NormalizeCommandIdentity(identity.CorrelationId, "robot-command");
        var idempotencyKey = NormalizeCommandIdentity(identity.IdempotencyKey, "mission-publish");
        var request = new MissionPublicationRequest(
            connectionId,
            mission,
            validateOnCreate,
            allowReplaceDraft,
            commandId,
            correlationId,
            idempotencyKey);

        return await ExecuteCommandAsync(
            "Publish",
            "Mission",
            mission.Id,
            connectionId,
            commandId,
            correlationId,
            idempotencyKey,
            token => _gateway.CreateMissionAsync(request, token),
            result => ApplyMissionResult(mission.Id, result),
            cancellationToken);
    }

    public Task<GatewayCommandResult> PublishTaskAsync(
        string taskId,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default)
        => PublishTaskAsync(
            taskId,
            WorkspaceCommandIdentity.Create($"robot-command-{Guid.NewGuid():N}", "task.publish"),
            validateOnCreate,
            allowReplaceDraft,
            cancellationToken);

    public async Task<GatewayCommandResult> PublishTaskAsync(
        string taskId,
        WorkspaceCommandIdentity identity,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var task = GetTask(taskId);
        var connectionId = RequireConnectionId(task.ConnectionId, "task", task.Id);
        if (!string.IsNullOrWhiteSpace(task.MissionId) &&
            _missions.TryGet(task.MissionId, out var parentMission) &&
            parentMission is { IsLocalDraft: true })
        {
            return await RecordRejectedCommandAsync(
                "Publish",
                "Task",
                task.Id,
                connectionId,
                $"Publish parent mission '{parentMission.Name}' before publishing this task.",
                cancellationToken,
                identity);
        }

        var validation = await ValidateTaskAsync(task.Id, cancellationToken);
        if (!validation.IsValid)
        {
            return await RecordRejectedCommandAsync(
                "Publish",
                "Task",
                task.Id,
                connectionId,
                validation.Summary,
                cancellationToken,
                identity);
        }

        task = GetTask(taskId);
        var commandId = NormalizeCommandIdentity(identity.RequestId, "cmd");
        var correlationId = NormalizeCommandIdentity(identity.CorrelationId, "robot-command");
        var idempotencyKey = NormalizeCommandIdentity(identity.IdempotencyKey, "task-publish");
        var request = new TaskPublicationRequest(
            connectionId,
            task,
            validateOnCreate,
            allowReplaceDraft,
            commandId,
            correlationId,
            idempotencyKey);

        return await ExecuteCommandAsync(
            "Publish",
            "Task",
            task.Id,
            connectionId,
            commandId,
            correlationId,
            idempotencyKey,
            token => _gateway.CreateTaskAsync(request, token),
            result => ApplyTaskResult(task.Id, result),
            cancellationToken);
    }

    public Task PlanTaskAssignmentAsync(
        string taskId,
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var planned = BuildPlannedAssignment(GetTask(taskId), GetVehicle(vehicleId));
        _tasks.Upsert(planned);
        return Task.CompletedTask;
    }

    public async Task<DocumentValidationResult> ValidateTaskForVehicleAsync(
        string taskId,
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        var planned = BuildPlannedAssignment(GetTask(taskId), GetVehicle(vehicleId));
        _tasks.Upsert(planned);
        var validation = await ValidateTaskAsync(taskId, cancellationToken);
        var current = GetTask(taskId);
        _tasks.Upsert(current with
        {
            AssignmentState = validation.IsValid ? "Validated" : "Blocked"
        });
        return validation;
    }

    public Task<GatewayCommandResult> AssignTaskAsync(
        string taskId,
        string vehicleId,
        bool validateOnAssign = true,
        CancellationToken cancellationToken = default)
        => AssignTaskAsync(
            taskId,
            vehicleId,
            WorkspaceCommandIdentity.Create($"robot-command-{Guid.NewGuid():N}", "task.assign"),
            validateOnAssign,
            cancellationToken);

    public async Task<GatewayCommandResult> AssignTaskAsync(
        string taskId,
        string vehicleId,
        WorkspaceCommandIdentity identity,
        bool validateOnAssign = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var planned = BuildPlannedAssignment(GetTask(taskId), GetVehicle(vehicleId));
        _tasks.Upsert(planned);
        var connectionId = RequireConnectionId(planned.ConnectionId, "task", planned.Id);
        if (planned.IsLocalDraft)
        {
            return await RecordRejectedCommandAsync(
                "Assign",
                "Task",
                planned.Id,
                connectionId,
                "Publish the task to Logos before assigning it.",
                cancellationToken,
                identity);
        }

        if (validateOnAssign)
        {
            var validation = await ValidateTaskForVehicleAsync(taskId, vehicleId, cancellationToken);
            if (!validation.IsValid)
            {
                return await RecordRejectedCommandAsync(
                    "Assign",
                    "Task",
                    planned.Id,
                    connectionId,
                    validation.Summary,
                    cancellationToken,
                    identity);
            }
        }

        planned = GetTask(taskId);
        var commandId = NormalizeCommandIdentity(identity.RequestId, "cmd");
        var correlationId = NormalizeCommandIdentity(identity.CorrelationId, "robot-command");
        var idempotencyKey = NormalizeCommandIdentity(identity.IdempotencyKey, "task-assign");
        var request = new TaskAssignmentRequest(
            connectionId,
            planned,
            validateOnAssign,
            commandId,
            correlationId,
            idempotencyKey);

        return await ExecuteCommandAsync(
            "Assign",
            "Task",
            planned.Id,
            connectionId,
            commandId,
            correlationId,
            idempotencyKey,
            token => _gateway.AssignTaskAsync(request, token),
            result => ApplyAssignmentResult(planned.Id, result),
            cancellationToken);
    }

    public async Task RefreshRemoteAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var missions = await _gateway.ListMissionsAsync(connectionId, cancellationToken);
        foreach (var mission in missions)
        {
            _missions.Upsert(mission);
        }

        var tasks = await _gateway.ListTasksAsync(connectionId, cancellationToken: cancellationToken);
        foreach (var task in tasks)
        {
            _tasks.Upsert(task);
        }
    }

    public Task<GatewayCommandResult> ExecuteMissionCommandAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = request with
        {
            RequestId = NormalizeCommandIdentity(request.RequestId, "cmd"),
            CorrelationId = NormalizeCommandIdentity(request.CorrelationId, "robot-command"),
            IdempotencyKey = NormalizeCommandIdentity(request.IdempotencyKey, "mission")
        };
        return ExecuteCommandAsync(
            prepared.Command,
            "Mission",
            prepared.MissionId,
            prepared.ConnectionId,
            prepared.RequestId!,
            prepared.CorrelationId!,
            prepared.IdempotencyKey!,
            token => _gateway.ExecuteMissionCommandAsync(prepared, token),
            result => ApplyMissionResult(prepared.MissionId, result),
            cancellationToken);
    }

    public Task<GatewayCommandResult> ExecuteTaskCommandAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = request with
        {
            RequestId = NormalizeCommandIdentity(request.RequestId, "cmd"),
            CorrelationId = NormalizeCommandIdentity(request.CorrelationId, "robot-command"),
            IdempotencyKey = NormalizeCommandIdentity(request.IdempotencyKey, "task")
        };
        return ExecuteCommandAsync(
            prepared.Command,
            "Task",
            prepared.TaskId,
            prepared.ConnectionId,
            prepared.RequestId!,
            prepared.CorrelationId!,
            prepared.IdempotencyKey!,
            token => _gateway.ExecuteTaskCommandAsync(prepared, token),
            result => ApplyTaskResult(prepared.TaskId, result),
            cancellationToken);
    }

    private async Task<GatewayCommandResult> ExecuteCommandAsync(
        string kind,
        string targetKind,
        string targetId,
        string? connectionId,
        string commandId,
        string correlationId,
        string idempotencyKey,
        Func<CancellationToken, Task<GatewayCommandResult>> execute,
        Action<GatewayCommandResult> apply,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new OperationalCommandRecord(
            commandId,
            kind,
            targetKind,
            targetId,
            connectionId,
            OperationalCommandState.Submitting,
            $"{kind} {targetKind.ToLowerInvariant()} {targetId}",
            "Submitting to Logos...",
            correlationId,
            now,
            now,
            IdempotencyKey: idempotencyKey);
        _commands.Upsert(record);

        try
        {
            var result = await execute(cancellationToken);
            var updated = record with
            {
                State = result.State,
                Message = result.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _commands.Upsert(updated);
            apply(result);
            return result;
        }
        catch (Exception ex)
        {
            _commands.Upsert(record with
            {
                State = OperationalCommandState.Failed,
                Message = ex.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            throw;
        }
    }


    private static string NormalizeCommandIdentity(string? value, string prefix)
        => string.IsNullOrWhiteSpace(value)
            ? $"{prefix}-{Guid.NewGuid():N}"
            : value.Trim();

    private void ApplyMissionResult(string missionId, GatewayCommandResult result)
    {
        if (!_missions.TryGet(missionId, out var mission) || mission is null)
        {
            return;
        }

        _missions.Upsert(mission with
        {
            State = result.LifecycleState ?? mission.State,
            MissionExecutionId = result.ExecutionId ?? mission.MissionExecutionId,
            Progress = result.Progress ?? mission.Progress,
            ObservedAt = DateTimeOffset.UtcNow,
            IsLocalDraft = result.Accepted ? false : mission.IsLocalDraft
        });
    }

    private void ApplyTaskResult(string taskId, GatewayCommandResult result)
    {
        if (!_tasks.TryGet(taskId, out var task) || task is null)
        {
            return;
        }

        _tasks.Upsert(task with
        {
            State = result.LifecycleState ?? task.State,
            TaskExecutionId = result.ExecutionId ?? task.TaskExecutionId,
            Progress = result.Progress ?? task.Progress,
            ObservedAt = DateTimeOffset.UtcNow,
            IsLocalDraft = result.Accepted ? false : task.IsLocalDraft
        });
    }

    private Task<GatewayCommandResult> RecordRejectedCommandAsync(
        string kind,
        string targetKind,
        string targetId,
        string? connectionId,
        string message,
        CancellationToken cancellationToken,
        WorkspaceCommandIdentity? identity = null)
    {
        var commandId = NormalizeCommandIdentity(identity?.RequestId, "cmd");
        var correlationId = NormalizeCommandIdentity(identity?.CorrelationId, "robot-command");
        var idempotencyKey = NormalizeCommandIdentity(
            identity?.IdempotencyKey,
            $"{targetKind.ToLowerInvariant()}-{kind.ToLowerInvariant()}");
        return ExecuteCommandAsync(
            kind,
            targetKind,
            targetId,
            connectionId,
            commandId,
            correlationId,
            idempotencyKey,
            _ => Task.FromResult(new GatewayCommandResult(
                false,
                OperationalCommandState.Rejected,
                message)),
            _ => { },
            cancellationToken);
    }

    private OperationalTaskRecord BuildPlannedAssignment(
        OperationalTaskRecord task,
        VehicleRecord vehicle)
    {
        var connectionId = task.ConnectionId;
        if (!string.IsNullOrWhiteSpace(connectionId) &&
            !vehicle.ConnectionIds.Contains(connectionId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Task '{task.Name}' is routed through connection '{connectionId}', but vehicle '{vehicle.Name}' is not available on that connection.");
        }

        connectionId ??= vehicle.ConnectionIds.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new InvalidOperationException(
                $"Vehicle '{vehicle.Name}' does not have an available Logos connection.");
        }

        return task with
        {
            AssignedVehicleId = vehicle.Id,
            AssignedLogosInstanceId = vehicle.LogosInstanceId,
            TeamId = task.TeamId ?? vehicle.TeamId,
            ConnectionId = connectionId,
            AssignmentState = "Planned"
        };
    }

    private VehicleRecord GetVehicle(string id)
        => _vehicles.TryGet(id, out var vehicle) && vehicle is not null
            ? vehicle
            : throw new KeyNotFoundException($"Vehicle '{id}' was not found.");

    private static string RequireConnectionId(string? connectionId, string resourceKind, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new InvalidOperationException(
                $"The {resourceKind} '{resourceId}' is not routed to a Logos connection.");
        }

        return connectionId.Trim();
    }

    private void ApplyAssignmentResult(string taskId, GatewayCommandResult result)
    {
        ApplyTaskResult(taskId, result);
        if (!_tasks.TryGet(taskId, out var task) || task is null)
        {
            return;
        }

        _tasks.Upsert(task with
        {
            AssignmentState = result.Accepted ? "Assigned" : task.AssignmentState,
            ObservedAt = DateTimeOffset.UtcNow
        });
    }

    private MissionRecord GetMission(string id)
        => _missions.TryGet(id, out var mission) && mission is not null
            ? mission
            : throw new KeyNotFoundException($"Mission '{id}' was not found.");

    private OperationalTaskRecord GetTask(string id)
        => _tasks.TryGet(id, out var task) && task is not null
            ? task
            : throw new KeyNotFoundException($"Task '{id}' was not found.");

    private static MissionRecord ToRecord(
        MissionPackageDocument document,
        string path,
        DocumentValidationResult validation)
        => new(
            document.MissionId,
            document.Name,
            "Draft",
            document.TeamId,
            document.VehicleId,
            document.ConnectionId,
            document.Objective,
            document.Priority,
            document.PolicyId,
            document.GeometryIds,
            document.RequiredCapabilities,
            document.PayloadJson,
            path,
            validation.State,
            validation.Summary);

    private static OperationalTaskRecord ToRecord(
        TaskPackageDocument document,
        string? path,
        DocumentValidationResult validation)
        => new(
            document.TaskId,
            document.Name,
            "Draft",
            document.MissionId,
            document.AssignedVehicleId,
            document.ConnectionId,
            document.TeamId,
            document.AssignedMemberId,
            document.AssignedLogosInstanceId,
            document.Objective,
            document.TaskType,
            document.Priority,
            document.BehaviourId,
            document.BehaviourVersion,
            document.PackageId,
            document.ParametersJson,
            string.IsNullOrWhiteSpace(document.AssignedVehicleId) ? "Unassigned" : "Planned",
            validation.State,
            validation.Summary,
            SourcePath: path,
            GeometryIds: document.GeometryIds);

    private static DocumentValidationResult MergeValidation(
        DocumentValidationResult local,
        DocumentValidationResult remote)
    {
        if (remote.State == PlanValidationState.Unavailable)
        {
            return new DocumentValidationResult(
                PlanValidationState.Warning,
                $"{local.Summary} Remote validation is unavailable.",
                local.Issues.Concat(remote.Issues).ToArray());
        }

        if (!remote.IsValid)
        {
            return remote;
        }

        return new DocumentValidationResult(
            remote.State == PlanValidationState.Warning ? PlanValidationState.Warning : local.State,
            remote.Summary,
            local.Issues.Concat(remote.Issues).ToArray());
    }
}
