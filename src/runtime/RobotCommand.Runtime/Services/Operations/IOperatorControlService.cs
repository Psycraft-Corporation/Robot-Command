using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public interface IOperatorControlService
{
    OperatorGatewayStatus GatewayStatus { get; }

    Task<ManualControlReadiness> GetManualControlReadinessAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ManualControlReadiness(false,
            [new OperatorPreflightFinding(
                "MANUAL_CONTROL_READINESS_UNAVAILABLE",
                OperatorPreflightSeverity.Blocking,
                "Manual-control readiness is unavailable.")]));

    Task<OperatorCommandPlan> PrepareAsync(
        string vehicleId,
        OperatorCommandKind command,
        string reason,
        CancellationToken cancellationToken = default);

    Task<OperatorCommandPlan> PrepareAsync(
        string vehicleId,
        OperatorCommandKind command,
        string reason,
        OperatorCommandParameters parameters,
        CancellationToken cancellationToken = default)
        => PrepareAsync(vehicleId, command, reason, cancellationToken);

    /// <summary>
    /// Re-evaluates a queued command without creating another command-history record.
    /// Implementations that do not provide a specialised assessment may fall back to preparation.
    /// </summary>
    Task<OperatorCommandPlan> AssessAsync(
        string vehicleId,
        OperatorCommandKind command,
        string reason,
        OperatorCommandParameters parameters,
        CancellationToken cancellationToken = default)
        => PrepareAsync(vehicleId, command, reason, parameters, cancellationToken);

    Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandPlan plan,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        OperatorCommandPlan plan,
        string message = "Cancelled before submission.",
        CancellationToken cancellationToken = default);
}
