using Grpc.Core;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Missions;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Operations;

/// <summary>
/// Executes bounded operator interventions through Logos. This gateway never
/// falls back to MAVLink, ROS, PX4, or ArduPilot control paths.
/// </summary>
public sealed class LogosOperatorCommandGateway : IOperatorCommandGateway
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(12);
    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogosCommandMetadataFactory _metadata;

    public LogosOperatorCommandGateway(
        ILogosOperationalSessionRegistry sessions,
        ILogosCommandMetadataFactory metadata)
    {
        _sessions = sessions;
        _metadata = metadata;
    }

    public OperatorGatewayStatus Status { get; } = new(
        true,
        "Vehicle operations are submitted through Logos VehicleOperationsService.");

    public async Task<OperatorCommandPreparationResult> PrepareAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(request.Target.ConnectionId, cancellationToken);
            var client = session.Clients.VehicleOperations
                         ?? throw new InvalidOperationException(
                             "The generated SDK does not contain VehicleOperationsService.");
            var apiRequest = new V1.PrepareOperationRequest
            {
                Command = _metadata.Create(
                    $"vehicle.{CommandName(request.Command)}.prepare",
                    request.Target.VehicleId,
                    request.CommandId,
                    request.CorrelationId,
                    request.IdempotencyKey + ":prepare"),
                LogosInstanceId = request.Target.LogosInstanceId ?? string.Empty,
                VehicleId = request.Target.VehicleId,
                Kind = VehicleOperationProtoMapper.ToProto(request.Command),
                Reason = request.Reason,
                Emergency = request.Emergency,
                ExpectedControlStateVersion = 0
            };
            var operationParameters = VehicleOperationProtoMapper.ToProtoParameters(
                request.Command,
                request.Parameters);
            if (operationParameters is not null)
            {
                apiRequest.Parameters = operationParameters;
            }

            apiRequest.PolicyContext.Add("source", "logos-robot-command");
            apiRequest.PolicyContext.Add("command_id", request.CommandId);
            apiRequest.PolicyContext.Add("correlation_id", request.CorrelationId);
            apiRequest.PolicyContext.Add("operator_command", request.Command.ToString());
            AddParameterPolicyContext(apiRequest.PolicyContext, request.Parameters);

            var response = await client.PrepareOperationAsync(
                apiRequest,
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            return VehicleOperationProtoMapper.ToPreparationResult(response);
        }
        catch (RpcException ex) when (ex.StatusCode is not StatusCode.Cancelled)
        {
            return OperatorCommandPreparationResult.Rejected(
                RpcMessage("prepare", ex),
                "VEHICLE_OPERATION_RPC_FAILED");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OperatorCommandPreparationResult.Rejected(
                $"Could not prepare the Logos vehicle operation: {ex.Message}",
                "VEHICLE_OPERATION_PREPARE_FAILED");
        }
    }

    public async Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Preparation is null)
        {
            return new OperatorCommandResult(
                false,
                OperationalCommandState.Rejected,
                "Execute requires a current Logos vehicle-operation preparation.");
        }

        if (request.Preparation.Expired)
        {
            return new OperatorCommandResult(
                false,
                OperationalCommandState.Rejected,
                "The Logos vehicle-operation confirmation token expired.");
        }

        try
        {
            var session = await RequireSessionAsync(request.Target.ConnectionId, cancellationToken);
            var client = session.Clients.VehicleOperations
                         ?? throw new InvalidOperationException(
                             "The generated SDK does not contain VehicleOperationsService.");
            var preparedTarget = request.Preparation.Target;
            var apiRequest = new V1.ExecuteOperationRequest
            {
                Command = _metadata.Create(
                    $"vehicle.{CommandName(request.Command)}.execute",
                    request.Target.VehicleId,
                    request.CommandId,
                    request.CorrelationId,
                    request.IdempotencyKey),
                Preparation = VehicleOperationProtoMapper.ToProto(request.Preparation.Reference),
                Reason = request.Reason,
                Emergency = request.Emergency,
                LogosInstanceId = preparedTarget.LogosInstanceId
                                  ?? request.Target.LogosInstanceId
                                  ?? string.Empty,
                VehicleId = preparedTarget.VehicleId ?? request.Target.VehicleId,
                VehicleBindingGeneration = preparedTarget.VehicleBindingGeneration ?? string.Empty
            };
            var response = await client.ExecuteOperationAsync(
                apiRequest,
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            return VehicleOperationProtoMapper.ToCommandResult(response);
        }
        catch (RpcException ex) when (ex.StatusCode is not StatusCode.Cancelled)
        {
            return new OperatorCommandResult(
                false,
                ex.StatusCode is StatusCode.DeadlineExceeded
                    ? OperationalCommandState.TimedOut
                    : OperationalCommandState.Failed,
                RpcMessage("execute", ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new OperatorCommandResult(
                false,
                OperationalCommandState.Failed,
                $"Could not execute the Logos vehicle operation: {ex.Message}");
        }
    }

    private async Task<ILogosOperationalSession> RequireSessionAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new InvalidOperationException("A Logos connection is required.");
        }

        if (!_sessions.TryGet(connectionId, out var session) || session is null)
        {
            throw new InvalidOperationException(
                $"Connection '{connectionId}' has no active Logos operational session.");
        }

        var snapshot = session.Status;
        if (snapshot.InspectedAt == DateTimeOffset.MinValue ||
            snapshot.Get(OperationalApiDomain.VehicleOperations).Availability is
                OperationalApiAvailability.Unknown or OperationalApiAvailability.Inspecting)
        {
            snapshot = await session.InspectAsync(cancellationToken: cancellationToken);
        }

        var status = snapshot.Get(OperationalApiDomain.VehicleOperations);
        if (!status.Available)
        {
            throw new InvalidOperationException(
                $"Vehicle Operations API is unavailable on '{connectionId}': {status.Detail}");
        }

        return session;
    }

    private static void AddParameterPolicyContext(
        Google.Protobuf.Collections.MapField<string, string> context,
        OperatorCommandParameters? parameters)
    {
        if (parameters is null) return;
        Add(context, "takeoff_altitude_agl_m", parameters.TakeoffAltitudeAglMetres);
        if (parameters.GoToTargetKind is { } targetKind)
        {
            context["go_to_target_kind"] = targetKind.ToString();
        }
        Add(context, "go_to_latitude_deg", parameters.GoToLatitudeDegrees);
        Add(context, "go_to_longitude_deg", parameters.GoToLongitudeDegrees);
        Add(context, "go_to_altitude_amsl_m", parameters.GoToAltitudeAmslMetres);
        Add(context, "go_to_north_m", parameters.GoToNorthMetres);
        Add(context, "go_to_east_m", parameters.GoToEastMetres);
        Add(context, "go_to_down_m", parameters.GoToDownMetres);
        Add(context, "go_to_yaw_deg", parameters.GoToYawDegrees);
        Add(context, "go_to_acceptance_radius_m", parameters.GoToAcceptanceRadiusMetres);
        if (parameters.AltitudeTargetKind is { } altitudeTargetKind)
        {
            context["altitude_target_kind"] = altitudeTargetKind.ToString();
        }
        Add(context, "altitude_amsl_m", parameters.AltitudeAmslMetres);
        Add(context, "altitude_agl_m", parameters.AltitudeAglMetres);
        Add(context, "altitude_relative_delta_m", parameters.AltitudeRelativeDeltaMetres);
        if (parameters.HeadingTargetKind is { } headingTargetKind)
        {
            context["heading_target_kind"] = headingTargetKind.ToString();
        }
        Add(context, "heading_deg", parameters.HeadingDegrees);
        Add(context, "relative_yaw_deg", parameters.RelativeYawDegrees);
    }

    private static void Add(
        Google.Protobuf.Collections.MapField<string, string> context,
        string key,
        double? value)
    {
        if (value is { } actual)
        {
            context[key] = actual.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string CommandName(OperatorCommandKind command)
        => command switch
        {
            OperatorCommandKind.Recover => "return-home",
            OperatorCommandKind.GoTo => "go-to",
            OperatorCommandKind.ChangeAltitude => "change-altitude",
            OperatorCommandKind.SetHeading => "set-heading",
            _ => command.ToString().ToLowerInvariant()
        };

    private static DateTime Deadline() => DateTime.UtcNow.Add(RpcTimeout);

    private static string RpcMessage(string action, RpcException exception)
        => string.IsNullOrWhiteSpace(exception.Status.Detail)
            ? $"Logos could not {action} the vehicle operation: {exception.StatusCode}."
            : $"Logos could not {action} the vehicle operation: {exception.Status.Detail}";
}
