using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Missions;

public sealed class LogosMissionTaskGateway : IMissionTaskGateway
{
    private const int PageSize = 200;
    private const int MaximumPages = 100;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogosCommandMetadataFactory _metadata;
    private readonly ILogger<LogosMissionTaskGateway> _logger;

    public LogosMissionTaskGateway(
        ILogosOperationalSessionRegistry sessions,
        ILogosCommandMetadataFactory metadata,
        ILogger<LogosMissionTaskGateway> logger)
    {
        _sessions = sessions;
        _metadata = metadata;
        _logger = logger;
    }

    public bool IsAvailable => _sessions.ConnectionIds.Any(ConnectionSupportsMissionTasks);

    public string AvailabilityMessage
    {
        get
        {
            if (_sessions.ConnectionIds.Count == 0)
            {
                return "Connect to a Logos runtime to use mission and task operations.";
            }

            var compatible = _sessions.ConnectionIds.Count(ConnectionSupportsMissionTasks);
            return compatible > 0
                ? $"Mission and task operations are available on {compatible} connected Logos runtime(s)."
                : "Connected Logos runtimes do not currently expose both MissionService and TaskService.";
        }
    }

    public async Task<IReadOnlyList<MissionRecord>> ListMissionsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(
            connectionId,
            OperationalApiDomain.Mission,
            cancellationToken);
        var plans = new List<V1.MissionPlan>();
        string pageToken = string.Empty;

        for (var pageIndex = 0; pageIndex < MaximumPages; pageIndex++)
        {
            var response = await session.Clients.Missions.ListMissionsAsync(
                new V1.ListMissionsRequest
                {
                    RequestId = _metadata.CreateRequestId(),
                    CorrelationId = _metadata.CreateCorrelationId(),
                    Page = new V1.PageRequest
                    {
                        PageSize = PageSize,
                        PageToken = pageToken
                    },
                    IncludeCompleted = true,
                    IncludeCancelled = true,
                    IncludeFailed = true
                },
                deadline: ReadDeadline(),
                cancellationToken: cancellationToken);
            EnsureSuccessful(response.Status, "list missions");
            plans.AddRange(response.Missions);

            var next = response.Page?.NextPageToken ?? string.Empty;
            if (string.IsNullOrWhiteSpace(next) || string.Equals(next, pageToken, StringComparison.Ordinal))
            {
                break;
            }

            pageToken = next;
        }

