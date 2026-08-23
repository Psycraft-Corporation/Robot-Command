using RobotCommand.Models;
using RobotCommand.Services.Missions;
using Xunit;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Tests;

public sealed class MissionTaskProtoMapperTests
{
    [Fact]
    public void MissionRoundTrip_PreservesOperationalFields()
    {
        var source = new MissionRecord(
            "mission-1",
            "SITL checkout",
            "Draft",
            AssignedTeamId: "team-1",
            AssignedVehicleId: "vehicle-1",
            ConnectionId: "connection-1",
            Objective: "Take off, hold, and land",
            Priority: "High",
            PolicyId: "policy-1",
            GeometryIds: ["zone-1"],
            RequiredCapabilities: ["flight.multicopter"],
            PayloadJson: "{\"altitudeMetres\":10}");

        var proto = MissionTaskProtoMapper.ToProto(source);
        var status = new V1.MissionStatus
        {
            MissionId = source.Id,
            MissionExecutionId = "execution-1",
            State = Enum.Parse<V1.MissionLifecycleState>("Running"),
            Progress = 0.4
        };
        var mapped = MissionTaskProtoMapper.ToModel(proto, "connection-1", status);

        Assert.Equal(source.Id, proto.MissionId);
        Assert.Equal(source.Name, proto.Metadata.DisplayName);
        Assert.Equal("team-1", mapped.AssignedTeamId);
        Assert.Equal("vehicle-1", mapped.AssignedVehicleId);
        Assert.Equal("Running", mapped.State);
        Assert.Equal("execution-1", mapped.MissionExecutionId);
        Assert.Equal(0.4, mapped.Progress);
        Assert.False(mapped.IsLocalDraft);
    }

    [Fact]
    public void TaskRoundTrip_PreservesBehaviourAndAssignment()
    {
        var source = new OperationalTaskRecord(
            "task-1",
            "Takeoff and hold",
            "Draft",
            MissionId: "mission-1",
            AssignedVehicleId: "vehicle-1",
            ConnectionId: "connection-1",
            TeamId: "team-1",
            AssignedLogosInstanceId: "logos-1",
            Objective: "Take off to ten metres",
            TaskType: "flight-checkout",
            Priority: "Normal",
            BehaviourId: "takeoff-hold-land",
            BehaviourVersion: "1.0.0",
            PackageId: "psycraft.flight-checkout",
            ParametersJson: "{\"altitudeMetres\":10}");

        var proto = MissionTaskProtoMapper.ToProto(source);
        var assignment = new V1.TaskAssignment
        {
            TaskId = source.Id,
            MissionId = source.MissionId,
            TeamId = source.TeamId,
            AssignedVehicleId = source.AssignedVehicleId,
            AssignedLogosInstanceId = source.AssignedLogosInstanceId,
            State = Enum.Parse<V1.TaskAssignmentState>("Accepted")
        };
        var status = new V1.TaskStatus
        {
            TaskId = source.Id,
            TaskExecutionId = "task-execution-1",
            State = Enum.Parse<V1.TaskLifecycleState>("Running"),
            AssignmentState = Enum.Parse<V1.TaskAssignmentState>("Accepted"),
            Assignment = assignment,
            Progress = new V1.TaskProgress { Progress = 0.25 }
        };
        var mapped = MissionTaskProtoMapper.ToModel(proto, "connection-1", status);

        Assert.Equal(source.BehaviourId, proto.Behaviour.BehaviourId);
        Assert.Equal(source.BehaviourVersion, proto.Behaviour.BehaviourVersion);
        Assert.Equal(source.PackageId, proto.Behaviour.PackageId);
        Assert.Equal("vehicle-1", mapped.AssignedVehicleId);
        Assert.Equal("Accepted", mapped.AssignmentState);
        Assert.Equal("Running", mapped.State);
        Assert.Equal("task-execution-1", mapped.TaskExecutionId);
        Assert.Equal(0.25, mapped.Progress);
    }

    [Fact]
    public void ValidationWarning_RemainsSubmittableAndIncludesStructuredIssue()
    {
        var validation = new V1.ValidationResult
        {
            Status = Enum.Parse<V1.ValidationStatus>("Warning")
        };
        validation.Issues.Add(new V1.Issue
        {
            Code = "BATTERY_MARGIN_LOW",
            Message = "Estimated battery margin is low.",
            Hint = "Reduce mission duration."
        });
        var status = new V1.DomainStatus
        {
            Ok = true,
            Code = Enum.Parse<V1.DomainCode>("Ok")
        };
        var authorization = new V1.AuthorizationDecision { Allowed = true };

        var result = MissionTaskProtoMapper.ToValidationResult(
            validation,
            status,
            authorization,
            "Task is valid.");

        Assert.Equal(PlanValidationState.Warning, result.State);
        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, item => item.Contains("BATTERY_MARGIN_LOW", StringComparison.Ordinal));
    }

    [Fact]
    public void PolicyDenial_MapsToRejectedCommand()
    {
        var authorization = new V1.AuthorizationDecision
        {
            Allowed = false,
            DeniedReasons = "Operator does not have mission.execute."
        };

        var result = MissionTaskProtoMapper.ToCommandResult(
            null,
            null,
            authorization,
            "Accepted");

        Assert.False(result.Accepted);
        Assert.Equal(OperationalCommandState.Rejected, result.State);
        Assert.Contains("mission.execute", result.Message);
    }
}
