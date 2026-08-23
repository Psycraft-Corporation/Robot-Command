namespace RobotCommand.Models;

public enum GeometryRemoteOperationKind
{
    Create,
    Update,
    Delete
}

public enum GeometryOperationAssessmentState
{
    Ready,
    Warning,
    Blocked,
    Conflict,
    Unavailable
}

public sealed record GeometryOperationFinding(
    string Code,
    GeometryValidationSeverity Severity,
    string Message,
    string Source);

public sealed record GeometryOperationAssessment(
    string ConnectionId,
    string GeometryId,
    GeometryRemoteOperationKind Operation,
    GeometryOperationAssessmentState State,
    string Summary,
    GeometryValidationResult? LocalValidation,
    GeometryValidationResult? LogosValidation,
    GeometryDeploymentRecord? Deployment,
    string? ExpectedRevision,
    IReadOnlyList<GeometryOperationFinding> Findings,
    DateTimeOffset AssessedAt)
{
    public bool CanExecute => State is GeometryOperationAssessmentState.Ready or GeometryOperationAssessmentState.Warning;

    public bool HasWarnings => Findings.Any(item => item.Severity == GeometryValidationSeverity.Warning);

    public bool HasBlockers => Findings.Any(item => item.Severity == GeometryValidationSeverity.Error);
}
