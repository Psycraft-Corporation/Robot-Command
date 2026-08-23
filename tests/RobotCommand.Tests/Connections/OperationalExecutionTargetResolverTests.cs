using RobotCommand.Models;
using RobotCommand.Services.Operations;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperationalExecutionTargetResolverTests
{
    [Fact]
    public void ResolveTask_UsesExactProjectedMissionVehicleAndConnection()
    {
        var fixture = new Fixture();
        var mission = fixture.AddMission("mission-1", "mission-execution-1");
        var task = fixture.AddTask("task-1", mission.Id, "task-execution-1", "Running");

        var resolution = fixture.Resolver.Resolve(SelectionFactory.From(task));

        Assert.True(resolution.Resolved);
        Assert.Equal("connection-1", resolution.Target!.ConnectionId);
        Assert.Equal("vehicle-1", resolution.Target.VehicleId);
        Assert.Equal("mission-1", resolution.Target.MissionId);
        Assert.Equal("task-1", resolution.Target.TaskId);
        Assert.Equal("mission-execution-1", resolution.Target.MissionExecutionId);
        Assert.Equal("task-execution-1", resolution.Target.TaskExecutionId);
    }

    [Fact]
    public void ResolveMission_PrefersTaskWithActiveExecution()
    {
        var fixture = new Fixture();
        var mission = fixture.AddMission("mission-1", "mission-execution-1");
        fixture.AddTask("task-planned", mission.Id, null, "Planned");
        fixture.AddTask("task-running", mission.Id, "task-execution-1", "Running");

        var resolution = fixture.Resolver.Resolve(SelectionFactory.From(mission));

        Assert.True(resolution.Resolved);
        Assert.Equal("task-running", resolution.Target!.TaskId);
    }

    [Fact]
    public void ResolveVehicle_PrefersLatestActiveAssignedTask()
    {
        var fixture = new Fixture();
        var mission = fixture.AddMission("mission-1", "mission-execution-1");
        fixture.AddTask("task-completed", mission.Id, "task-execution-old", "Completed", DateTimeOffset.UtcNow.AddMinutes(-5));
        fixture.AddTask("task-running", mission.Id, "task-execution-new", "Running", DateTimeOffset.UtcNow);
        var vehicle = Assert.Single(fixture.Vehicles.Items);

        var resolution = fixture.Resolver.Resolve(SelectionFactory.From(vehicle));

        Assert.True(resolution.Resolved);
        Assert.Equal("task-running", resolution.Target!.TaskId);
    }

    [Fact]
    public void ResolveTask_RejectsUnroutableLocalDraft()
    {
        var fixture = new Fixture(addConnection: false);
        var mission = fixture.AddMission("mission-local", null, connectionId: null);
        var task = fixture.AddTask("task-local", mission.Id, null, "Draft", connectionId: null);

        var resolution = fixture.Resolver.Resolve(SelectionFactory.From(task));

        Assert.False(resolution.Resolved);
        Assert.Equal(OperationalInspectionResolutionState.NotPublished, resolution.State);
    }

    private sealed class Fixture
    {
        public Fixture(bool addConnection = true)
        {
            if (addConnection)
            {
                Connections.Upsert(new ConnectionRecord(
                    "connection-1",
                    "Dracula direct",
                    "http://127.0.0.1:50051",
                    ConnectionMode.Direct,
                    AvailabilityState.Online,
                    true));
            }

            Vehicles.Upsert(new VehicleRecord(
                "vehicle-1",
                "Dracula",
                ["connection-1"],
                "logos-1",
                null,
                "Multicopter",
                "Air",
                "dracula",
                AvailabilityState.Online));
            Resolver = new OperationalExecutionTargetResolver(
                Missions,
                Tasks,
                Vehicles,
                Connections,
                Runtimes);
        }

        public EntityStore<string, MissionRecord> Missions { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, OperationalTaskRecord> Tasks { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleRecord> Vehicles { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, ConnectionRecord> Connections { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, RuntimeRecord> Runtimes { get; } = new(item => item.Id, StringComparer.Ordinal);
        public OperationalExecutionTargetResolver Resolver { get; }

        public MissionRecord AddMission(
            string id,
            string? executionId,
            string? connectionId = "connection-1")
        {
            var mission = new MissionRecord(
                id,
                $"Mission {id}",
                executionId is null ? "Draft" : "Running",
                AssignedVehicleId: "vehicle-1",
                ConnectionId: connectionId,
                MissionExecutionId: executionId,
                ObservedAt: DateTimeOffset.UtcNow,
                IsLocalDraft: executionId is null);
            Missions.Upsert(mission);
            return mission;
        }

        public OperationalTaskRecord AddTask(
            string id,
            string missionId,
            string? executionId,
            string state,
            DateTimeOffset? observedAt = null,
            string? connectionId = "connection-1")
        {
            var task = new OperationalTaskRecord(
                id,
                $"Task {id}",
                state,
                MissionId: missionId,
                AssignedVehicleId: "vehicle-1",
                ConnectionId: connectionId,
                BehaviourId: "search",
                BehaviourVersion: "1.0.0",
                TaskExecutionId: executionId,
                ObservedAt: observedAt ?? DateTimeOffset.UtcNow,
                IsLocalDraft: executionId is null);
            Tasks.Upsert(task);
            return task;
        }
    }
}
