using RobotCommand.Models;

namespace RobotCommand.State;

public static class SelectionFactory
{
    public static OperationalSelection From(ConnectionRecord connection) => new(
        SelectionKind.Connection,
        connection.Id,
        connection.Name,
        connection.Target,
        [
            new("Mode", connection.Mode.ToString()),
            new("State", connection.State.ToString()),
            new("Auto reconnect", connection.AutoReconnect ? "Enabled" : "Disabled"),
            new("Logos instance", connection.LogosInstanceId ?? "Not discovered"),
            new("Runtime role", connection.RuntimeRole ?? "Not discovered"),
            new("Last seen", connection.LastSeen?.ToLocalTime().ToString("G") ?? "Never"),
            new("Last attempt", connection.LastAttempt?.ToLocalTime().ToString("G") ?? "Never"),
            new("Last error", connection.LastError ?? "None")
        ]);

    public static OperationalSelection From(RuntimeRecord runtime) => new(
        SelectionKind.Runtime,
        runtime.Id,
        runtime.Name,
        runtime.Role ?? "Unknown",
        [
            new("Logos instance", runtime.Id),
            new("Connections", Join(runtime.ConnectionIds ?? [])),
            new("State", runtime.State.ToString()),
            new("Runtime mode", runtime.RuntimeMode ?? "Unknown"),
            new("Platform", runtime.PlatformKind ?? "Unknown"),
            new("Profile", runtime.PlatformProfile ?? "Unknown"),
            new("Logos version", runtime.LogosVersion ?? "Unknown"),
            new("Health", runtime.Health ?? "Unknown"),
            new("Readiness", runtime.Readiness ?? "Unknown"),
            new("Capabilities", (runtime.CapabilityKeys?.Count ?? 0).ToString()),
            new("Last seen", runtime.LastSeen?.ToLocalTime().ToString("G") ?? "Never")
        ]);

    public static OperationalSelection From(TeamRecord team) => new(
        SelectionKind.Team,
        team.Id,
        team.Name,
        team.IsPartial
            ? $"{team.MemberCount} directly observed member(s)"
            : $"{team.MemberCount} member(s)",
        [
            new("Connections", Join(team.ConnectionIds)),
            new("State", team.State.ToString()),
            new("Discovery", team.IsPartial ? "Partial - inferred from connected runtimes" : "Complete"),
            new("Manager", team.ManagerLogosInstanceId ?? "Not observed"),
            new("Last seen", team.LastSeen?.ToLocalTime().ToString("G") ?? "Never")
        ]);

    public static OperationalSelection From(VehicleRecord vehicle) => new(
        SelectionKind.Vehicle,
        vehicle.Id,
        vehicle.Name,
        $"{vehicle.Domain} · {vehicle.VehicleClass}",
        [
            new("Connections", Join(vehicle.ConnectionIds)),
            new("Logos instance", vehicle.LogosInstanceId ?? "Unknown"),
            new("Team", vehicle.TeamId ?? "Unassigned"),
            new("Profile", string.IsNullOrWhiteSpace(vehicle.ProfileKey) ? "Unknown" : vehicle.ProfileKey),
            new("State", vehicle.State.ToString()),
            new("Readiness", vehicle.Readiness),
            new("Lifecycle", vehicle.Lifecycle),
            new("Arm state", vehicle.ArmState),
            new("Health", vehicle.Health),
            new("Capabilities", (vehicle.CapabilityKeys?.Count ?? 0).ToString()),
            new("Last seen", vehicle.LastSeen?.ToLocalTime().ToString("G") ?? "Never")
        ]);

    public static OperationalSelection From(GeometrySelectionContext geometry) => new(
        SelectionKind.Geometry,
        geometry.GeometryId,
        geometry.DisplayName,
        $"{geometry.Kind} · {geometry.DeploymentState}",
        [
            new("Geometry ID", geometry.GeometryId),
            new("Kind", geometry.Kind.ToString()),
            new("Frame", geometry.Frame.ToString()),
            new("Connection", geometry.ConnectionId ?? "Local only"),
            new("Deployment", geometry.DeploymentState),
            new("Policy", geometry.PolicySummary),
            new("Source", geometry.Source)
        ]);