        var records = new List<MissionRecord>(plans.Count);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var details = await TryGetMissionDetailsAsync(session, plan.MissionId, cancellationToken);
            records.Add(MissionTaskProtoMapper.ToModel(
                details.Plan ?? plan,
                connectionId,
                details.Status,
                details.Execution));
        }

        return records
            .OrderByDescending(item => item.ObservedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<OperationalTaskRecord>> ListTasksAsync(
        string connectionId,
        string? missionId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(
            connectionId,
            OperationalApiDomain.Task,
            cancellationToken);
        var plans = new List<V1.TaskPlan>();
        string pageToken = string.Empty;

        for (var pageIndex = 0; pageIndex < MaximumPages; pageIndex++)
        {
            var response = await session.Clients.Tasks.ListTasksAsync(
                new V1.ListTasksRequest
                {
                    RequestId = _metadata.CreateRequestId(),
                    CorrelationId = _metadata.CreateCorrelationId(),
                    Page = new V1.PageRequest
                    {
                        PageSize = PageSize,
                        PageToken = pageToken
                    },
                    MissionId = missionId ?? string.Empty,
                    IncludeCompleted = true,
                    IncludeCancelled = true,
                    IncludeFailed = true
                },
                deadline: ReadDeadline(),
                cancellationToken: cancellationToken);
            EnsureSuccessful(response.Status, "list tasks");
            plans.AddRange(response.Tasks);

            var next = response.Page?.NextPageToken ?? string.Empty;
            if (string.IsNullOrWhiteSpace(next) || string.Equals(next, pageToken, StringComparison.Ordinal))
            {
                break;
            }

            pageToken = next;
        }

        var records = new List<OperationalTaskRecord>(plans.Count);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var details = await TryGetTaskDetailsAsync(session, plan.TaskId, cancellationToken);
            records.Add(MissionTaskProtoMapper.ToModel(
                details.Plan ?? plan,
                connectionId,
                details.Status,
                details.Execution));
        }

        return records
            .OrderByDescending(item => item.ObservedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<DocumentValidationResult> ValidateMissionAsync(
        string connectionId,
        MissionRecord mission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mission);
        try
        {
            var session = await RequireSessionAsync(
                connectionId,
                OperationalApiDomain.Mission,
                cancellationToken);
            var response = await session.Clients.Missions.ValidateMissionAsync(
                new V1.ValidateMissionRequest
                {
                    Command = _metadata.Create("mission.validate", mission.Id),
                    Mission = MissionTaskProtoMapper.ToProto(mission)
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);
            return MissionTaskProtoMapper.ToValidationResult(
                response.Validation,
                response.Status,
                response.Authorization,
                $"Mission '{mission.Name}' is valid in Logos.");
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return UnavailableValidation(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unimplemented or StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return UnavailableValidation(RpcMessage("Mission validation", ex));
        }
    }

    public async Task<DocumentValidationResult> ValidateTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            var session = await RequireSessionAsync(
                connectionId,
                OperationalApiDomain.Task,
                cancellationToken);
            var response = await session.Clients.Tasks.ValidateTaskAsync(
                new V1.ValidateTaskRequest
                {
                    Command = _metadata.Create("task.validate", task.Id),
                    Task = MissionTaskProtoMapper.ToProto(task),
                    CandidateVehicleId = task.AssignedVehicleId ?? string.Empty,
                    CandidateMemberId = task.AssignedMemberId ?? string.Empty,
                    CandidateLogosInstanceId = task.AssignedLogosInstanceId ?? string.Empty
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);
            return MissionTaskProtoMapper.ToValidationResult(
                response.Validation,
                response.Status,
                response.Authorization,
                $"Task '{task.Name}' is valid in Logos.");
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return UnavailableValidation(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unimplemented or StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return UnavailableValidation(RpcMessage("Task validation", ex));
        }
    }

    public Task<GatewayCommandResult> CreateMissionAsync(
        string connectionId,
        MissionRecord mission,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default)
        => CreateMissionAsync(
            new MissionPublicationRequest(connectionId, mission, validateOnCreate, allowReplaceDraft),
            cancellationToken);

    public async Task<GatewayCommandResult> CreateMissionAsync(
        MissionPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Mission);
        var mission = request.Mission;
        try
        {
            var session = await RequireSessionAsync(
                request.ConnectionId,
                OperationalApiDomain.Mission,
                cancellationToken);
            var response = await session.Clients.Missions.CreateMissionAsync(
                new V1.CreateMissionRequest
                {
                    Command = _metadata.Create("mission.create", mission.Id, request.RequestId, request.CorrelationId, request.IdempotencyKey),
                    Mission = MissionTaskProtoMapper.ToProto(mission),
                    ValidateOnCreate = request.ValidateOnCreate,
                    AllowReplaceDraft = request.AllowReplaceDraft
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);

            var validation = request.ValidateOnCreate
                ? MissionTaskProtoMapper.ToValidationResult(
                    response.Validation,
                    response.Status,
                    response.Authorization,
                    $"Mission '{mission.Name}' was created and validated.")
                : null;
            if (validation is { IsValid: false })
            {
                return new GatewayCommandResult(
                    false,
                    OperationalCommandState.Rejected,
                    validation.Summary,
                    LifecycleState: "Draft");
            }

            return MissionTaskProtoMapper.ToCommandResult(
                null,
                response.Status,
                response.Authorization,
                $"Mission '{mission.Name}' was created in Logos.",
                lifecycleState: "Draft");
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return FailedCommand("Create mission", ex);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            return FailedCommand("Create mission", ex);
        }
    }

    public Task<GatewayCommandResult> CreateTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default)
        => CreateTaskAsync(
            new TaskPublicationRequest(connectionId, task, validateOnCreate, allowReplaceDraft),
            cancellationToken);

    public async Task<GatewayCommandResult> CreateTaskAsync(
        TaskPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Task);
        var task = request.Task;
        try
        {
            var session = await RequireSessionAsync(
                request.ConnectionId,
                OperationalApiDomain.Task,
                cancellationToken);
            var response = await session.Clients.Tasks.CreateTaskAsync(
                new V1.CreateTaskRequest
                {
                    Command = _metadata.Create("task.create", task.Id, request.RequestId, request.CorrelationId, request.IdempotencyKey),
                    Task = MissionTaskProtoMapper.ToProto(task),
                    ValidateOnCreate = request.ValidateOnCreate,
                    AllowReplaceDraft = request.AllowReplaceDraft
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);

            var validation = request.ValidateOnCreate
                ? MissionTaskProtoMapper.ToValidationResult(
                    response.Validation,
                    response.Status,
                    response.Authorization,
                    $"Task '{task.Name}' was created and validated.")
                : null;
            if (validation is { IsValid: false })
            {
                return new GatewayCommandResult(
                    false,
                    OperationalCommandState.Rejected,
                    validation.Summary,
                    LifecycleState: "Draft");
            }

            return MissionTaskProtoMapper.ToCommandResult(
                null,
                response.Status,
                response.Authorization,
                $"Task '{task.Name}' was created in Logos.",
                lifecycleState: "Draft");
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return FailedCommand("Create task", ex);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            return FailedCommand("Create task", ex);
        }
    }

    public Task<GatewayCommandResult> AssignTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        bool validateOnAssign = true,
        CancellationToken cancellationToken = default)
        => AssignTaskAsync(new TaskAssignmentRequest(connectionId, task, validateOnAssign), cancellationToken);

    public async Task<GatewayCommandResult> AssignTaskAsync(
        TaskAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Task);
        var task = request.Task;
        if (string.IsNullOrWhiteSpace(task.AssignedVehicleId) &&
            string.IsNullOrWhiteSpace(task.AssignedMemberId) &&
            string.IsNullOrWhiteSpace(task.AssignedLogosInstanceId))
        {
            return new GatewayCommandResult(
                false,
                OperationalCommandState.Rejected,
                "Assign the task to a vehicle, member, or Logos instance before submitting it.");
        }

        try
        {
            var session = await RequireSessionAsync(
                request.ConnectionId,
                OperationalApiDomain.Task,
                cancellationToken);
            var response = await session.Clients.Tasks.AssignTaskAsync(
                new V1.AssignTaskRequest
                {
                    Command = _metadata.Create("task.assign", task.Id, request.RequestId, request.CorrelationId, request.IdempotencyKey),
                    TaskId = task.Id,
                    Task = MissionTaskProtoMapper.ToProto(task),
                    AssignedMemberId = task.AssignedMemberId ?? string.Empty,
                    AssignedVehicleId = task.AssignedVehicleId ?? string.Empty,
                    AssignedLogosInstanceId = task.AssignedLogosInstanceId ?? string.Empty,
                    ValidateOnAssign = request.ValidateOnAssign
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);

            var validation = request.ValidateOnAssign
                ? MissionTaskProtoMapper.ToValidationResult(
                    response.Validation,
                    response.Result?.Status,
                    response.Authorization,
                    $"Task '{task.Name}' passed assignment validation.")
                : null;
            if (validation is { IsValid: false })
            {
                return new GatewayCommandResult(
                    false,
                    OperationalCommandState.Rejected,
                    validation.Summary,
                    response.TaskStatus?.TaskExecutionId,
                    response.TaskStatus?.State.ToString(),
                    response.TaskStatus?.Progress?.Progress);
            }

            return MissionTaskProtoMapper.ToCommandResult(
                response.Result,
                response.Result?.Status,
                response.Authorization,
                $"Task '{task.Name}' was assigned.",
                response.TaskStatus?.TaskExecutionId,
                response.TaskStatus?.State.ToString() ?? response.Assignment?.State.ToString(),
                response.TaskStatus?.Progress?.Progress);
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return FailedCommand("Assign task", ex);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            return FailedCommand("Assign task", ex);
        }
    }

    public async Task<PreparedOperationGatewayResult> PrepareMissionOperationAsync(
        MissionOperationPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(
                request.ConnectionId,
                OperationalApiDomain.Mission,
                cancellationToken);
            var apiRequest = new V1.PrepareMissionOperationRequest
            {
                Command = _metadata.Create(
                    $"mission.{CommandName(request.Kind)}.prepare",
                    request.MissionId,
                    request.Identity.RequestId,
                    request.Identity.CorrelationId,
                    request.Identity.IdempotencyKey),
                Kind = ToMissionOperationKind(request.Kind),
                MissionId = request.MissionId,
                MissionExecutionId = request.MissionExecutionId ?? string.Empty,
                Reason = request.Reason ?? string.Empty,
                Emergency = request.Emergency,
                ExpectedControlStateVersion = request.ExpectedControlStateVersion
            };
            AddPolicyContext(apiRequest.PolicyContext, request.PolicyContext);
            var response = await session.Clients.Missions.PrepareMissionOperationAsync(
                apiRequest,
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);
            return MapPreparedOperation(response.Status, response.Preparation, "Mission intervention preparation was rejected.");
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return PreparedOperationGatewayResult.Rejected(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            return PreparedOperationGatewayResult.Rejected(RpcMessage("Prepare mission intervention", ex));
        }
    }

    public async Task<PreparedOperationGatewayResult> PrepareTaskOperationAsync(
        TaskOperationPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(
                request.ConnectionId,
                OperationalApiDomain.Task,
                cancellationToken);
            var apiRequest = new V1.PrepareTaskOperationRequest
            {
                Command = _metadata.Create(
                    $"task.{CommandName(request.Kind)}.prepare",
                    request.TaskId,
                    request.Identity.RequestId,
                    request.Identity.CorrelationId,
                    request.Identity.IdempotencyKey),
                Kind = ToTaskOperationKind(request.Kind),
                TaskId = request.TaskId,
                TaskExecutionId = request.TaskExecutionId ?? string.Empty,
                Reason = request.Reason ?? string.Empty,
                Emergency = request.Emergency,
                ExpectedControlStateVersion = request.ExpectedControlStateVersion
            };
            AddPolicyContext(apiRequest.PolicyContext, request.PolicyContext);
            var response = await session.Clients.Tasks.PrepareTaskOperationAsync(
                apiRequest,
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);
            return MapPreparedOperation(response.Status, response.Preparation, "Task intervention preparation was rejected.");
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return PreparedOperationGatewayResult.Rejected(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            return PreparedOperationGatewayResult.Rejected(RpcMessage("Prepare task intervention", ex));
        }
    }

    public async Task<GatewayCommandResult> ExecuteMissionCommandAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(
                request.ConnectionId,
                OperationalApiDomain.Mission,
                cancellationToken);
            var command = NormalizeCommand(request.Command);
            return command switch
            {
                "start" => await StartMissionAsync(session, request, cancellationToken),
                "pause" => await PauseMissionAsync(session, request, cancellationToken),
                "resume" => await ResumeMissionAsync(session, request, cancellationToken),
                "end" or "complete" => await EndMissionAsync(session, request, cancellationToken),
                "cancel" or "stop" => await CancelMissionAsync(session, request, cancellationToken),
                "abort" => await AbortMissionAsync(session, request, cancellationToken),
                _ => MissionTaskProtoMapper.UnsupportedCommand("mission", request.Command)
            };
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return FailedCommand($"Mission {request.Command}", ex);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            return FailedCommand($"Mission {request.Command}", ex);
        }
    }

    public async Task<GatewayCommandResult> ExecuteTaskCommandAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(
                request.ConnectionId,
                OperationalApiDomain.Task,
                cancellationToken);
            var command = NormalizeCommand(request.Command);
            return command switch
            {
                "assign" => await AssignExistingTaskAsync(session, request, cancellationToken),
                "start" => await StartTaskAsync(session, request, cancellationToken),
                "cancel" or "stop" => await CancelTaskAsync(session, request, cancellationToken),
                "abort" => await AbortTaskAsync(session, request, cancellationToken),
                _ => MissionTaskProtoMapper.UnsupportedCommand("task", request.Command)
            };
        }
        catch (MissionTaskGatewayUnavailableException ex)
        {
            return FailedCommand($"Task {request.Command}", ex);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            return FailedCommand($"Task {request.Command}", ex);
        }
    }

    private async Task<GatewayCommandResult> StartMissionAsync(
        ILogosOperationalSession session,
        MissionCommandRequest request,
        CancellationToken cancellationToken)
    {
        var response = await session.Clients.Missions.StartMissionAsync(
            new V1.StartMissionRequest
            {
                Command = CommandMetadata("mission.start", request.MissionId, request),
                MissionId = request.MissionId,
                RejectOnWarnings = false
            },
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return MissionTaskProtoMapper.ToCommandResult(
            response.Result,
            response.Result?.Status,
            response.Authorization,
            $"Mission '{request.MissionId}' start was accepted.",
            response.Execution?.MissionExecutionId,
            response.MissionStatus?.State.ToString() ?? response.Execution?.State.ToString(),
            response.MissionStatus?.Progress);
    }

    private async Task<GatewayCommandResult> PauseMissionAsync(
        ILogosOperationalSession session,
        MissionCommandRequest request,
        CancellationToken cancellationToken)
    {
        var apiRequest = new V1.PauseMissionRequest
        {
            Command = CommandMetadata("mission.pause", request.MissionId, request),
            MissionId = request.MissionId,
            MissionExecutionId = request.MissionExecutionId ?? string.Empty,
            Reason = request.Reason ?? string.Empty
        };
        AttachPreparation(apiRequest, request.Preparation);
        var response = await session.Clients.Missions.PauseMissionAsync(
            apiRequest,
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return FromMissionStatus(response.Result, response.Authorization, response.MissionStatus, "Mission pause was accepted.");
    }

    private async Task<GatewayCommandResult> ResumeMissionAsync(
        ILogosOperationalSession session,
        MissionCommandRequest request,
        CancellationToken cancellationToken)
    {
        var apiRequest = new V1.ResumeMissionRequest
        {
            Command = CommandMetadata("mission.resume", request.MissionId, request),
            MissionId = request.MissionId,
            MissionExecutionId = request.MissionExecutionId ?? string.Empty,
            RequireRecheck = true
        };
        AttachPreparation(apiRequest, request.Preparation);
        var response = await session.Clients.Missions.ResumeMissionAsync(
            apiRequest,
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return FromMissionStatus(response.Result, response.Authorization, response.MissionStatus, "Mission resume was accepted.");
    }

    private async Task<GatewayCommandResult> EndMissionAsync(
        ILogosOperationalSession session,
        MissionCommandRequest request,
        CancellationToken cancellationToken)
    {
        var apiRequest = new V1.EndMissionRequest
        {
            Command = CommandMetadata("mission.end", request.MissionId, request),
            MissionId = request.MissionId,
            MissionExecutionId = request.MissionExecutionId ?? string.Empty,
            Reason = request.Reason ?? string.Empty
        };
        AttachPreparation(apiRequest, request.Preparation);
        var response = await session.Clients.Missions.EndMissionAsync(
            apiRequest,
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return FromMissionStatus(response.Result, response.Authorization, response.MissionStatus, "Mission end was accepted.");
    }

    private async Task<GatewayCommandResult> CancelMissionAsync(
        ILogosOperationalSession session,
        MissionCommandRequest request,
        CancellationToken cancellationToken)
    {
        var apiRequest = new V1.CancelMissionRequest
        {
            Command = CommandMetadata("mission.cancel", request.MissionId, request),
            MissionId = request.MissionId,
            MissionExecutionId = request.MissionExecutionId ?? string.Empty,
            Reason = request.Reason ?? string.Empty
        };
        AttachPreparation(apiRequest, request.Preparation);
        var response = await session.Clients.Missions.CancelMissionAsync(
            apiRequest,
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return FromMissionStatus(response.Result, response.Authorization, response.MissionStatus, "Mission cancellation was accepted.");
    }

    private async Task<GatewayCommandResult> AbortMissionAsync(
        ILogosOperationalSession session,
        MissionCommandRequest request,
        CancellationToken cancellationToken)
    {
        var apiRequest = new V1.AbortMissionRequest
        {
            Command = CommandMetadata("mission.abort", request.MissionId, request),
            MissionId = request.MissionId,
            MissionExecutionId = request.MissionExecutionId ?? string.Empty,
            Reason = request.Reason ?? string.Empty,
            Emergency = request.Emergency
        };
        AttachPreparation(apiRequest, request.Preparation);
        var response = await session.Clients.Missions.AbortMissionAsync(
            apiRequest,
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return FromMissionStatus(response.Result, response.Authorization, response.MissionStatus, "Mission abort was accepted.");
    }

    private async Task<GatewayCommandResult> AssignExistingTaskAsync(
        ILogosOperationalSession session,
        TaskCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AssignedVehicleId) &&
            string.IsNullOrWhiteSpace(request.AssignedMemberId) &&
            string.IsNullOrWhiteSpace(request.AssignedLogosInstanceId))
        {
            return new GatewayCommandResult(
                false,
                OperationalCommandState.Rejected,
                "Assign the task to a vehicle, member, or Logos instance before submitting it.");
        }

        var task = await session.Clients.Tasks.GetTaskAsync(
            new V1.GetTaskRequest
            {
                RequestId = _metadata.CreateRequestId(),
                CorrelationId = _metadata.CreateCorrelationId(request.CorrelationId),
                TaskId = request.TaskId,
                IncludePlan = true,
                IncludeStatus = false
            },
            deadline: ReadDeadline(),
            cancellationToken: cancellationToken);
        EnsureSuccessful(task.Status, $"get task '{request.TaskId}' for assignment");
        if (task.Task is null || string.IsNullOrWhiteSpace(task.Task.TaskId))
        {
            return new GatewayCommandResult(
                false,
                OperationalCommandState.Rejected,
                $"Logos did not return task plan '{request.TaskId}' for assignment.");
        }

        var response = await session.Clients.Tasks.AssignTaskAsync(
            new V1.AssignTaskRequest
            {
                Command = CommandMetadata("task.assign", request.TaskId, request),
                TaskId = request.TaskId,
                Task = task.Task,
                AssignedMemberId = request.AssignedMemberId ?? string.Empty,
                AssignedVehicleId = request.AssignedVehicleId ?? string.Empty,
                AssignedLogosInstanceId = request.AssignedLogosInstanceId ?? string.Empty,
                ValidateOnAssign = true
            },
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return MissionTaskProtoMapper.ToCommandResult(
            response.Result,
            response.Result?.Status,
            response.Authorization,
            "Task assignment was accepted.",
            response.TaskStatus?.TaskExecutionId,
            response.TaskStatus?.State.ToString() ?? response.Assignment?.State.ToString(),
            response.TaskStatus?.Progress?.Progress);
    }

    private async Task<GatewayCommandResult> StartTaskAsync(
        ILogosOperationalSession session,
        TaskCommandRequest request,
        CancellationToken cancellationToken)
    {
        var response = await session.Clients.Tasks.StartTaskAsync(
            new V1.StartTaskRequest
            {
                Command = CommandMetadata("task.start", request.TaskId, request),
                TaskId = request.TaskId,
                TaskExecutionId = request.TaskExecutionId ?? string.Empty,
                RequireRecheck = true
            },
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return MissionTaskProtoMapper.ToCommandResult(
            response.Result,
            response.Result?.Status,
            response.Authorization,
            "Task start was accepted.",
            response.Execution?.TaskExecutionId ?? response.TaskStatus?.TaskExecutionId,
            response.TaskStatus?.State.ToString() ?? response.Execution?.State.ToString(),
            response.TaskStatus?.Progress?.Progress);
    }

    private async Task<GatewayCommandResult> CancelTaskAsync(
        ILogosOperationalSession session,
        TaskCommandRequest request,
        CancellationToken cancellationToken)
    {
        var apiRequest = new V1.CancelTaskRequest
        {
            Command = CommandMetadata("task.cancel", request.TaskId, request),
            TaskId = request.TaskId,
            TaskExecutionId = request.TaskExecutionId ?? string.Empty,
            Reason = request.Reason ?? string.Empty
        };
        AttachPreparation(apiRequest, request.Preparation);
        var response = await session.Clients.Tasks.CancelTaskAsync(
            apiRequest,
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return FromTaskStatus(response.Result, response.Authorization, response.TaskStatus, "Task cancellation was accepted.");
    }

    private async Task<GatewayCommandResult> AbortTaskAsync(
        ILogosOperationalSession session,
        TaskCommandRequest request,
        CancellationToken cancellationToken)
    {
        var apiRequest = new V1.AbortTaskRequest
        {
            Command = CommandMetadata("task.abort", request.TaskId, request),
            TaskId = request.TaskId,
            TaskExecutionId = request.TaskExecutionId ?? string.Empty,
            Reason = request.Reason ?? string.Empty,
            Emergency = request.Emergency
        };
        AttachPreparation(apiRequest, request.Preparation);
        var response = await session.Clients.Tasks.AbortTaskAsync(
            apiRequest,
            deadline: CommandDeadline(),
            cancellationToken: cancellationToken);
        return FromTaskStatus(response.Result, response.Authorization, response.TaskStatus, "Task abort was accepted.");
    }

    private GatewayCommandResult FromMissionStatus(
        V1.CommandResult? result,
        V1.AuthorizationDecision? authorization,
        V1.MissionStatus? status,
        string message)
        => MissionTaskProtoMapper.ToCommandResult(
            result,
            result?.Status,
            authorization,
            message,
            status?.MissionExecutionId,
            status?.State.ToString(),
            status?.Progress);

    private GatewayCommandResult FromTaskStatus(
        V1.CommandResult? result,
        V1.AuthorizationDecision? authorization,
        V1.TaskStatus? status,
        string message)
        => MissionTaskProtoMapper.ToCommandResult(
            result,
            result?.Status,
            authorization,
            message,
            status?.TaskExecutionId,
            status?.State.ToString(),
            status?.Progress?.Progress);

    private static PreparedOperationGatewayResult MapPreparedOperation(
        V1.DomainStatus? status,
        V1.PreparedOperation? preparation,
        string fallbackMessage)
    {
        if (status is not { Ok: true } ||
            preparation is null ||
            string.IsNullOrWhiteSpace(preparation.PreparationId) ||
            string.IsNullOrWhiteSpace(preparation.ConfirmationToken))
        {
            var rejectedMessage = status is null || string.IsNullOrWhiteSpace(status.Message)
                ? fallbackMessage
                : status.Message;
            return PreparedOperationGatewayResult.Rejected(rejectedMessage);
        }

        var authorizationAllowed = preparation.Authorization?.Allowed == true;
        var readiness = preparation.Readiness?.Readiness.ToString() ?? "Unknown";
        var readinessAllows = !string.Equals(readiness, "NotReady", StringComparison.OrdinalIgnoreCase);
        var warnings = preparation.Warnings
            .Select(IssueText)
            .Concat(preparation.Readiness is null
                ? Enumerable.Empty<string>()
                : preparation.Readiness.Issues.Select(IssueText))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var authorizationDecision = preparation.Authorization is null
            ? "Unavailable"
            : authorizationAllowed
                ? "Allowed"
                : string.IsNullOrWhiteSpace(preparation.Authorization.DeniedReasons)
                    ? "Denied"
                    : preparation.Authorization.DeniedReasons;
        var message = new[]
        {
            status.Message,
            preparation.Authorization?.Status?.Message,
            preparation.Readiness?.Message
        }.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
          ?? (authorizationAllowed && readinessAllows
              ? "Logos prepared the intervention."
              : fallbackMessage);
        var preparedAt = ToDateTimeOffset(preparation.PreparedAt) ?? DateTimeOffset.UtcNow;
        var expiresAt = ToDateTimeOffset(preparation.ExpiresAt) ?? preparedAt;
        var target = preparation.Target is null
            ? null
            : new PreparedOperationTargetSnapshot(
                EmptyToNull(preparation.Target.LogosInstanceId),
                EmptyToNull(preparation.Target.VehicleId),
                EmptyToNull(preparation.Target.VehicleBindingGeneration),
                EmptyToNull(preparation.Target.MissionId),
                EmptyToNull(preparation.Target.MissionExecutionId),
                EmptyToNull(preparation.Target.TaskId),
                EmptyToNull(preparation.Target.TaskExecutionId),
                preparation.Target.ControlStateVersion);

        return new PreparedOperationGatewayResult(
            authorizationAllowed && readinessAllows && expiresAt > DateTimeOffset.UtcNow,
            message,
            new PreparedOperationReference(preparation.PreparationId, preparation.ConfirmationToken),
            target,
            preparation.OperationType,
            preparation.Destructive,
            authorizationAllowed,
            authorizationDecision,
            readiness,
            warnings,
            preparedAt,
            expiresAt);
    }

    private static V1.MissionOperationKind ToMissionOperationKind(OperationalInterventionKind kind)
        => kind switch
        {
            OperationalInterventionKind.PauseMission => V1.MissionOperationKind.Pause,
            OperationalInterventionKind.ResumeMission => V1.MissionOperationKind.Resume,
            OperationalInterventionKind.EndMission => V1.MissionOperationKind.End,
            OperationalInterventionKind.CancelMission => V1.MissionOperationKind.Cancel,
            OperationalInterventionKind.AbortMission => V1.MissionOperationKind.Abort,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a mission intervention.")
        };

    private static V1.TaskOperationKind ToTaskOperationKind(OperationalInterventionKind kind)
        => kind switch
        {
            OperationalInterventionKind.CancelTask => V1.TaskOperationKind.Cancel,
            OperationalInterventionKind.AbortTask => V1.TaskOperationKind.Abort,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a task intervention.")
        };

    private static string CommandName(OperationalInterventionKind kind)
        => kind switch
        {
            OperationalInterventionKind.PauseMission => "pause",
            OperationalInterventionKind.ResumeMission => "resume",
            OperationalInterventionKind.EndMission => "end",
            OperationalInterventionKind.CancelMission => "cancel",
            OperationalInterventionKind.AbortMission => "abort",
            OperationalInterventionKind.CancelTask => "cancel",
            OperationalInterventionKind.AbortTask => "abort",
            _ => "intervene"
        };

    private static void AddPolicyContext(
        IDictionary<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source is null) return;
        foreach (var pair in source)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                destination[pair.Key] = pair.Value ?? string.Empty;
            }
        }
    }

    private static V1.PreparedOperationRef ToApi(PreparedOperationReference preparation)
        => new()
        {
            PreparationId = preparation.PreparationId,
            ConfirmationToken = preparation.ConfirmationToken
        };

    private static void AttachPreparation(V1.PauseMissionRequest request, PreparedOperationReference? preparation)
    {
        if (preparation is not null) request.Preparation = ToApi(preparation);
    }

    private static void AttachPreparation(V1.ResumeMissionRequest request, PreparedOperationReference? preparation)
    {
        if (preparation is not null) request.Preparation = ToApi(preparation);
    }

    private static void AttachPreparation(V1.EndMissionRequest request, PreparedOperationReference? preparation)
    {
        if (preparation is not null) request.Preparation = ToApi(preparation);
    }

    private static void AttachPreparation(V1.CancelMissionRequest request, PreparedOperationReference? preparation)
    {
        if (preparation is not null) request.Preparation = ToApi(preparation);
    }

    private static void AttachPreparation(V1.AbortMissionRequest request, PreparedOperationReference? preparation)
    {
        if (preparation is not null) request.Preparation = ToApi(preparation);
    }

    private static void AttachPreparation(V1.CancelTaskRequest request, PreparedOperationReference? preparation)
    {
        if (preparation is not null) request.Preparation = ToApi(preparation);
    }

    private static void AttachPreparation(V1.AbortTaskRequest request, PreparedOperationReference? preparation)
    {
        if (preparation is not null) request.Preparation = ToApi(preparation);
    }

    private static string IssueText(V1.Issue issue)
        => string.IsNullOrWhiteSpace(issue.Code) ? issue.Message : $"{issue.Code}: {issue.Message}";

    private static DateTimeOffset? ToDateTimeOffset(Timestamp? timestamp)
    {
        if (timestamp is null || timestamp.Seconds == 0 && timestamp.Nanos == 0) return null;
        return new DateTimeOffset(timestamp.ToDateTime(), TimeSpan.Zero);
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private V1.CommandRequestMetadata CommandMetadata(
        string operation,
        string targetId,
        MissionCommandRequest request)
        => _metadata.Create(
            operation,
            targetId,
            request.RequestId,
            request.CorrelationId,
            request.IdempotencyKey);

    private V1.CommandRequestMetadata CommandMetadata(
        string operation,
        string targetId,
        TaskCommandRequest request)
        => _metadata.Create(
            operation,
            targetId,
            request.RequestId,
            request.CorrelationId,
            request.IdempotencyKey);

    private async Task<ILogosOperationalSession> RequireSessionAsync(
        string connectionId,
        OperationalApiDomain domain,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new MissionTaskGatewayUnavailableException("A Logos connection is required.");
        }

        if (!_sessions.TryGet(connectionId, out var session) || session is null)
        {
            throw new MissionTaskGatewayUnavailableException(
                $"Connection '{connectionId}' has no active Logos operational session.");
        }

        var snapshot = session.Status;
        if (snapshot.InspectedAt == DateTimeOffset.MinValue ||
            snapshot.Get(domain).Availability is OperationalApiAvailability.Unknown or OperationalApiAvailability.Inspecting)
        {
            snapshot = await session.InspectAsync(cancellationToken: cancellationToken);
        }

        var status = snapshot.Get(domain);
        if (!status.Available)
        {
            throw new MissionTaskGatewayUnavailableException(
                $"{domain} API is not available on connection '{connectionId}': {status.Detail}");
        }

        return session;
    }

    private bool ConnectionSupportsMissionTasks(string connectionId)
    {
        if (!_sessions.TryGet(connectionId, out var session) || session is null)
        {
            return false;
        }

        return session.Status.IsAvailable(OperationalApiDomain.Mission) &&
               session.Status.IsAvailable(OperationalApiDomain.Task);
    }

    private async Task<MissionDetails> TryGetMissionDetailsAsync(
        ILogosOperationalSession session,
        string missionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await session.Clients.Missions.GetMissionAsync(
                new V1.GetMissionRequest
                {
                    RequestId = _metadata.CreateRequestId(),
                    CorrelationId = _metadata.CreateCorrelationId(),
                    MissionId = missionId,
                    IncludePlan = true,
                    IncludeStatus = true,
                    IncludeTasks = false
                },
                deadline: ReadDeadline(),
                cancellationToken: cancellationToken);
            if (response.Status is { Ok: true })
            {
                return new MissionDetails(response.Mission, response.MissionStatus, response.Execution);
            }
        }
        catch (RpcException ex) when (ex.StatusCode is not StatusCode.Cancelled)
        {
            _logger.LogDebug(ex, "Could not hydrate mission {MissionId}", missionId);
        }

        return default;
    }

    private async Task<TaskDetails> TryGetTaskDetailsAsync(
        ILogosOperationalSession session,
        string taskId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await session.Clients.Tasks.GetTaskAsync(
                new V1.GetTaskRequest
                {
                    RequestId = _metadata.CreateRequestId(),
                    CorrelationId = _metadata.CreateCorrelationId(),
                    TaskId = taskId,
                    IncludePlan = true,
                    IncludeStatus = true
                },
                deadline: ReadDeadline(),
                cancellationToken: cancellationToken);
            if (response.Status is { Ok: true })
            {
                return new TaskDetails(response.Task, response.TaskStatus, response.Execution);
            }
        }
        catch (RpcException ex) when (ex.StatusCode is not StatusCode.Cancelled)
        {
            _logger.LogDebug(ex, "Could not hydrate task {TaskId}", taskId);
        }

        return default;
    }

    private static void EnsureSuccessful(V1.DomainStatus? status, string operation)
    {
        if (status is { Ok: true })
        {
            return;
        }

        var message = status is null
            ? "Logos returned no domain status."
            : string.IsNullOrWhiteSpace(status.Message)
                ? status.Code.ToString()
                : $"{status.Code}: {status.Message}";
        throw new InvalidOperationException($"Could not {operation}. {message}");
    }

    private static DocumentValidationResult UnavailableValidation(string message)
        => new(
            PlanValidationState.Unavailable,
            message,
            [message]);

    private static GatewayCommandResult FailedCommand(string operation, Exception exception)
    {
        var message = exception is RpcException rpc
            ? RpcMessage(operation, rpc)
            : exception.Message;
        var unavailable = exception is MissionTaskGatewayUnavailableException ||
                          exception is RpcException
                          {
                              StatusCode: StatusCode.Unimplemented or StatusCode.Unavailable or StatusCode.DeadlineExceeded
                          };
        return new GatewayCommandResult(
            false,
            unavailable ? OperationalCommandState.Rejected : OperationalCommandState.Failed,
            message);
    }

    private static string RpcMessage(string operation, RpcException exception)
        => string.IsNullOrWhiteSpace(exception.Status.Detail)
            ? $"{operation} failed: {exception.StatusCode}."
            : $"{operation} failed: {exception.Status.Detail}";

    private static string NormalizeCommand(string command)
        => command?.Trim().ToLowerInvariant() ?? string.Empty;

    private static DateTime ReadDeadline() => DateTime.UtcNow.Add(ReadTimeout);

    private static DateTime CommandDeadline() => DateTime.UtcNow.Add(CommandTimeout);

    private readonly record struct MissionDetails(
        V1.MissionPlan? Plan,
        V1.MissionStatus? Status,
        V1.MissionExecution? Execution);

    private readonly record struct TaskDetails(
        V1.TaskPlan? Plan,
        V1.TaskStatus? Status,
        V1.TaskExecution? Execution);

    private sealed class MissionTaskGatewayUnavailableException(string message)
        : InvalidOperationException(message);
}
