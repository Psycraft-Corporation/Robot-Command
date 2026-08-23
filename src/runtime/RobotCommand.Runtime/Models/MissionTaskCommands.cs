namespace RobotCommand.Models;

public sealed record MissionCommandRequest(
    string ConnectionId,
    string MissionId,
    string Command,
    string? MissionExecutionId = null,
    string Reason = "",
    bool Emergency = false,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null,
    PreparedOperationReference? Preparation = null);

public sealed record TaskCommandRequest(
    string ConnectionId,
    string TaskId,
    string Command,
    string? TaskExecutionId = null,
    string? AssignedVehicleId = null,
    string? AssignedMemberId = null,
    string? AssignedLogosInstanceId = null,
    string Reason = "",
    bool Emergency = false,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null,
    PreparedOperationReference? Preparation = null);

public sealed record MissionPublicationRequest(
    string ConnectionId,
    MissionRecord Mission,
    bool ValidateOnCreate = true,
    bool AllowReplaceDraft = true,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

public sealed record TaskPublicationRequest(
    string ConnectionId,
    OperationalTaskRecord Task,
    bool ValidateOnCreate = true,
    bool AllowReplaceDraft = true,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

public sealed record TaskAssignmentRequest(
    string ConnectionId,
    OperationalTaskRecord Task,
    bool ValidateOnAssign = true,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

public sealed record GatewayCommandResult(
    bool Accepted,
    OperationalCommandState State,
    string Message,
    string? ExecutionId = null,
    string? LifecycleState = null,
    double? Progress = null);

public sealed record WorkspaceCommandIdentity(
    string RequestId,
    string CorrelationId,
    string IdempotencyKey)
{
    public static WorkspaceCommandIdentity Create(
        string operationId,
        string step,
        string? requestId = null)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }

        if (string.IsNullOrWhiteSpace(step))
        {
            throw new ArgumentException("A command step is required.", nameof(step));
        }

        var normalizedOperation = operationId.Trim();
        var normalizedStep = NormalizeStep(step);
        return new WorkspaceCommandIdentity(
            string.IsNullOrWhiteSpace(requestId)
                ? $"cmd-{Guid.NewGuid():N}"
                : requestId.Trim(),
            normalizedOperation,
            $"{normalizedOperation}:{normalizedStep}");
    }

    private static string NormalizeStep(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "command" : normalized;
    }
}
