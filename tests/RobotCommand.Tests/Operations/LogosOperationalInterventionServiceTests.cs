using RobotCommand.Models;
using RobotCommand.Services.Missions;
using RobotCommand.Services.Operations;
using Xunit;

namespace RobotCommand.Tests.Operations;

public sealed class LogosOperationalInterventionServiceTests
{
    [Fact]
    public async Task Prepare_CapturesAuthoritativeTargetAndServerToken()
    {
        var fixture = new Fixture();

        var preparation = await fixture.Service.PrepareAsync(
            OperationalInterventionKind.PauseMission,
            "Pause for inspection");

        Assert.True(preparation.CanExecute);
        Assert.Equal("mission-1", preparation.Target.MissionId);
        Assert.Equal("prepare-1", preparation.Reference.PreparationId);
        Assert.Equal("token-1", preparation.Reference.ConfirmationToken);
        Assert.Equal("Pause for inspection", fixture.Gateway.LastMissionPreparation!.Reason);
    }

    [Fact]
    public async Task Execute_RequiresTypedConfirmationForAbort()
    {
        var fixture = new Fixture();
        var preparation = await fixture.Service.PrepareAsync(
            OperationalInterventionKind.AbortMission,
            "Vehicle is unsafe");

        var result = await fixture.Service.ExecuteAsync(preparation.OperationId, "wrong");

        Assert.False(result.Accepted);
        Assert.Null(fixture.Workspace.LastMissionCommand);
        Assert.Contains(preparation.ConfirmationPhrase, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_RevalidatesExecutionIdentityBeforeSubmitting()
    {
        var fixture = new Fixture();
        var preparation = await fixture.Service.PrepareAsync(
            OperationalInterventionKind.CancelTask,
            "Stop the task");
        fixture.Supervision.Snapshot = fixture.Supervision.Snapshot with
        {
            Target = fixture.Supervision.Snapshot.Target! with { TaskId = "task-2" }
        };

        var result = await fixture.Service.ExecuteAsync(
            preparation.OperationId,
            preparation.ConfirmationPhrase);

        Assert.False(result.Accepted);
        Assert.Null(fixture.Workspace.LastTaskCommand);
        Assert.Contains("changed after preparation", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_PropagatesPreparedReferenceAndCorrelationIdentity()
    {
        var fixture = new Fixture();
        var preparation = await fixture.Service.PrepareAsync(
            OperationalInterventionKind.CancelTask,
            "Stop the task");

        var result = await fixture.Service.ExecuteAsync(
            preparation.OperationId,
            preparation.ConfirmationPhrase);

        Assert.True(result.Accepted);
        var request = Assert.IsType<TaskCommandRequest>(fixture.Workspace.LastTaskCommand);
        Assert.Equal("prepare-1", request.Preparation!.PreparationId);
        Assert.Equal(preparation.OperationId, request.CorrelationId);
        Assert.Equal("cancel", request.Command);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Supervision = new FakeSupervision { Snapshot = CreateSnapshot() };
            Gateway = new FakeGateway();
            Workspace = new FakeWorkspace();
            Service = new LogosOperationalInterventionService(Gateway, Workspace, Supervision);
        }

        public FakeSupervision Supervision { get; }
        public FakeGateway Gateway { get; }
        public FakeWorkspace Workspace { get; }
        public LogosOperationalInterventionService Service { get; }
    }

    private sealed class FakeSupervision : IOperationalSupervisionService
    {
        public event EventHandler? Changed;
        public OperationalExecutionSnapshot Snapshot { get; set; } = OperationalExecutionSnapshot.Empty;
        public Task BeginAsync(OperationalExecutionTarget target, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeGateway : IMissionTaskGateway
    {
        public bool IsAvailable => true;
        public string AvailabilityMessage => "Available";
        public MissionOperationPreparationRequest? LastMissionPreparation { get; private set; }
        public TaskOperationPreparationRequest? LastTaskPreparation { get; private set; }

        public Task<PreparedOperationGatewayResult> PrepareMissionOperationAsync(
            MissionOperationPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastMissionPreparation = request;
            return Task.FromResult(Prepared(request.MissionId, null));
        }

        public Task<PreparedOperationGatewayResult> PrepareTaskOperationAsync(
            TaskOperationPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastTaskPreparation = request;
            return Task.FromResult(Prepared("mission-1", request.TaskId));
        }

        private static PreparedOperationGatewayResult Prepared(string missionId, string? taskId)
            => new(
                true,
                "Prepared",
                new PreparedOperationReference("prepare-1", "token-1"),
                new PreparedOperationTargetSnapshot(
                    "logos-1", "vehicle-1", "binding-1", missionId, "mission-execution-1",
                    taskId, taskId is null ? null : "task-execution-1", 7),
                taskId is null ? "mission.intervention" : "task.intervention",
                true,
                true,
                "Allowed",
                "Ready",
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(1));

        public Task<IReadOnlyList<MissionRecord>> ListMissionsAsync(string connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MissionRecord>>([]);
        public Task<IReadOnlyList<OperationalTaskRecord>> ListTasksAsync(string connectionId, string? missionId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OperationalTaskRecord>>([]);
        public Task<DocumentValidationResult> ValidateMissionAsync(string connectionId, MissionRecord mission, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DocumentValidationResult> ValidateTaskAsync(string connectionId, OperationalTaskRecord task, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<GatewayCommandResult> CreateMissionAsync(string connectionId, MissionRecord mission, bool validateOnCreate = true, bool allowReplaceDraft = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<GatewayCommandResult> CreateTaskAsync(string connectionId, OperationalTaskRecord task, bool validateOnCreate = true, bool allowReplaceDraft = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<GatewayCommandResult> AssignTaskAsync(string connectionId, OperationalTaskRecord task, bool validateOnAssign = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<GatewayCommandResult> ExecuteMissionCommandAsync(MissionCommandRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<GatewayCommandResult> ExecuteTaskCommandAsync(TaskCommandRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeWorkspace : IMissionTaskWorkspaceService
    {
        public bool GatewayAvailable => true;
        public string GatewayStatus => "Available";
        public MissionCommandRequest? LastMissionCommand { get; private set; }
        public TaskCommandRequest? LastTaskCommand { get; private set; }

        public Task<GatewayCommandResult> ExecuteMissionCommandAsync(MissionCommandRequest request, CancellationToken cancellationToken = default)
        {
            LastMissionCommand = request;
            return Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted"));
        }

        public Task<GatewayCommandResult> ExecuteTaskCommandAsync(TaskCommandRequest request, CancellationToken cancellationToken = default)
        {
            LastTaskCommand = request;
            return Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted"));
        }

        public Task<MissionRecord> ImportMissionAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationalTaskRecord> ImportTaskAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportMissionAsync(string missionId, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportTaskAsync(string taskId, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentValidationResult> ValidateMissionAsync(string missionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentValidationResult> ValidateTaskAsync(string taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GatewayCommandResult> PublishMissionAsync(string missionId, bool validateOnCreate = true, bool allowReplaceDraft = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GatewayCommandResult> PublishMissionAsync(string missionId, WorkspaceCommandIdentity identity, bool validateOnCreate = true, bool allowReplaceDraft = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GatewayCommandResult> PublishTaskAsync(string taskId, bool validateOnCreate = true, bool allowReplaceDraft = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GatewayCommandResult> PublishTaskAsync(string taskId, WorkspaceCommandIdentity identity, bool validateOnCreate = true, bool allowReplaceDraft = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PlanTaskAssignmentAsync(string taskId, string vehicleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentValidationResult> ValidateTaskForVehicleAsync(string taskId, string vehicleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GatewayCommandResult> AssignTaskAsync(string taskId, string vehicleId, bool validateOnAssign = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GatewayCommandResult> AssignTaskAsync(string taskId, string vehicleId, WorkspaceCommandIdentity identity, bool validateOnAssign = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RefreshRemoteAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static OperationalExecutionSnapshot CreateSnapshot()
    {
        var target = new OperationalExecutionTarget(
            "connection-1", "vehicle-1", "Dracula SITL", "logos-1",
            "mission-1", "task-1", "mission-execution-1", "task-execution-1",
            "correlation-1", DateTimeOffset.UtcNow);
        return OperationalExecutionSnapshot.Empty with
        {
            Target = target,
            Mission = new MissionRuntimeSnapshot(
                "mission-1", "mission-execution-1", "Running", "Healthy", "Ready", "", "",
                "statechart-1", "policy-1", "Active", 0.5, [], [], "StatusUpdate", DateTimeOffset.UtcNow),
            Task = new TaskRuntimeSnapshot(
                "task-1", "task-execution-1", "mission-1", "Running", "Accepted", "Healthy", "Ready",
                "", "", "behaviour-1", "Running", "policy-1", 0.5, "Execute", "", "", 0, 0,
                [], [], "StatusUpdate", DateTimeOffset.UtcNow)
        };
    }
}
