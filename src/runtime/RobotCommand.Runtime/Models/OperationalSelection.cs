namespace RobotCommand.Models;

public enum SelectionKind
{
    None,
    Connection,
    Runtime,
    Team,
    Vehicle,
    Geometry,
    Mission,
    Task,
    Event,
    Command
}

public sealed record SelectionField(string Label, string Value);

public sealed record GeometrySelectionContext(
    string GeometryId,
    string DisplayName,
    GeometryDocumentKind Kind,
    GeometryCoordinateFrame Frame,
    string? ConnectionId,
    string DeploymentState,
    string PolicySummary,
    string Source);

public sealed record OperationalSelection(
    SelectionKind Kind,
    string? Id,
    string Title,
    string Subtitle,
    IReadOnlyList<SelectionField> Fields)
{
    public static OperationalSelection None { get; } = new(
        SelectionKind.None,
        null,
        "Nothing selected",
        "Select a connection, runtime, team, vehicle, geometry, mission, task, event, or command.",
        Array.Empty<SelectionField>());
}
