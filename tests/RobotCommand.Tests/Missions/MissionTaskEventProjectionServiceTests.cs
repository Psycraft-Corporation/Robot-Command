using RobotCommand.Models;
using RobotCommand.Services.Missions;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MissionTaskEventProjectionServiceTests
{
    [Fact]
    public async Task NewMissionAndTaskEvents_CreateProjectedRecords()
    {
        var events = new EntityStore<string, ConsoleEventRecord>(item => item.Id, StringComparer.Ordinal);
        var missions = new EntityStore<string, MissionRecord>(item => item.Id, StringComparer.Ordinal);
        var tasks = new EntityStore<string, OperationalTaskRecord>(item => item.Id, StringComparer.Ordinal);
        using var service = new MissionTaskEventProjectionService(events, missions, tasks);
        await service.StartAsync(CancellationToken.None);

        events.Upsert(new ConsoleEventRecord(
            "event-1",
            DateTimeOffset.UtcNow,
            "Info",
            "mission",
            "Mission entered running state",
            "connection-1",
            Domain: "Mission",
            Kind: "StateChanged",
            Code: "MISSION_RUNNING",
            SubjectId: "mission-1"));
        events.Upsert(new ConsoleEventRecord(
            "event-2",
            DateTimeOffset.UtcNow,
            "Info",
            "task",
            "Task succeeded",
            "connection-1",
            Domain: "Task",
            Kind: "StateChanged",
            Code: "TASK_SUCCEEDED",
            SubjectId: "task-1"));

        Assert.True(missions.TryGet("mission-1", out var mission));
        Assert.Equal("Running", mission!.State);
        Assert.False(mission.IsLocalDraft);
        Assert.True(tasks.TryGet("task-1", out var task));
        Assert.Equal("Succeeded", task!.State);
    }
}
