using Google.Protobuf.WellKnownTypes;
using RobotCommand.Models;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Operations;

public static class VehicleOperationProtoMapper
{
    public static V1.VehicleOperationKind ToProto(OperatorCommandKind command)
        => command switch
        {
            OperatorCommandKind.Arm => V1.VehicleOperationKind.Arm,
            OperatorCommandKind.Disarm => V1.VehicleOperationKind.Disarm,
            OperatorCommandKind.Hold => V1.VehicleOperationKind.Hold,
            OperatorCommandKind.Takeoff => V1.VehicleOperationKind.Takeoff,
            OperatorCommandKind.GoTo => V1.VehicleOperationKind.GoTo,
            OperatorCommandKind.ChangeAltitude => V1.VehicleOperationKind.ChangeAltitude,
            OperatorCommandKind.SetHeading => V1.VehicleOperationKind.SetHeading,
            OperatorCommandKind.Land => V1.VehicleOperationKind.Land,
            OperatorCommandKind.Recover => V1.VehicleOperationKind.ReturnHome,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };

    public static V1.VehicleOperationParameters? ToProtoParameters(
        OperatorCommandKind command,
        OperatorCommandParameters? parameters)
        => command switch
        {
            OperatorCommandKind.Takeoff => new V1.VehicleOperationParameters
            {
                Takeoff = new V1.TakeoffParameters
                {
                    AltitudeAglM = parameters?.TakeoffAltitudeAglMetres ?? 0
                }
            },
            OperatorCommandKind.GoTo => ToGoToParameters(parameters),
            OperatorCommandKind.ChangeAltitude => ToChangeAltitudeParameters(parameters),
            OperatorCommandKind.SetHeading => ToSetHeadingParameters(parameters),
            _ => null
        };

    private static V1.VehicleOperationParameters? ToGoToParameters(
        OperatorCommandParameters? parameters)
    {
        if (parameters?.GoToTargetKind == OperatorGoToTargetKind.GlobalWgs84)
        {
            return new V1.VehicleOperationParameters
            {
                GoTo = new V1.GoToParameters
                {
                    GlobalWgs84 = new V1.GlobalWgs84Target
                    {
                        LatitudeDeg = parameters.GoToLatitudeDegrees ?? 0,
                        LongitudeDeg = parameters.GoToLongitudeDegrees ?? 0,
                        AltitudeAmslM = parameters.GoToAltitudeAmslMetres ?? 0,
                        AcceptanceRadiusM = parameters.GoToAcceptanceRadiusMetres ?? 0
                    }
                }
            };
        }

        if (parameters?.GoToTargetKind == OperatorGoToTargetKind.LocalNed)
        {
            var target = new V1.LocalNedTarget
            {
                NorthM = parameters.GoToNorthMetres ?? 0,
                EastM = parameters.GoToEastMetres ?? 0,
                DownM = parameters.GoToDownMetres ?? 0,
                AcceptanceRadiusM = parameters.GoToAcceptanceRadiusMetres ?? 0
            };
            if (parameters.GoToYawDegrees is { } yawDegrees)
            {
                target.YawRad = yawDegrees * Math.PI / 180d;
            }

            return new V1.VehicleOperationParameters
            {
                GoTo = new V1.GoToParameters { LocalNed = target }
            };
        }

        return null;
    }

    private static V1.VehicleOperationParameters? ToChangeAltitudeParameters(
        OperatorCommandParameters? parameters)
    {
        if (parameters?.AltitudeTargetKind is null)
        {
            return null;
        }

        var altitude = new V1.ChangeAltitudeParameters();
        switch (parameters.AltitudeTargetKind)
        {
            case OperatorAltitudeTargetKind.AltitudeAmsl:
                altitude.AltitudeAmslM = parameters.AltitudeAmslMetres ?? 0;
                break;
            case OperatorAltitudeTargetKind.AltitudeAgl:
                altitude.AltitudeAglM = parameters.AltitudeAglMetres ?? 0;
                break;
            case OperatorAltitudeTargetKind.RelativeDelta:
                altitude.RelativeDeltaM = parameters.AltitudeRelativeDeltaMetres ?? 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(parameters), parameters.AltitudeTargetKind, null);
        }

