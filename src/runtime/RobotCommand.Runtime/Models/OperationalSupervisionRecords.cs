namespace RobotCommand.Models;

public enum OperationalWatchState
{
    Stopped,
    Starting,
    Live,
    BackingOff,
    Completed,
    Unsupported,
    Faulted
}

public sealed record OperationalWatchStatus(
    OperationalWatchState State,
    string Summary,
    string Detail,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastMessageAt = null,
    int RestartCount = 0,
    string? LastError = null)
{
    public static OperationalWatchStatus Stopped(string domain) => new(
        OperationalWatchState.Stopped,
        $"{domain} watch stopped",
        "No active execution is being supervised.",
        DateTimeOffset.UtcNow);

    public bool Active => State is
        OperationalWatchState.Starting or
        OperationalWatchState.Live or
        OperationalWatchState.BackingOff;
}

public sealed record OperationalExecutionTarget(
    string ConnectionId,
    string VehicleId,
    string VehicleName,
    string? LogosInstanceId,
    string MissionId,
    string TaskId,
    string? MissionExecutionId,
    string? TaskExecutionId,
    string CorrelationId,
    DateTimeOffset StartedAt);

public sealed record OperationalRuntimeIssue(
    string Code,
    string Severity,
    string Message,
    string FieldPath = "",
    string ResourceId = "",
    string Hint = "")
{
    public string Summary => string.IsNullOrWhiteSpace(Code)
        ? Message
        : $"{Code}: {Message}";
}

public sealed record MissionRuntimeSnapshot(
    string MissionId,
    string? MissionExecutionId,
    string State,
    string Health,
    string Readiness,
    string Code,
    string Message,
    string ActiveStatechartId,
    string ActivePolicyId,
    string ActiveStateName,
    double? Progress,
    IReadOnlyList<string> BlockingConditions,
    IReadOnlyList<OperationalRuntimeIssue> Issues,
    string EventType,
    DateTimeOffset ObservedAt)
{
    public string ProgressText => Progress is >= 0 and <= 1
        ? $"{Progress.Value:P0}"
        : "--";
}

public sealed record TaskRuntimeSnapshot(
    string TaskId,
    string? TaskExecutionId,
    string MissionId,
    string State,
    string AssignmentState,
    string Health,
    string Readiness,
    string Code,
    string Message,
    string ActiveBehaviourId,
    string ActiveBehaviourState,
    string ActivePolicyId,
    double? Progress,
    string Phase,
    string ActiveGeometryId,
    string ActiveObjectId,
    int CompletedUnits,
    int TotalUnits,
    IReadOnlyList<string> BlockingConditions,
    IReadOnlyList<OperationalRuntimeIssue> Issues,
    string EventType,
    DateTimeOffset ObservedAt)
{
    public string ProgressText => Progress is >= 0 and <= 1
        ? $"{Progress.Value:P0}"
        : TotalUnits > 0
            ? $"{CompletedUnits}/{TotalUnits}"
            : "--";
}

public sealed record BehaviourTreeNodeRuntimeRecord(
    string NodeId,
    string ParentNodeId,
    string Name,
    string NodeType,
    string Kind,
    string State,
    int Depth,
    int ChildIndex,
    ulong TickCount,
    double? LastTickMilliseconds,
    string Reason,
    IReadOnlyList<OperationalRuntimeIssue> Issues,
    DateTimeOffset? StateChangedAt,
    DateTimeOffset ObservedAt)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? NodeType : Name;

    public string Detail => string.IsNullOrWhiteSpace(Reason)
        ? $"{NodeType} / ticks {TickCount}"
        : Reason;

    public bool Running => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public bool Failed => State is "Failed" or "Aborted" or "Cancelled";

    public bool NeedsAttention => Running || Failed || Issues.Count > 0;
}

public sealed record AutonomyRuntimeSnapshot(
    string State,
    string Health,
    string Readiness,
    string Code,
    string Message,
    string BehaviourId,
    string BehaviourVersion,
    string BehaviourState,
    string TreeState,
    string LatestOutcome,
    string OutcomeReason,
    ulong TreeSequence,
    ulong TreeTickCount,
    bool TreeReady,
    bool Ticking,
    string StatechartId,
    string StatechartVersion,
    string StatechartState,
    string ActiveStateName,
    string ActiveTransition,
    IReadOnlyList<string> BlockingConditions,
    IReadOnlyList<OperationalRuntimeIssue> Issues,
    string EventType,
    DateTimeOffset ObservedAt);

public sealed record OperationalExecutionSnapshot(
    OperationalExecutionTarget? Target,
    MissionRuntimeSnapshot? Mission,
    TaskRuntimeSnapshot? Task,
    AutonomyRuntimeSnapshot? Autonomy,
    IReadOnlyList<BehaviourTreeNodeRuntimeRecord> TreeNodes,
    OperationalWatchStatus MissionWatch,
    OperationalWatchStatus TaskWatch,
    OperationalWatchStatus AutonomyWatch,
    DateTimeOffset UpdatedAt)
{
    public static OperationalExecutionSnapshot Empty { get; } = new(
        null,
        null,
        null,
        null,
        [],
        OperationalWatchStatus.Stopped("Mission"),
        OperationalWatchStatus.Stopped("Task"),
        OperationalWatchStatus.Stopped("Autonomy"),
        DateTimeOffset.UtcNow);

    public bool HasTarget => Target is not null;

    public IReadOnlyList<BehaviourTreeNodeRuntimeRecord> AttentionNodes => TreeNodes
        .Where(item => item.NeedsAttention)
        .OrderByDescending(item => item.Running)
        .ThenByDescending(item => item.Failed)
        .ThenBy(item => item.Depth)
        .ThenBy(item => item.ChildIndex)
        .Take(24)
        .ToArray();

    public IReadOnlyList<OperationalRuntimeIssue> Issues =>
        (Mission?.Issues ?? [])
        .Concat(Task?.Issues ?? [])
        .Concat(Autonomy?.Issues ?? [])
        .Concat(TreeNodes.SelectMany(item => item.Issues))
        .DistinctBy(item => (item.Code, item.Message, item.ResourceId))
        .ToArray();
}