    public static OperationalSelection From(MissionRecord mission) => new(
        SelectionKind.Mission,
        mission.Id,
        mission.Name,
        $"{mission.State} · {mission.Priority}",
        [
            new("Connection", mission.ConnectionId ?? "Local only"),
            new("Team", mission.AssignedTeamId ?? "Unassigned"),
            new("Vehicle", mission.AssignedVehicleId ?? "Unassigned"),
            new("Objective", string.IsNullOrWhiteSpace(mission.Objective) ? "Not specified" : mission.Objective),
            new("Policy", string.IsNullOrWhiteSpace(mission.PolicyId) ? "Default" : mission.PolicyId),
            new("Geometry", Join(mission.GeometryIds ?? [])),
            new("Validation", mission.ValidationState.ToString()),
            new("Validation summary", mission.ValidationSummary),
            new("Execution", mission.MissionExecutionId ?? "Not started"),
            new("Progress", mission.Progress is null ? "Unknown" : $"{mission.Progress:P0}"),
            new("Source", mission.SourcePath ?? (mission.IsLocalDraft ? "Local draft" : "Logos event projection"))
        ]);

    public static OperationalSelection From(OperationalTaskRecord task) => new(
        SelectionKind.Task,
        task.Id,
        task.Name,
        $"{task.State} · {task.TaskType}",
        [
            new("Connection", task.ConnectionId ?? "Local only"),
            new("Mission", task.MissionId ?? "Unassigned"),
            new("Team", task.TeamId ?? "Unassigned"),
            new("Vehicle", task.AssignedVehicleId ?? "Unassigned"),
            new("Assignment", task.AssignmentState),
            new("Objective", string.IsNullOrWhiteSpace(task.Objective) ? "Not specified" : task.Objective),
            new("Behaviour", string.IsNullOrWhiteSpace(task.BehaviourId) ? "Not specified" : task.BehaviourId),
            new("Package", string.IsNullOrWhiteSpace(task.PackageId) ? "Not specified" : task.PackageId),
            new("Geometry", Join(task.GeometryIds ?? [])),
            new("Validation", task.ValidationState.ToString()),
            new("Validation summary", task.ValidationSummary),
            new("Execution", task.TaskExecutionId ?? "Not started"),
            new("Progress", task.Progress is null ? "Unknown" : $"{task.Progress:P0}"),
            new("Source", task.SourcePath ?? (task.IsLocalDraft ? "Local draft" : "Logos event projection"))
        ]);


    public static OperationalSelection From(OperationalCommandRecord command) => new(
        SelectionKind.Command,
        command.Id,
        command.Summary,
        $"{command.State} · {command.TargetKind} {command.TargetId}",
        [
            new("Kind", command.Kind),
            new("State", command.State.ToString()),
            new("Target", $"{command.TargetKind} {command.TargetId}"),
            new("Connection", command.ConnectionId ?? "Not routed"),
            new("Vehicle", command.VehicleId ?? "Not applicable"),
            new("Logos instance", command.LogosInstanceId ?? "Unknown"),
            new("Correlation", command.CorrelationId),
            new("Idempotency", command.IdempotencyKey ?? "Not set"),
            new("Policy", command.PolicyDecision ?? "Not evaluated"),
            new("Reason", command.Reason ?? "Not supplied"),
            new("Emergency", command.Emergency ? "Yes" : "No"),
            new("Message", command.Message),
            new("Created", command.CreatedAt.ToLocalTime().ToString("G")),
            new("Updated", command.UpdatedAt.ToLocalTime().ToString("G"))
        ]);

    public static OperationalSelection From(ConsoleEventRecord item) => new(
        SelectionKind.Event,
        item.Id,
        item.Message,
        item.Source,
        [
            new("Severity", item.Severity),
            new("Time", item.Timestamp.ToLocalTime().ToString("G"))
        ]);

    private static string Join(IReadOnlyList<string> values)
        => values.Count == 0 ? "None" : string.Join(", ", values);
}
