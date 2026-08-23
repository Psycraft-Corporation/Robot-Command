using Google.Protobuf.WellKnownTypes;
using RobotCommand.Services.Operations;
using Xunit;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Tests.Operations;

public sealed class OperationalSupervisionMapperTests
{
    [Fact]
    public void MapMission_ProjectsAuthoritativeStatus()
    {
        var observedAt = DateTime.UtcNow;
        var response = new V1.WatchMissionResponse
        {
            EventType = (V1.WatchMissionEventType)2,
            MissionStatus = new V1.MissionStatus
            {
                MissionId = "mission-1",
                MissionExecutionId = "mission-execution-1",
                State = (V1.MissionLifecycleState)4,
                Health = (V1.HealthLevel)1,
                Readiness = (V1.ReadinessLevel)1,
                Message = "Mission is running",
                ActiveStateName = "Executing",
                Progress = 0.4,
                ObservedAt = Timestamp.FromDateTime(observedAt)
            }
        };
        response.MissionStatus.BlockingConditions.Add("waiting-for-task");

        var mapped = OperationalSupervisionMapper.MapMission(response);

        Assert.NotNull(mapped);
        Assert.Equal("mission-1", mapped!.MissionId);
        Assert.Equal("mission-execution-1", mapped.MissionExecutionId);
        Assert.Equal("Running", mapped.State);
        Assert.Equal(0.4, mapped.Progress);
        Assert.Contains("waiting-for-task", mapped.BlockingConditions);
    }

    [Fact]
    public void MapTask_ProjectsProgressAndAssignment()
    {
        var response = new V1.WatchTaskResponse
        {
            EventType = (V1.WatchTaskEventType)4,
            TaskStatus = new V1.TaskStatus
            {
                TaskId = "task-1",
                TaskExecutionId = "task-execution-1",
                MissionId = "mission-1",
                State = (V1.TaskLifecycleState)6,
                AssignmentState = (V1.TaskAssignmentState)3,
                ActiveBehaviourId = "takeoff-hold-land",
                ActiveBehaviourState = "RUNNING",
                Progress = new V1.TaskProgress
                {
                    Progress = 0.25,
                    Phase = "takeoff",
                    CompletedUnits = 1,
                    TotalUnits = 4
                }
            }
        };

        var mapped = OperationalSupervisionMapper.MapTask(response);

        Assert.NotNull(mapped);
        Assert.Equal("Running", mapped!.State);
        Assert.Equal("Accepted", mapped.AssignmentState);
        Assert.Equal("takeoff", mapped.Phase);
        Assert.Equal(0.25, mapped.Progress);
        Assert.Equal("takeoff-hold-land", mapped.ActiveBehaviourId);
    }

    [Fact]
    public void MergeNodes_AppliesIncrementalUpdatesWithoutDroppingOtherNodes()
    {
        var snapshot = new V1.WatchAutonomyRuntimeResponse
        {
            EventType = (V1.WatchAutonomyRuntimeEventType)1,
            RuntimeStatus = new V1.AutonomyRuntimeStatus
            {
                State = (V1.AutonomyRuntimeState)5,
                Behaviour = new V1.BehaviourRuntimeStatus
                {
                    BehaviourId = "behaviour-1",
                    BehaviourVersion = "1.0.0",
                    TreeStatus = new V1.BehaviourTreeStatus
                    {
                        BehaviourId = "behaviour-1",
                        BehaviourVersion = "1.0.0",
                        Sequence = 1,
                        TreeState = (V1.BehaviourTreeNodeExecutionState)3,
                        Ticking = true,
                        TreeReady = true
                    }
                }
            }
        };
        snapshot.RuntimeStatus.Behaviour.TreeStatus.Nodes.Add(new V1.BehaviourTreeNodeStatus
        {
            NodeId = "root",
            NodeName = "Root",
            NodeType = "Sequence",
            Depth = 0,
            State = (V1.BehaviourTreeNodeExecutionState)3,
            TickCount = 2
        });
        snapshot.RuntimeStatus.Behaviour.TreeStatus.Nodes.Add(new V1.BehaviourTreeNodeStatus
        {
            NodeId = "takeoff",
            ParentNodeId = "root",
            NodeName = "Takeoff",
            NodeType = "TrajectoryTakeoffAction",
            Depth = 1,
            State = (V1.BehaviourTreeNodeExecutionState)3,
            TickCount = 2
        });

        var nodes = OperationalSupervisionMapper.MergeNodes([], snapshot);

        var update = new V1.WatchAutonomyRuntimeResponse
        {
            EventType = (V1.WatchAutonomyRuntimeEventType)4
        };
        update.NodeUpdates.Add(new V1.BehaviourTreeNodeStatus
        {
            NodeId = "takeoff",
            ParentNodeId = "root",
            NodeName = "Takeoff",
            NodeType = "TrajectoryTakeoffAction",
            Depth = 1,
            State = (V1.BehaviourTreeNodeExecutionState)4,
            TickCount = 3,
            Reason = "Target altitude reached"
        });

        nodes = OperationalSupervisionMapper.MergeNodes(nodes, update);

        Assert.Equal(2, nodes.Count);
        Assert.Equal("Running", nodes.Single(item => item.NodeId == "root").State);
        var takeoff = nodes.Single(item => item.NodeId == "takeoff");
        Assert.Equal("Succeeded", takeoff.State);
        Assert.Equal("Target altitude reached", takeoff.Reason);
    }

    [Fact]
    public void MapAutonomy_ProjectsTreeAndStatechartState()
    {
        var response = new V1.WatchAutonomyRuntimeResponse
        {
            EventType = (V1.WatchAutonomyRuntimeEventType)2,
            RuntimeStatus = new V1.AutonomyRuntimeStatus
            {
                State = (V1.AutonomyRuntimeState)5,
                Message = "Autonomy active",
                Behaviour = new V1.BehaviourRuntimeStatus
                {
                    BehaviourId = "behaviour-1",
                    BehaviourVersion = "1.0.0",
                    State = (V1.AutonomyRuntimeState)5,
                    TreeStatus = new V1.BehaviourTreeStatus
                    {
                        BehaviourId = "behaviour-1",
                        BehaviourVersion = "1.0.0",
                        TreeState = (V1.BehaviourTreeNodeExecutionState)3,
                        Sequence = 7,
                        TreeTickCount = 42,
                        TreeReady = true,
                        Ticking = true
                    }
                },
                Statechart = new V1.StatechartRuntimeStatus
                {
                    StatechartId = "mission-lifecycle",
                    StatechartVersion = "1.0.0",
                    State = (V1.AutonomyRuntimeState)5,
                    ActiveStateName = "Running",
                    ActiveTransition = "task_started"
                }
            }
        };

        var mapped = OperationalSupervisionMapper.MapAutonomy(response);

        Assert.NotNull(mapped);
        Assert.Equal("Active", mapped!.State);
        Assert.Equal("behaviour-1", mapped.BehaviourId);
        Assert.Equal("Running", mapped.TreeState);
        Assert.Equal((ulong)42, mapped.TreeTickCount);
        Assert.Equal("Running", mapped.ActiveStateName);
        Assert.Equal("task_started", mapped.ActiveTransition);
    }
}
