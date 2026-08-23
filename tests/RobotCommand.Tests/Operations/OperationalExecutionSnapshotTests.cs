using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests.Operations;

public sealed class OperationalExecutionSnapshotTests
{
    private static readonly string[] expected = new[] { "running", "failed" };

    [Fact]
    public void AttentionNodes_PrioritizesRunningThenFailedNodes()
    {
        var now = DateTimeOffset.UtcNow;
        var nodes = new[]
        {
            Node("idle", "Idle", 0),
            Node("failed", "Failed", 2),
            Node("running", "Running", 3)
        };
        var snapshot = OperationalExecutionSnapshot.Empty with
        {
            TreeNodes = nodes,
            UpdatedAt = now
        };

        var attention = snapshot.AttentionNodes;

        Assert.Equal(expected, attention.Select(item => item.NodeId));
    }

    [Fact]
    public void Issues_DeduplicatesTheSameRuntimeIssue()
    {
        var issue = new OperationalRuntimeIssue("BLOCKED", "Error", "Vehicle is not ready", ResourceId: "vehicle-1");
        var mission = new MissionRuntimeSnapshot(
            "mission-1", null, "Blocked", "Unknown", "Unknown", "", "", "", "", "", null,
            [], [issue], "StatusUpdate", DateTimeOffset.UtcNow);
        var task = new TaskRuntimeSnapshot(
            "task-1", null, "mission-1", "Blocked", "Accepted", "Unknown", "Unknown", "", "", "", "", "",
            null, "", "", "", 0, 0, [], [issue], "StatusUpdate", DateTimeOffset.UtcNow);
        var snapshot = OperationalExecutionSnapshot.Empty with { Mission = mission, Task = task };

        Assert.Single(snapshot.Issues);
    }

    private static BehaviourTreeNodeRuntimeRecord Node(string id, string state, int depth)
        => new(
            id,
            string.Empty,
            id,
            "TestNode",
            "Action",
            state,
            depth,
            0,
            1,
            null,
            string.Empty,
            [],
            null,
            DateTimeOffset.UtcNow);
}
