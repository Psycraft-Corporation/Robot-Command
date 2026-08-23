namespace RobotCommand.Models;

public sealed record GeometryLibraryIssue(
    string Code,
    GeometryValidationSeverity Severity,
    string Message,
    string Path,
    string? GeometryId = null,
    string FieldPath = "")
{
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}
