namespace RobotCommand.Models;

public enum GeometryValidationState
{
    Valid,
    Warning,
    Invalid,
    Unavailable
}

public enum GeometryValidationSeverity
{
    Information,
    Warning,
    Error
}

public sealed record GeometryValidationIssue(
    string Code,
    GeometryValidationSeverity Severity,
    string Message,
    string FieldPath = "",
    string Source = "Robot Command");

public sealed record GeometryValidationResult(
    GeometryValidationState State,
    string Summary,
    IReadOnlyList<GeometryValidationIssue> Issues)
{
    public bool IsValid => State is GeometryValidationState.Valid or GeometryValidationState.Warning;

    public bool HasErrors => Issues.Any(item => item.Severity == GeometryValidationSeverity.Error);

    public static GeometryValidationResult Unavailable(string message)
        => new(
            GeometryValidationState.Unavailable,
            message,
            [new GeometryValidationIssue(
                "GEOMETRY_VALIDATION_UNAVAILABLE",
                GeometryValidationSeverity.Warning,
                message)]);
}
