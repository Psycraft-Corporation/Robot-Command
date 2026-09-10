using RobotCommand.Core;

namespace RobotCommand.Services.Mavlink;

/// <summary>Runtime boundary for immediate camera and gimbal actions.</summary>
public interface IMavlinkCameraControlService
{
    Task<MavlinkCommandDispatchResult> ExecuteAsync(
        string connectionId,
        string vehicleId,
        string? cameraSourceId,
        FlightMissionCameraAction action,
        CancellationToken cancellationToken = default);
}

public sealed class MavlinkCameraControlService(IMavlinkConnectionRegistry connections)
    : IMavlinkCameraControlService
{
    public Task<MavlinkCommandDispatchResult> ExecuteAsync(
        string connectionId,
        string vehicleId,
        string? cameraSourceId,
        FlightMissionCameraAction action,
        CancellationToken cancellationToken = default)
        => connections.TryGet(connectionId, out var connection) && connection is not null
            ? connection.SendCameraActionAsync(vehicleId, cameraSourceId, action, cancellationToken)
            : Task.FromResult(MavlinkCommandDispatchResult.Rejected(
                "The MAVLink connection is not active."));
}
