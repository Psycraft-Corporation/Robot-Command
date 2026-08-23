using RobotCommand.Models;
using RobotCommand.Services.Mavlink;

namespace RobotCommand.Services.Operations;

/// <summary>
/// Routes bounded operator operations to a discovered MAVLink autopilot while
/// preserving the common prepare/confirm/execute contract.
/// </summary>
public sealed class MavlinkOperatorCommandGateway(
    IMavlinkConnectionRegistry connections) : IOperatorCommandGateway
{
    public OperatorGatewayStatus Status { get; } = new(
        true,
        "Vehicle operations are submitted directly to the selected autopilot over MAVLink.");

    public Task<OperatorCommandPreparationResult> PrepareAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolve(request, out var connection, out var adapter, out var rejection))
        {
            return Task.FromResult(rejection!);
        }

        var now = DateTimeOffset.UtcNow;
        _ = connection!.TryGetVehicle(request.Target.VehicleId, out _, out _, out var telemetry, out _);
        var findings = new List<OperatorPreflightFinding>
        {
            new(
                "MAVLINK_LOCAL_PREFLIGHT",
                OperatorPreflightSeverity.Info,
                $"{adapter!.DisplayName} system on {connection.Definition.Name} is ready for MAVLink dispatch.",
                "Robot Command MAVLink")
        };
        if (telemetry?.IsStale == true)
        {
            findings.Add(new(
                "MAVLINK_TELEMETRY_DELAYED",
                OperatorPreflightSeverity.Warning,
                $"{adapter.DisplayName} telemetry is delayed. The command may be accepted by the vehicle but its outcome cannot be confirmed until telemetry recovers.",
                "Robot Command MAVLink"));
        }
        var preparation = new PreparedVehicleOperation(
            new PreparedOperationReference(
                $"mavlink-prep-{Guid.NewGuid():N}",
                $"mavlink-token-{Guid.NewGuid():N}"),
            new PreparedOperationTargetSnapshot(
                request.Target.LogosInstanceId ?? request.Target.VehicleId,
                request.Target.VehicleId,
                request.Target.ConnectionId,
                null,
                null,
                null,
                null,
                0),
            request.Command.ToString(),
            "LOCAL_MAVLINK_ALLOW",
            "READY",
            [],
            now,
            now.AddSeconds(30));

        return Task.FromResult(new OperatorCommandPreparationResult(
            true,
            $"{adapter!.DisplayName} MAVLink operation prepared locally.",
            preparation,
            findings));
    }

    public async Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Preparation is null || request.Preparation.Expired)
        {
            return new(
                false,
                OperationalCommandState.Rejected,
                "Execute requires a current, unexpired MAVLink preparation.");
        }

        if (!connections.TryGet(request.Target.ConnectionId, out var connection) ||
            connection is null)
        {
            return new(
                false,
                OperationalCommandState.Rejected,
                "The MAVLink connection is no longer available.");
        }

        var result = await connection.SendOperatorCommandAsync(request, cancellationToken);
        return new(
            result.Accepted,
            result.State,
            result.Message,
            result.MavlinkCommandId is { } commandId
                ? $"mavlink-command-{commandId}"
                : null);
    }

    private bool TryResolve(
        OperatorCommandRequest request,
        out MavlinkConnection? connection,
        out IMavlinkAutopilotAdapter? adapter,
        out OperatorCommandPreparationResult? rejection)
    {
        adapter = null;
        if (!connections.TryGet(request.Target.ConnectionId, out connection) ||
            connection is null)
        {
            rejection = OperatorCommandPreparationResult.Rejected(
                "The MAVLink connection is not active.",
                "MAVLINK_CONNECTION_UNAVAILABLE");
            return false;
        }

        if (!connection.TryGetVehicle(
            request.Target.VehicleId,
            out _,
            out _,
            out var telemetry,
            out var discoveredAdapter) ||
            telemetry is null ||
            discoveredAdapter is null)
        {
            rejection = OperatorCommandPreparationResult.Rejected(
                "The target is not a supported MAVLink multicopter for the configured autopilot profile.",
                "MAVLINK_TARGET_UNSUPPORTED");
            return false;
        }

        adapter = discoveredAdapter;
        if (telemetry.State is AvailabilityState.Offline or AvailabilityState.Reconnecting)
        {
            rejection = OperatorCommandPreparationResult.Rejected(
                $"The {adapter.DisplayName} MAVLink route is offline or reconnecting.",
                "MAVLINK_ROUTE_UNAVAILABLE");
            return false;
        }

        if (adapter.Profile == MavlinkAutopilotProfile.ArduPilot)
        {
            var movement = request.Command is OperatorCommandKind.Takeoff or
                OperatorCommandKind.GoTo or
                OperatorCommandKind.ChangeAltitude or
                OperatorCommandKind.SetHeading;
            var diagnostics = connection.LiveSnapshot.VehicleDiagnostics
                .FirstOrDefault(item => string.Equals(item.VehicleId, request.Target.VehicleId, StringComparison.Ordinal));
            var blocker = diagnostics?.Checks.FirstOrDefault(item =>
                item.State == VehicleDiagnosticCheckState.Failed &&
                item.AffectedOperations?.Contains(request.Command) == true);
            if (blocker is not null)
            {
                rejection = OperatorCommandPreparationResult.Rejected(
                    blocker.Detail,
                    blocker.Code);
                return false;
            }

            if (movement && string.Equals(telemetry.AirframeMode, "Guided_NoGPS", StringComparison.OrdinalIgnoreCase))
            {
                rejection = OperatorCommandPreparationResult.Rejected(
                    "ArduPilot is in Guided_NoGPS; global movement commands require Guided mode with valid GPS position.",
                    "ARDUPILOT_GUIDED_NO_GPS");
                return false;
            }

            if (movement && (!IsValidLatitude(telemetry.LatitudeDegrees) || !IsValidLongitude(telemetry.LongitudeDegrees)))
            {
                rejection = OperatorCommandPreparationResult.Rejected(
                    "ArduPilot has no valid global GPS position; this command cannot be prepared.",
                    "ARDUPILOT_GLOBAL_POSITION_MISSING");
                return false;
            }
        }

        try
        {
            _ = adapter.BuildCommand(request, telemetry);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            rejection = OperatorCommandPreparationResult.Rejected(
                ex.Message,
                "MAVLINK_COMMAND_INVALID");
            return false;
        }

        rejection = null;
        return true;
    }

    private static bool IsValidLatitude(double? value)
        => value is { } latitude && double.IsFinite(latitude) && latitude is >= -90 and <= 90;

    private static bool IsValidLongitude(double? value)
        => value is { } longitude && double.IsFinite(longitude) && longitude is >= -180 and <= 180;
}
