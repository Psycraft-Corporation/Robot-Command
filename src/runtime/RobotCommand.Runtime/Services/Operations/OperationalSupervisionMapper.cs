using Google.Protobuf.WellKnownTypes;
using RobotCommand.Models;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Operations;

public static class OperationalSupervisionMapper
{
    public static MissionRuntimeSnapshot? MapMission(V1.WatchMissionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = response.MissionStatus;
        if (status is null || string.IsNullOrWhiteSpace(status.MissionId))
        {
            return null;
        }

        return new MissionRuntimeSnapshot(
            status.MissionId,
            EmptyToNull(status.MissionExecutionId),
            Display(status.State),
            Display(status.Health),
            Display(status.Readiness),
            status.Code,
            status.Message,
            status.ActiveStatechartId,
            status.ActivePolicyId,
            status.ActiveStateName,
            NormalizeProgress(status.Progress),
            status.BlockingConditions.ToArray(),
            MapIssues(status.Issues),
            Display(response.EventType),
            ToDateTimeOffset(status.ObservedAt) ?? DateTimeOffset.UtcNow);
    }

    public static TaskRuntimeSnapshot? MapTask(V1.WatchTaskResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = response.TaskStatus;
        if (status is null || string.IsNullOrWhiteSpace(status.TaskId))
        {
            return null;
        }

        var progress = status.Progress;
        return new TaskRuntimeSnapshot(
            status.TaskId,
            EmptyToNull(status.TaskExecutionId),
            status.MissionId,
            Display(status.State),
            Display(status.AssignmentState),
            Display(status.Health),
            Display(status.Readiness),
            status.Code,
            status.Message,
            status.ActiveBehaviourId,
            status.ActiveBehaviourState,
            status.ActivePolicyId,
            progress is null ? null : NormalizeProgress(progress.Progress),
            progress?.Phase ?? string.Empty,
            progress?.ActiveGeometryId ?? string.Empty,
            progress?.ActiveObjectId ?? string.Empty,
            progress?.CompletedUnits ?? 0,
            progress?.TotalUnits ?? 0,
            status.BlockingConditions.ToArray(),
            MapIssues(status.Issues),
            Display(response.EventType),
            ToDateTimeOffset(status.ObservedAt) ?? DateTimeOffset.UtcNow);
    }

    public static AutonomyRuntimeSnapshot? MapAutonomy(V1.WatchAutonomyRuntimeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var runtime = response.RuntimeStatus;
        var tree = response.TreeStatus ?? runtime?.Behaviour?.TreeStatus;
        var behaviour = runtime?.Behaviour;
        var statechart = runtime?.Statechart;
        if (runtime is null && tree is null && behaviour is null && statechart is null)
        {
            return null;
        }

        var observedAt = ToDateTimeOffset(runtime?.ObservedAt) ??
                         ToDateTimeOffset(tree?.ObservedAt) ??
                         ToDateTimeOffset(behaviour?.ObservedAt) ??
                         ToDateTimeOffset(statechart?.ObservedAt) ??
                         DateTimeOffset.UtcNow;
        var issues = MapIssues(runtime?.Issues)
            .Concat(MapIssues(behaviour?.Issues))
            .Concat(MapIssues(tree?.Issues))
            .Concat(MapIssues(statechart?.Issues))
            .DistinctBy(item => (item.Code, item.Message, item.ResourceId))
            .ToArray();

        return new AutonomyRuntimeSnapshot(
            Display(runtime?.State),
            Display(runtime?.Health),
            Display(runtime?.Readiness),
            runtime?.Code ?? string.Empty,
            runtime?.Message ?? string.Empty,
            FirstNonEmpty(behaviour?.BehaviourId, tree?.BehaviourId),
            FirstNonEmpty(behaviour?.BehaviourVersion, tree?.BehaviourVersion),
            Display(behaviour?.State),
            Display(tree?.TreeState),
            Display(tree?.LatestOutcome),
            tree?.OutcomeReason ?? string.Empty,
            tree?.Sequence ?? 0,
            tree?.TreeTickCount ?? 0,
            tree?.TreeReady ?? false,
            tree?.Ticking ?? false,
            statechart?.StatechartId ?? string.Empty,
            statechart?.StatechartVersion ?? string.Empty,
            Display(statechart?.State),
            statechart?.ActiveStateName ?? string.Empty,
            statechart?.ActiveTransition ?? string.Empty,
            runtime?.BlockingConditions.ToArray() ?? [],
            issues,
            Display(response.EventType),
            observedAt);
    }

    public static IReadOnlyList<BehaviourTreeNodeRuntimeRecord> MergeNodes(
        IReadOnlyList<BehaviourTreeNodeRuntimeRecord> existing,
        V1.WatchAutonomyRuntimeResponse response)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(response);

        var incoming = new List<V1.BehaviourTreeNodeStatus>();
        var tree = response.TreeStatus ?? response.RuntimeStatus?.Behaviour?.TreeStatus;
        if (tree is not null)
        {
            incoming.AddRange(tree.Nodes);
        }
        incoming.AddRange(response.NodeUpdates);
        if (incoming.Count == 0)
        {
            return existing;
        }

        var map = existing.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        foreach (var node in incoming)
        {
            var mapped = MapNode(node);
            map[mapped.NodeId] = mapped;
        }

        return map.Values
            .OrderBy(item => item.Depth)
            .ThenBy(item => item.ChildIndex)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsDifferentTree(
        AutonomyRuntimeSnapshot? previous,
        AutonomyRuntimeSnapshot? current)
    {
        if (previous is null || current is null)
        {
            return false;
        }

        return !string.Equals(previous.BehaviourId, current.BehaviourId, StringComparison.Ordinal) ||
               !string.Equals(previous.BehaviourVersion, current.BehaviourVersion, StringComparison.Ordinal) ||
               current.TreeSequence < previous.TreeSequence;
    }

    private static BehaviourTreeNodeRuntimeRecord MapNode(V1.BehaviourTreeNodeStatus node)
    {
        var id = string.IsNullOrWhiteSpace(node.NodeId)
            ? $"anonymous:{node.Depth}:{node.ChildIndex}:{node.NodeType}:{node.NodeName}"
            : node.NodeId;
        return new BehaviourTreeNodeRuntimeRecord(
            id,
            node.ParentNodeId,
            node.NodeName,
            node.NodeType,
            Display(node.NodeKind),
            Display(node.State),
            node.Depth,
            node.ChildIndex,
            node.TickCount,
            node.LastTickDuration is null ? null : node.LastTickDuration.ToTimeSpan().TotalMilliseconds,
            node.Reason,
            MapIssues(node.Issues),
            ToDateTimeOffset(node.StateChangedAt),
            ToDateTimeOffset(node.LastObservedAt) ?? DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<OperationalRuntimeIssue> MapIssues(
        IEnumerable<V1.Issue>? issues)
        => issues?.Select(item => new OperationalRuntimeIssue(
                item.Code,
                Display(item.Severity),
                item.Message,
                item.FieldPath,
                item.ResourceId,
                item.Hint))
            .ToArray() ?? [];

    private static string Display(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "Unspecified", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        return text;
    }

    private static double? NormalizeProgress(double value)
        => double.IsFinite(value) && value is >= 0 and <= 1 ? value : null;

    private static DateTimeOffset? ToDateTimeOffset(Timestamp? timestamp)
    {
        if (timestamp is null || timestamp.Seconds == 0 && timestamp.Nanos == 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(timestamp.ToDateTime(), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
