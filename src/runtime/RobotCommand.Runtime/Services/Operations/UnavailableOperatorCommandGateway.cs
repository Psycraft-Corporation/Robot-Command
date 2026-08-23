using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public sealed class UnavailableOperatorCommandGateway : IOperatorCommandGateway
{
    private const string Message =
        "The installed Psycraft.Logos.Api.Sdk package does not expose the policy-governed VehicleOperationsService. " +
        "Robot Command will not bypass Logos through MAVLink, ROS, PX4, or ArduPilot.";

    public OperatorGatewayStatus Status { get; } = new(false, Message);

    public Task<OperatorCommandPreparationResult> PrepareAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OperatorCommandPreparationResult.Rejected(
            Message,
            "VEHICLE_OPERATIONS_UNAVAILABLE"));
    }

    public Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new OperatorCommandResult(
            false,
            OperationalCommandState.Rejected,
            Message));
    }
}
