using RobotCommand.Models;
using RobotCommand.Services.Operations;
using Xunit;

namespace RobotCommand.Tests.Operations;

public sealed class OperationalInterventionRulesTests
{
    [Theory]
    [InlineData("Running", OperationalInterventionKind.PauseMission, true)]
    [InlineData("Paused", OperationalInterventionKind.ResumeMission, true)]
    [InlineData("Completed", OperationalInterventionKind.CancelMission, false)]
    public void MissionStateControlsApplicableInterventions(
        string state,
        OperationalInterventionKind kind,
        bool expected)
    {
        var snapshot = Snapshot(missionState: state, taskState: "Running");

        var applicable = OperationalInterventionRules.IsApplicable(kind, snapshot, out _);

        Assert.Equal(expected, applicable);
    }

    [Fact]
    public void DestructiveInterventionsRequireStableTypedPhrase()
    {
        var target = Snapshot("Running", "Running").Target!;
        var captured = new OperationalInterventionTarget(
            target.ConnectionId,
            target.VehicleId,
            target.VehicleName,
            target.LogosInstanceId,
            target.MissionId,
            target.MissionExecutionId,
            target.TaskId,
            target.TaskExecutionId,
            DateTimeOffset.UtcNow);

        Assert.Equal("ABORT mission-1", OperationalInterventionRules.ConfirmationPhrase(
            OperationalInterventionKind.AbortMission,
            captured));
        Assert.Equal("CANCEL task-1", OperationalInterventionRules.ConfirmationPhrase(
            OperationalInterventionKind.CancelTask,
            captured));
    }

    private static OperationalExecutionSnapshot Snapshot(string missionState, string taskState)
    {
        var target = new OperationalExecutionTarget(
            "connection-1",
            "vehicle-1",
            "Dracula SITL",
            "logos-1",
            "mission-1",
            "task-1",
            "mission-execution-1",
            "task-execution-1",
            "correlation-1",
            DateTimeOffset.UtcNow);
        return OperationalExecutionSnapshot.Empty with
        {
            Target = target,
            Mission = new MissionRuntimeSnapshot(
                "mission-1", "mission-execution-1", missionState, "Healthy", "Ready", "", "",
                "statechart-1", "policy-1", "Active", 0.4, [], [], "StatusUpdate", DateTimeOffset.UtcNow),
            Task = new TaskRuntimeSnapshot(
                "task-1", "task-execution-1", "mission-1", taskState, "Accepted", "Healthy", "Ready",
                "", "", "behaviour-1", "Running", "policy-1", 0.4, "Execute", "", "", 0, 0,
                [], [], "StatusUpdate", DateTimeOffset.UtcNow)
        };
    }
}
