using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Simulation;
using RobotCommand.Services.Workflows;

namespace RobotCommand.Services.Operations;

public sealed class RoutedOperatorCommandGateway : IOperatorCommandGateway
{
    private readonly LogosOperatorCommandGateway _logos;
    private readonly MavlinkOperatorCommandGateway _mavlink;
    private readonly IMavlinkConnectionRegistry _mavlinkConnections;
    private readonly IGhostUnitService _ghosts;
    private readonly IFormationLockWorkflow _formation;

    public RoutedOperatorCommandGateway(
        LogosOperatorCommandGateway logos,
        MavlinkOperatorCommandGateway mavlink,
        IMavlinkConnectionRegistry mavlinkConnections,
        IGhostUnitService ghosts,
        IFormationLockWorkflow formation)
    {
        _logos = logos;
        _mavlink = mavlink;
        _mavlinkConnections = mavlinkConnections;
        _ghosts = ghosts;
        _formation = formation;
    }

    public OperatorGatewayStatus Status { get; } = new(
        true,
        "Vehicle operations are submitted through the selected unit's Logos, MAVLink, or simulated backend.");

    public Task<OperatorCommandPreparationResult> PrepareAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_ghosts.IsGhostVehicle(request.Target.VehicleId))
        {
            return _ghosts.PrepareAsync(request, cancellationToken);
        }

        return _mavlinkConnections.TryGet(request.Target.ConnectionId, out _)
            ? _mavlink.PrepareAsync(request, cancellationToken)
            : _logos.PrepareAsync(request, cancellationToken);
    }

    public async Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_ghosts.IsGhostVehicle(request.Target.VehicleId))
        {
            await _formation.HandleIndependentOperationAsync(request.Target.VehicleId, ToWorkflow(request.Command), cancellationToken);
            return await _ghosts.ExecuteAsync(request, cancellationToken);
        }

        return await (_mavlinkConnections.TryGet(request.Target.ConnectionId, out _)
            ? _mavlink.ExecuteAsync(request, cancellationToken)
            : _logos.ExecuteAsync(request, cancellationToken));
    }

    private static OperatorWorkflowCommandKind ToWorkflow(OperatorCommandKind command) => command switch
    {
        OperatorCommandKind.Recover => OperatorWorkflowCommandKind.ReturnHome,
        _ => Enum.TryParse<OperatorWorkflowCommandKind>(command.ToString(), out var mapped) ? mapped : OperatorWorkflowCommandKind.Hold
    };
}
