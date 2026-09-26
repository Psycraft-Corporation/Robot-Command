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

        if (request.Command == OperatorCommandKind.SetCameraSettings)
        {
            return Task.FromResult(OperatorCommandPreparationResult.Rejected(
                "Camera setting changes are currently supported only by Ghost camera simulation.",
                "CAMERA_SETTINGS_BACKEND_UNSUPPORTED"));
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
            if (!IsCameraCommand(request.Command))
                await _formation.HandleIndependentOperationAsync(request.Target.VehicleId, ToWorkflow(request.Command), cancellationToken);
            return await _ghosts.ExecuteAsync(request, cancellationToken);
        }

        if (request.Command == OperatorCommandKind.SetCameraSettings)
        {
            return new(false, OperationalCommandState.Rejected,
                "Camera setting changes are currently supported only by Ghost camera simulation.");
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

    private static bool IsCameraCommand(OperatorCommandKind command)
        => command is OperatorCommandKind.CapturePhoto or OperatorCommandKind.StartVideo or
            OperatorCommandKind.StopVideo or OperatorCommandKind.CenterGimbal or
            OperatorCommandKind.NadirGimbal or OperatorCommandKind.SetGimbal or
            OperatorCommandKind.SetCameraSettings;
}