        return new V1.VehicleOperationParameters { ChangeAltitude = altitude };
    }

    private static V1.VehicleOperationParameters? ToSetHeadingParameters(
        OperatorCommandParameters? parameters)
    {
        if (parameters?.HeadingTargetKind is null)
        {
            return null;
        }

        var heading = new V1.SetHeadingParameters();
        switch (parameters.HeadingTargetKind)
        {
            case OperatorHeadingTargetKind.AbsoluteHeading:
                heading.HeadingRad = (parameters.HeadingDegrees ?? 0) * Math.PI / 180d;
                break;
            case OperatorHeadingTargetKind.RelativeYaw:
                heading.RelativeYawRad = (parameters.RelativeYawDegrees ?? 0) * Math.PI / 180d;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(parameters), parameters.HeadingTargetKind, null);
        }

        return new V1.VehicleOperationParameters { SetHeading = heading };
    }

    public static OperatorCommandPreparationResult ToPreparationResult(
        V1.PrepareOperationResponse? response)
    {
        if (response is null)
        {
            return OperatorCommandPreparationResult.Rejected(
                "Logos returned no vehicle-operation preparation response.");
        }

        var preparation = response.Preparation;
        var domainStatus = response.Status;
        if (domainStatus is not { Ok: true } ||
            preparation is null ||
            string.IsNullOrWhiteSpace(preparation.PreparationId) ||
            string.IsNullOrWhiteSpace(preparation.ConfirmationToken))
        {
            return OperatorCommandPreparationResult.Rejected(
                Message(domainStatus?.Message, "Logos rejected vehicle-operation preparation."));
        }

        var readiness = response.Readiness;
        var authorization = preparation.Authorization ?? readiness?.Authorization;
        var allowed = authorization?.Allowed == true;
        var readinessText = readiness?.Readiness?.Readiness.ToString()
                            ?? preparation.Readiness?.Readiness.ToString()
                            ?? "Unknown";
        var blockers = readiness?.BlockingConditions
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var warnings = Enumerable.Empty<V1.Issue>()
            .Concat(readiness is null ? Enumerable.Empty<V1.Issue>() : readiness.Warnings.AsEnumerable())
            .Concat(preparation.Warnings.AsEnumerable())
            .Concat(readiness?.Readiness is null
                ? Enumerable.Empty<V1.Issue>()
                : readiness.Readiness.Issues.AsEnumerable())
            .Concat(preparation.Readiness is null
                ? Enumerable.Empty<V1.Issue>()
                : preparation.Readiness.Issues.AsEnumerable())
            .Select(IssueText)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var findings = new List<OperatorPreflightFinding>();
        foreach (var blocker in blockers)
        {
            findings.Add(new OperatorPreflightFinding(
                "VEHICLE_OPERATION_BLOCKED",
                OperatorPreflightSeverity.Blocking,
                blocker,
                "Logos VehicleOperationsService"));
        }

        foreach (var warning in warnings)
        {
            findings.Add(new OperatorPreflightFinding(
                "VEHICLE_OPERATION_WARNING",
                OperatorPreflightSeverity.Warning,
                warning,
                "Logos VehicleOperationsService"));
        }

        if (!allowed)
        {
            findings.Add(new OperatorPreflightFinding(
                "VEHICLE_OPERATION_NOT_AUTHORIZED",
                OperatorPreflightSeverity.Blocking,
                Message(
                    authorization?.DeniedReasons,
                    authorization?.Status?.Message,
                    "Logos did not authorize the vehicle operation."),
                "Logos VehicleOperationsService"));
        }

        if (string.Equals(readinessText, "NotReady", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new OperatorPreflightFinding(
                "VEHICLE_OPERATION_NOT_READY",
                OperatorPreflightSeverity.Blocking,
                Message(
                    readiness?.Readiness?.Message,
                    preparation.Readiness?.Message,
                    "Logos reports that the vehicle is not ready for this operation."),
                "Logos VehicleOperationsService"));
        }

        var preparedAt = ToDateTimeOffset(preparation.PreparedAt) ?? DateTimeOffset.UtcNow;
        var expiresAt = ToDateTimeOffset(preparation.ExpiresAt) ?? preparedAt;
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            findings.Add(new OperatorPreflightFinding(
                "VEHICLE_OPERATION_PREPARATION_EXPIRED",
                OperatorPreflightSeverity.Blocking,
                "The Logos vehicle-operation preparation is already expired.",
                "Logos VehicleOperationsService"));
        }

        var target = preparation.Target is null
            ? new PreparedOperationTargetSnapshot(null, null, null, null, null, null, null, 0)
            : new PreparedOperationTargetSnapshot(
                EmptyToNull(preparation.Target.LogosInstanceId),
                EmptyToNull(preparation.Target.VehicleId),
                EmptyToNull(preparation.Target.VehicleBindingGeneration),
                EmptyToNull(preparation.Target.MissionId),
                EmptyToNull(preparation.Target.MissionExecutionId),
                EmptyToNull(preparation.Target.TaskId),
                EmptyToNull(preparation.Target.TaskExecutionId),
                preparation.Target.ControlStateVersion);
        var authorizationDecision = authorization is null
            ? "Unavailable"
            : allowed
                ? "Allowed"
                : Message(authorization.DeniedReasons, "Denied");
        var prepared = new PreparedVehicleOperation(
            new PreparedOperationReference(
                preparation.PreparationId,
                preparation.ConfirmationToken),
            target,
            preparation.OperationType,
            authorizationDecision,
            readinessText,
            warnings,
            preparedAt,
            expiresAt);
        var accepted = findings.All(item => item.Severity != OperatorPreflightSeverity.Blocking);
        return new OperatorCommandPreparationResult(
            accepted,
            Message(
                domainStatus.Message,
                readiness?.Readiness?.Message,
                preparation.Readiness?.Message,
                accepted
                    ? "Logos prepared the vehicle operation."
                    : "Logos blocked the vehicle operation."),
            accepted ? prepared : null,
            findings);
    }

    public static OperatorCommandResult ToCommandResult(
        V1.ExecuteOperationResponse? response)
    {
        if (response is null)
        {
            return new OperatorCommandResult(
                false,
                OperationalCommandState.Failed,
                "Logos returned no vehicle-operation execution response.");
        }

        var authorization = response.Authorization;
        var result = response.Result;
        var operation = response.Operation;
        var allowed = authorization?.Allowed == true;
        // ExecuteOperation returns an Accepted command result immediately,
        // while the operation itself may already be Running or may later
        // complete asynchronously. Prefer the operation lifecycle whenever it
        // is present so the UI does not report a dispatched operation as a
        // completed command.
        var state = operation?.State is V1.OperationState.Pending or
            V1.OperationState.Running or
            V1.OperationState.Succeeded or
            V1.OperationState.Cancelled or
            V1.OperationState.Failed or
            V1.OperationState.TimedOut
            ? MapOperationState(operation.State)
            : result is null
                ? OperationalCommandState.Failed
                : MapCommandStatus(result.CommandStatus);
        var accepted = allowed && state is
            OperationalCommandState.Accepted or
            OperationalCommandState.InProgress or
            OperationalCommandState.Succeeded;
        var message = !allowed
            ? Message(
                authorization?.DeniedReasons,
                authorization?.Status?.Message,
                "Logos denied the vehicle operation.")
            : Message(
                result?.Status?.Message,
                operation?.Status?.Message,
                accepted
                    ? "Logos accepted the vehicle operation."
                    : "The vehicle operation was not accepted.");
        var operationId = EmptyToNull(operation?.OperationId)
                          ?? EmptyToNull(result?.Operation?.OperationId);
        return new OperatorCommandResult(accepted, state, message, operationId);
    }

    public static V1.PreparedOperationRef ToProto(PreparedOperationReference preparation)
        => new()
        {
            PreparationId = preparation.PreparationId,
            ConfirmationToken = preparation.ConfirmationToken
        };

    private static OperationalCommandState MapCommandStatus(V1.CommandStatus status)
        => status switch
        {
            V1.CommandStatus.Accepted => OperationalCommandState.Accepted,
            V1.CommandStatus.InProgress => OperationalCommandState.InProgress,
            V1.CommandStatus.Succeeded => OperationalCommandState.Succeeded,
            V1.CommandStatus.Rejected => OperationalCommandState.Rejected,
            V1.CommandStatus.Failed or V1.CommandStatus.Cancelled => OperationalCommandState.Failed,
            _ => OperationalCommandState.Failed
        };

    private static OperationalCommandState MapOperationState(V1.OperationState state)
        => state switch
        {
            V1.OperationState.Pending => OperationalCommandState.Accepted,
            V1.OperationState.Running => OperationalCommandState.InProgress,
            V1.OperationState.Succeeded => OperationalCommandState.Succeeded,
            V1.OperationState.Cancelled => OperationalCommandState.Cancelled,
            V1.OperationState.Failed => OperationalCommandState.Failed,
            V1.OperationState.TimedOut => OperationalCommandState.TimedOut,
            _ => OperationalCommandState.Failed
        };

    private static string IssueText(V1.Issue issue)
        => string.IsNullOrWhiteSpace(issue.Code)
            ? issue.Message
            : $"{issue.Code}: {issue.Message}";

    private static DateTimeOffset? ToDateTimeOffset(Timestamp? timestamp)
    {
        if (timestamp is null || timestamp.Seconds == 0 && timestamp.Nanos == 0) return null;
        return new DateTimeOffset(timestamp.ToDateTime(), TimeSpan.Zero);
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Message(params string?[] candidates)
        => candidates.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))?.Trim()
           ?? "No detail was returned.";
}
