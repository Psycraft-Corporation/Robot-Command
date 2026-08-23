using RobotCommand.Models;
using RobotCommand.Services.Missions;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MissionTaskWorkspaceServiceTests
{
    [Fact]
    public async Task ImportMission_AddsMissionAndEmbeddedTasks()
    {
        var fixture = new Fixture();
        var root = Path.Combine(Path.GetTempPath(), $"robot-command-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "mission.json");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": "logos.mission.v1",
              "missionId": "mission-1",
              "name": "Inspection",
              "objective": "Inspect assets",
              "tasks": [
                {
                  "schemaVersion": "logos.task.v1",
                  "taskId": "task-1",
                  "name": "Inspect north asset",
                  "objective": "Capture inspection imagery",
                  "taskType": "inspection",
                  "behaviourId": "inspect-asset"
                }
              ]
            }
            """);

            var mission = await fixture.Workspace.ImportMissionAsync(path);

            Assert.Equal("mission-1", mission.Id);
            Assert.True(fixture.Missions.TryGet("mission-1", out _));
            Assert.True(fixture.Tasks.TryGet("task-1", out var task));
            Assert.Equal("mission-1", task!.MissionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PlanTaskAssignment_CapturesVehicleRouteAndRuntimeIdentity()
    {
        var fixture = new Fixture();
        fixture.Tasks.Upsert(new OperationalTaskRecord(
            "task-1",
            "Search",
            "Draft",
            Objective: "Find target",
            TaskType: "search",
            BehaviourId: "search"));
        fixture.Vehicles.Upsert(new VehicleRecord(
            "vehicle-1",
            "Dracula",
            ["connection-1"],
            "logos-1",
            "team-1",
            "Multicopter",
            "Air",
            "dracula",
            AvailabilityState.Online));

        await fixture.Workspace.PlanTaskAssignmentAsync("task-1", "vehicle-1");

        Assert.True(fixture.Tasks.TryGet("task-1", out var updated));
        Assert.Equal("vehicle-1", updated!.AssignedVehicleId);
        Assert.Equal("logos-1", updated.AssignedLogosInstanceId);
        Assert.Equal("connection-1", updated.ConnectionId);
        Assert.Equal("Planned", updated.AssignmentState);
    }

    [Fact]
    public async Task ExecuteMissionCommand_RecordsRejectedGatewayResult()
    {
        var fixture = new Fixture();
        fixture.Missions.Upsert(new MissionRecord("mission-1", "Mission", "Draft", ConnectionId: "connection-1"));

        var result = await fixture.Workspace.ExecuteMissionCommandAsync(
            new MissionCommandRequest("connection-1", "mission-1", "Start"));

        Assert.False(result.Accepted);
        Assert.Single(fixture.Commands.Items);
        Assert.Equal(OperationalCommandState.Rejected, fixture.Commands.Items[0].State);
    }

    [Fact]
    public async Task ExecuteMissionCommand_UsesSameIdentityForHistoryAndGateway()
    {
        var gateway = new RecordingGateway();
        var fixture = new Fixture(gateway);
        fixture.Missions.Upsert(new MissionRecord("mission-1", "Mission", "Draft", ConnectionId: "connection-1"));

        var result = await fixture.Workspace.ExecuteMissionCommandAsync(
            new MissionCommandRequest("connection-1", "mission-1", "Start"));

        Assert.True(result.Accepted);
        Assert.NotNull(gateway.LastMissionCommand);
        var history = Assert.Single(fixture.Commands.Items);
        Assert.Equal(history.Id, gateway.LastMissionCommand!.RequestId);
        Assert.Equal(history.CorrelationId, gateway.LastMissionCommand.CorrelationId);
        Assert.Equal(history.IdempotencyKey, gateway.LastMissionCommand.IdempotencyKey);
    }

    [Fact]
    public async Task PublishMission_UsesSameIdentityForHistoryAndGateway()
    {
        var gateway = new RecordingGateway();
        var fixture = new Fixture(gateway);
        fixture.Missions.Upsert(new MissionRecord("mission-1", "Patrol", "Draft", ConnectionId: "connection-1", Objective: "Patrol the test route"));

        var result = await fixture.Workspace.PublishMissionAsync("mission-1");

        Assert.True(result.Accepted);
        Assert.NotNull(gateway.LastMissionPublication);
        var history = Assert.Single(fixture.Commands.Items);
        Assert.Equal(history.Id, gateway.LastMissionPublication!.RequestId);
        Assert.Equal(history.CorrelationId, gateway.LastMissionPublication.CorrelationId);
        Assert.Equal(history.IdempotencyKey, gateway.LastMissionPublication.IdempotencyKey);
        Assert.True(fixture.Missions.TryGet("mission-1", out var published));
        Assert.False(published!.IsLocalDraft);
    }

    [Fact]
    public async Task PublishTask_RejectsUntilParentMissionIsPublished()
    {
        var gateway = new RecordingGateway();
        var fixture = new Fixture(gateway);
        fixture.Missions.Upsert(new MissionRecord("mission-1", "Patrol", "Draft", ConnectionId: "connection-1", Objective: "Patrol the test route"));
        fixture.Tasks.Upsert(new OperationalTaskRecord("task-1", "Patrol route", "Draft", MissionId: "mission-1", ConnectionId: "connection-1", Objective: "Visit each waypoint", TaskType: "patrol", BehaviourId: "patrol"));

        var result = await fixture.Workspace.PublishTaskAsync("task-1");

        Assert.False(result.Accepted);
        Assert.Null(gateway.LastTaskPublication);
        Assert.Contains("Publish parent mission", result.Message);
        Assert.Equal(OperationalCommandState.Rejected, Assert.Single(fixture.Commands.Items).State);
    }

    [Fact]
    public async Task ValidateTaskForVehicle_UsesVehicleRoutingAndUpdatesAssignmentState()
    {
        var gateway = new RecordingGateway();
        var fixture = new Fixture(gateway);
        fixture.Tasks.Upsert(new OperationalTaskRecord("task-1", "Patrol route", "Draft", Objective: "Visit each waypoint", TaskType: "patrol", BehaviourId: "patrol"));
        fixture.Vehicles.Upsert(CreateVehicle());

        var validation = await fixture.Workspace.ValidateTaskForVehicleAsync("task-1", "vehicle-1");

        Assert.True(validation.IsValid);
        Assert.NotNull(gateway.LastValidatedTask);
        Assert.Equal("vehicle-1", gateway.LastValidatedTask!.AssignedVehicleId);
        Assert.Equal("logos-1", gateway.LastValidatedTask.AssignedLogosInstanceId);
        Assert.Equal("connection-1", gateway.LastValidatedTask.ConnectionId);
        Assert.True(fixture.Tasks.TryGet("task-1", out var updated));
        Assert.Equal("Validated", updated!.AssignmentState);
    }

    [Fact]
    public async Task AssignTask_RequiresPublishedTask()
    {
        var gateway = new RecordingGateway();
        var fixture = new Fixture(gateway);
        fixture.Tasks.Upsert(new OperationalTaskRecord("task-1", "Patrol route", "Draft", ConnectionId: "connection-1", Objective: "Visit each waypoint", TaskType: "patrol", BehaviourId: "patrol"));
        fixture.Vehicles.Upsert(CreateVehicle());

        var result = await fixture.Workspace.AssignTaskAsync("task-1", "vehicle-1");

        Assert.False(result.Accepted);
        Assert.Null(gateway.LastTaskAssignment);
        Assert.Contains("Publish the task", result.Message);
    }

    [Fact]
    public async Task AssignTask_UsesSameIdentityAndMarksTaskAssigned()
    {
        var gateway = new RecordingGateway();
        var fixture = new Fixture(gateway);
        fixture.Tasks.Upsert(new OperationalTaskRecord("task-1", "Patrol route", "Draft", ConnectionId: "connection-1", Objective: "Visit each waypoint", TaskType: "patrol", BehaviourId: "patrol", IsLocalDraft: false));
        fixture.Vehicles.Upsert(CreateVehicle());

        var result = await fixture.Workspace.AssignTaskAsync("task-1", "vehicle-1");

        Assert.True(result.Accepted);
        Assert.NotNull(gateway.LastTaskAssignment);
        var history = Assert.Single(fixture.Commands.Items);
        Assert.Equal(history.Id, gateway.LastTaskAssignment!.RequestId);
        Assert.Equal(history.CorrelationId, gateway.LastTaskAssignment.CorrelationId);
        Assert.Equal(history.IdempotencyKey, gateway.LastTaskAssignment.IdempotencyKey);
        Assert.True(fixture.Tasks.TryGet("task-1", out var assigned));
        Assert.Equal("Assigned", assigned!.AssignmentState);
    }

    private static VehicleRecord CreateVehicle()
        => new("vehicle-1", "Dracula", ["connection-1"], "logos-1", "team-1", "Multicopter", "Air", "dracula", AvailabilityState.Online);

    private sealed class Fixture
    {
        public EntityStore<string, MissionRecord> Missions { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, OperationalTaskRecord> Tasks { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleRecord> Vehicles { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, OperationalCommandRecord> Commands { get; } = new(item => item.Id, StringComparer.Ordinal);

        public Fixture(IMissionTaskGateway? gateway = null)
        {
            Workspace = new MissionTaskWorkspaceService(
                new MissionTaskDocumentService(),
                gateway ?? new UnavailableMissionTaskGateway(),
                Missions,
                Tasks,
                Vehicles,
                Commands);
        }

        public MissionTaskWorkspaceService Workspace { get; }
    }

    private sealed class RecordingGateway : IMissionTaskGateway
    {
        public bool IsAvailable => true;

        public string AvailabilityMessage => "Available";

        public MissionCommandRequest? LastMissionCommand { get; private set; }
        public MissionPublicationRequest? LastMissionPublication { get; private set; }
        public TaskPublicationRequest? LastTaskPublication { get; private set; }
        public TaskAssignmentRequest? LastTaskAssignment { get; private set; }
        public OperationalTaskRecord? LastValidatedTask { get; private set; }

        public Task<IReadOnlyList<MissionRecord>> ListMissionsAsync(
            string connectionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MissionRecord>>([]);

        public Task<IReadOnlyList<OperationalTaskRecord>> ListTasksAsync(
            string connectionId,
            string? missionId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OperationalTaskRecord>>([]);

        public Task<DocumentValidationResult> ValidateMissionAsync(
            string connectionId,
            MissionRecord mission,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DocumentValidationResult(PlanValidationState.Valid, "Valid", []));
        }

        public Task<DocumentValidationResult> ValidateTaskAsync(
            string connectionId,
            OperationalTaskRecord task,
            CancellationToken cancellationToken = default)
        {
            LastValidatedTask = task;
            return Task.FromResult(new DocumentValidationResult(PlanValidationState.Valid, "Valid", []));
        }

        public Task<GatewayCommandResult> CreateMissionAsync(
            string connectionId,
            MissionRecord mission,
            bool validateOnCreate = true,
            bool allowReplaceDraft = true,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted"));

        public Task<GatewayCommandResult> CreateMissionAsync(MissionPublicationRequest request, CancellationToken cancellationToken = default)
        {
            LastMissionPublication = request;
            return Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted", LifecycleState: "Draft"));
        }

        public Task<GatewayCommandResult> CreateTaskAsync(
            string connectionId,
            OperationalTaskRecord task,
            bool validateOnCreate = true,
            bool allowReplaceDraft = true,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted"));

        public Task<GatewayCommandResult> CreateTaskAsync(TaskPublicationRequest request, CancellationToken cancellationToken = default)
        {
            LastTaskPublication = request;
            return Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted", LifecycleState: "Draft"));
        }

        public Task<GatewayCommandResult> AssignTaskAsync(
            string connectionId,
            OperationalTaskRecord task,
            bool validateOnAssign = true,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted"));

        public Task<GatewayCommandResult> AssignTaskAsync(TaskAssignmentRequest request, CancellationToken cancellationToken = default)
        {
            LastTaskAssignment = request;
            return Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted", "task-execution-1", "Assigned"));
        }

        public Task<GatewayCommandResult> ExecuteMissionCommandAsync(
            MissionCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            LastMissionCommand = request;
            return Task.FromResult(new GatewayCommandResult(
                true,
                OperationalCommandState.Accepted,
                "Accepted",
                "mission-execution-1",
                "Running"));
        }

        public Task<GatewayCommandResult> ExecuteTaskCommandAsync(
            TaskCommandRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayCommandResult(true, OperationalCommandState.Accepted, "Accepted"));
    }
}
