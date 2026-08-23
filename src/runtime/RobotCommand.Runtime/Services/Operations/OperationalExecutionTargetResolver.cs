using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.Services.Operations;

public sealed class OperationalExecutionTargetResolver : IOperationalExecutionTargetResolver
{
    private readonly IEntityStore<string, MissionRecord> _missions;
    private readonly IEntityStore<string, OperationalTaskRecord> _tasks;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, ConnectionRecord> _connections;
    private readonly IEntityStore<string, RuntimeRecord> _runtimes;

    public OperationalExecutionTargetResolver(
        IEntityStore<string, MissionRecord> missions,
        IEntityStore<string, OperationalTaskRecord> tasks,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, RuntimeRecord> runtimes)
    {
        _missions = missions;
        _tasks = tasks;
        _vehicles = vehicles;
        _connections = connections;
        _runtimes = runtimes;
    }

    public OperationalInspectionResolution Resolve(OperationalSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.Id))
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.UnsupportedSelection,
                "Nothing inspectable is selected.",
                "Select a projected mission, task, vehicle, or runtime.");
        }

        return selection.Kind switch
        {
            SelectionKind.Task => ResolveTask(selection.Id),
            SelectionKind.Mission => ResolveMission(selection.Id),
            SelectionKind.Vehicle => ResolveVehicle(selection.Id),
            SelectionKind.Runtime => ResolveRuntime(selection.Id),
            _ => OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.UnsupportedSelection,
                $"{selection.Kind} selections are not execution targets.",
                "Select a mission, task, vehicle, or runtime with an active projected task.")
        };
    }

    private OperationalInspectionResolution ResolveTask(string taskId)
    {
        if (!_tasks.TryGet(taskId, out var task) || task is null)
        {
            return Missing(OperationalInspectionResolutionState.MissingRecord, "task", taskId);
        }

        return ResolveTaskRecord(task, $"Selected task '{task.Name}'.");
    }

    private OperationalInspectionResolution ResolveMission(string missionId)
    {
        if (!_missions.TryGet(missionId, out var mission) || mission is null)
        {
            return Missing(OperationalInspectionResolutionState.MissingRecord, "mission", missionId);
        }

        var task = BestTask(_tasks.Items.Where(item =>
            string.Equals(item.MissionId, mission.Id, StringComparison.Ordinal)));
        if (task is null)
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.MissingTask,
                $"Mission '{mission.Name}' has no projected task to inspect.",
                "Wait for task projection or select a task explicitly.");
        }

        return ResolveTaskRecord(task, $"Selected mission '{mission.Name}' via task '{task.Name}'.", mission);
    }

    private OperationalInspectionResolution ResolveVehicle(string vehicleId)
    {
        if (!_vehicles.TryGet(vehicleId, out var vehicle) || vehicle is null)
        {
            return Missing(OperationalInspectionResolutionState.MissingRecord, "vehicle", vehicleId);
        }

        var task = BestTask(_tasks.Items.Where(item =>
            string.Equals(item.AssignedVehicleId, vehicle.Id, StringComparison.Ordinal)));
        if (task is null)
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.MissingTask,
                $"Vehicle '{vehicle.Name}' has no projected task to inspect.",
                "Select a task explicitly or wait for an assigned task projection.");
        }

        return ResolveTaskRecord(task, $"Selected vehicle '{vehicle.Name}' via task '{task.Name}'.", vehicleOverride: vehicle);
    }

    private OperationalInspectionResolution ResolveRuntime(string runtimeId)
    {
        if (!_runtimes.TryGet(runtimeId, out var runtime) || runtime is null)
        {
            return Missing(OperationalInspectionResolutionState.MissingRecord, "runtime", runtimeId);
        }

        var runtimeConnections = (runtime.ConnectionIds ?? []).ToHashSet(StringComparer.Ordinal);
        var vehicle = _vehicles.Items
            .Where(item =>
                string.Equals(item.LogosInstanceId, runtime.Id, StringComparison.Ordinal) ||
                (item.ConnectionIds ?? []).Any(runtimeConnections.Contains))
            .OrderByDescending(item => AvailabilityRank(item.State))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (vehicle is null)
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.MissingVehicle,
                $"Runtime '{runtime.Name}' has no projected vehicle.",
                "Wait for vehicle discovery or select a mission or task explicitly.");
        }

        return ResolveVehicle(vehicle.Id);
    }

    private OperationalInspectionResolution ResolveTaskRecord(
        OperationalTaskRecord task,
        string detail,
        MissionRecord? missionOverride = null,
        VehicleRecord? vehicleOverride = null)
    {
        if (task.IsLocalDraft)
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.NotPublished,
                $"Task '{task.Name}' is still a local draft.",
                "Publish or import the task through Logos before opening authoritative supervision.");
        }

        if (string.IsNullOrWhiteSpace(task.MissionId))
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.MissingMission,
                $"Task '{task.Name}' is not attached to a mission.",
                "Only mission-scoped tasks can be supervised by the current Logos watch APIs.");
        }

        var mission = missionOverride;
        if (mission is null && (!_missions.TryGet(task.MissionId, out mission) || mission is null))
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.MissingMission,
                $"Mission '{task.MissionId}' is not projected locally.",
                "Wait for mission projection before opening authoritative supervision.");
        }

        var vehicleId = FirstNonEmpty(task.AssignedVehicleId, mission.AssignedVehicleId);
        var vehicle = vehicleOverride;
        if (vehicle is null &&
            (string.IsNullOrWhiteSpace(vehicleId) || !_vehicles.TryGet(vehicleId, out vehicle) || vehicle is null))
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.MissingVehicle,
                $"Task '{task.Name}' has no projected vehicle target.",
                "Assign the task to a vehicle or wait for assignment projection.");
        }

        var preferredConnectionId = FirstNonEmpty(task.ConnectionId, mission.ConnectionId);
        var connection = ResolveConnection(vehicle, preferredConnectionId);
        if (connection is null)
        {
            return OperationalInspectionResolution.Unresolved(
                OperationalInspectionResolutionState.MissingConnection,
                $"No routed Logos connection is available for '{vehicle.Name}'.",
                "Reconnect the vehicle runtime or wait for route projection.");
        }

        var target = new OperationalExecutionTarget(
            connection.Id,
            vehicle.Id,
            vehicle.Name,
            vehicle.LogosInstanceId,
            mission.Id,
            task.Id,
            mission.MissionExecutionId,
            task.TaskExecutionId,
            $"inspector:{mission.Id}:{task.Id}",
            task.ObservedAt ?? mission.ObservedAt ?? DateTimeOffset.UtcNow);
        return OperationalInspectionResolution.Ready(
            target,
            $"{vehicle.Name} · {mission.Name} · {task.Name}",
            detail);
    }

    private ConnectionRecord? ResolveConnection(VehicleRecord vehicle, string? preferredConnectionId)
    {
        if (!string.IsNullOrWhiteSpace(preferredConnectionId) &&
            _connections.TryGet(preferredConnectionId, out var preferred) &&
            preferred is not null)
        {
            return preferred;
        }

        return (vehicle.ConnectionIds ?? [])
            .Select(id => _connections.TryGet(id, out var connection) ? connection : null)
            .Where(item => item is not null)
            .Cast<ConnectionRecord>()
            .OrderByDescending(item => AvailabilityRank(item.State))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static OperationalTaskRecord? BestTask(IEnumerable<OperationalTaskRecord> candidates)
        => candidates
            .OrderByDescending(item => !item.IsLocalDraft)
            .ThenByDescending(item => ActiveStateRank(item.State))
            .ThenByDescending(item => !string.IsNullOrWhiteSpace(item.TaskExecutionId))
            .ThenByDescending(item => item.ObservedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static int ActiveStateRank(string? state)
        => state?.Trim().ToLowerInvariant() switch
        {
            "running" or "inprogress" or "in_progress" => 6,
            "starting" => 5,
            "paused" or "blocked" => 4,
            "assigned" or "ready" => 3,
            "planned" or "pending" => 2,
            _ => 0
        };

    private static int AvailabilityRank(AvailabilityState state)
        => state switch
        {
            AvailabilityState.Online => 6,
            AvailabilityState.Degraded => 5,
            AvailabilityState.Connecting => 4,
            AvailabilityState.Reconnecting => 3,
            AvailabilityState.Stale => 2,
            AvailabilityState.Offline => 1,
            _ => 0
        };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static OperationalInspectionResolution Missing(
        OperationalInspectionResolutionState state,
        string kind,
        string id)
        => OperationalInspectionResolution.Unresolved(
            state,
            $"The selected {kind} is no longer projected.",
            $"No current {kind} record exists for '{id}'.");
}
