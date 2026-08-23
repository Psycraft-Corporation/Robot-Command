namespace RobotCommand.Models;

public enum OperationalInspectionSource
{
    QuickRun,
    Selection,
    Manual
}

public enum OperationalInspectionResolutionState
{
    Ready,
    UnsupportedSelection,
    MissingRecord,
    NotPublished,
    MissingMission,
    MissingTask,
    MissingVehicle,
    MissingConnection
}

public sealed record OperationalInspectionRequest(
    long Sequence,
    OperationalExecutionTarget Target,
    OperationalInspectionSource Source,
    string SourceDescription,
    bool OpenInspector,
    DateTimeOffset RequestedAt)
{
    public string SourceLabel => Source switch
    {
        OperationalInspectionSource.QuickRun => "Quick Run launch",
        OperationalInspectionSource.Selection => "Current console selection",
        _ => string.IsNullOrWhiteSpace(SourceDescription) ? "Manual target" : SourceDescription
    };
}

public sealed record OperationalInspectionResolution(
    OperationalInspectionResolutionState State,
    string Summary,
    string Detail,
    OperationalExecutionTarget? Target = null)
{
    public bool Resolved => State == OperationalInspectionResolutionState.Ready && Target is not null;

    public static OperationalInspectionResolution Ready(
        OperationalExecutionTarget target,
        string summary,
        string detail)
        => new(OperationalInspectionResolutionState.Ready, summary, detail, target);

    public static OperationalInspectionResolution Unresolved(
        OperationalInspectionResolutionState state,
        string summary,
        string detail)
        => new(state, summary, detail);
}
