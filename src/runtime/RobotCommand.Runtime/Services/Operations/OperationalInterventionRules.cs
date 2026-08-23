using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public static class OperationalInterventionRules
{
    public static bool IsApplicable(
        OperationalInterventionKind kind,
        OperationalExecutionSnapshot snapshot,
        out string reason)
    {
        reason = string.Empty;
        if (snapshot.Target is null)
        {
            reason = "No authoritative Logos execution is selected.";
            return false;
        }

        var missionState = Normalize(snapshot.Mission?.State);
        var taskState = Normalize(snapshot.Task?.State);
        return kind switch
        {
            OperationalInterventionKind.PauseMission => Require(
                missionState is "running" or "ready" or "blocked",
                "The mission must be active before it can be paused.",
                out reason),
            OperationalInterventionKind.ResumeMission => Require(
                missionState == "paused",
                "Only a paused mission can be resumed.",
                out reason),
            OperationalInterventionKind.EndMission => Require(
                !IsMissionTerminal(missionState),
                "The mission has already reached a terminal state.",
                out reason),
            OperationalInterventionKind.CancelMission => Require(
                !IsMissionTerminal(missionState),
                "The mission has already reached a terminal state.",
                out reason),
            OperationalInterventionKind.AbortMission => Require(
                !IsMissionTerminal(missionState),
                "The mission has already reached a terminal state.",
                out reason),
            OperationalInterventionKind.CancelTask => Require(
                snapshot.Task is not null && !IsTaskTerminal(taskState),
                "The task is unavailable or has already reached a terminal state.",
                out reason),
            OperationalInterventionKind.AbortTask => Require(
                snapshot.Task is not null && !IsTaskTerminal(taskState),
                "The task is unavailable or has already reached a terminal state.",
                out reason),
            _ => Require(false, "The requested intervention is unsupported.", out reason)
        };
    }

    public static bool IsSameTarget(
        OperationalInterventionTarget target,
        OperationalExecutionSnapshot snapshot)
    {
        var current = snapshot.Target;
        return current is not null &&
               string.Equals(current.ConnectionId, target.ConnectionId, StringComparison.Ordinal) &&
               string.Equals(current.VehicleId, target.VehicleId, StringComparison.Ordinal) &&
               string.Equals(current.MissionId, target.MissionId, StringComparison.Ordinal) &&
               string.Equals(current.TaskId, target.TaskId, StringComparison.Ordinal) &&
               MatchesOptional(current.MissionExecutionId, target.MissionExecutionId) &&
               MatchesOptional(current.TaskExecutionId, target.TaskExecutionId);
    }

    public static bool IsTaskOperation(OperationalInterventionKind kind)
        => kind is OperationalInterventionKind.CancelTask or OperationalInterventionKind.AbortTask;

    public static bool IsEmergency(OperationalInterventionKind kind)
        => kind is OperationalInterventionKind.AbortMission or OperationalInterventionKind.AbortTask;

    public static bool IsDestructive(OperationalInterventionKind kind)
        => kind is
            OperationalInterventionKind.EndMission or
            OperationalInterventionKind.CancelMission or
            OperationalInterventionKind.AbortMission or
            OperationalInterventionKind.CancelTask or
            OperationalInterventionKind.AbortTask;

    public static string DisplayName(OperationalInterventionKind kind)
        => kind switch
        {
            OperationalInterventionKind.PauseMission => "Pause mission",
            OperationalInterventionKind.ResumeMission => "Resume mission",
            OperationalInterventionKind.EndMission => "End mission",
            OperationalInterventionKind.CancelMission => "Cancel mission",
            OperationalInterventionKind.AbortMission => "Abort mission",
            OperationalInterventionKind.CancelTask => "Cancel task",
            OperationalInterventionKind.AbortTask => "Abort task",
            _ => kind.ToString()
        };

    public static string ConfirmationPhrase(
        OperationalInterventionKind kind,
        OperationalInterventionTarget target)
        => kind switch
        {
            OperationalInterventionKind.EndMission => $"END {target.MissionId}",
            OperationalInterventionKind.CancelMission => $"CANCEL {target.MissionId}",
            OperationalInterventionKind.AbortMission => $"ABORT {target.MissionId}",
            OperationalInterventionKind.CancelTask => $"CANCEL {target.TaskId}",
            OperationalInterventionKind.AbortTask => $"ABORT {target.TaskId}",
            _ => string.Empty
        };

    private static bool IsMissionTerminal(string state)
        => state is "completed" or "cancelled" or "aborted" or "faulted";

    private static bool IsTaskTerminal(string state)
        => state is "completed" or "succeeded" or "failed" or "cancelled" or "aborted";

    private static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool MatchesOptional(string? left, string? right)
        => string.IsNullOrWhiteSpace(left) ||
           string.IsNullOrWhiteSpace(right) ||
           string.Equals(left, right, StringComparison.Ordinal);

    private static bool Require(bool condition, string failure, out string reason)
    {
        reason = condition ? string.Empty : failure;
        return condition;
    }
}
