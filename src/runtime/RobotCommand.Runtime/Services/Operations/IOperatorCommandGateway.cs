using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public interface IOperatorCommandGateway
{
    OperatorGatewayStatus Status { get; }

    Task<OperatorCommandPreparationResult> PrepareAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default);
}
