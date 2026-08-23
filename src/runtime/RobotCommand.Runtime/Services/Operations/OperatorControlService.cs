using System.Text.Json;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.ManualControl;
using RobotCommand.Services.Reconciliation;
using RobotCommand.Services.Simulation;
using RobotCommand.State;

namespace RobotCommand.Services.Operations;

public sealed class OperatorControlService : IOperatorControlService
{
    private readonly AppConfiguration _configuration;
    private readonly IOperatorCommandGateway _gateway;
    private readonly ILogosConnectionManager _connections;
    private readonly IEntityStore<string, ConnectionRecord> _connectionStore;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot>? _diagnostics;
    private readonly IGhostUnitService? _ghosts;
    private readonly IManualControlRegistry? _manualControl;
    private readonly IUnitDefinitionService? _reconciliation;

    public OperatorControlService(
        AppConfiguration configuration,
        IOperatorCommandGateway gateway,
        ILogosConnectionManager connections,
        IEntityStore<string, ConnectionRecord> connectionStore,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, OperationalCommandRecord> commands,
        IGhostUnitService? ghosts = null,
        IManualControlRegistry? manualControl = null,
        IEntityStore<string, VehicleDiagnosticsSnapshot>? diagnostics = null,
        IUnitDefinitionService? reconciliation = null)
    {
        _configuration = configuration;
        _gateway = gateway;
        _connections = connections;
        _connectionStore = connectionStore;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _commands = commands;
        _diagnostics = diagnostics;
        _ghosts = ghosts;
        _manualControl = manualControl;
        _reconciliation = reconciliation;
    }

    public OperatorGatewayStatus GatewayStatus => _gateway.Status;

    public Task<ManualControlReadiness> GetManualControlReadinessAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        vehicleId = _reconciliation?.ResolveCommandSource(vehicleId) ?? vehicleId;
        if (!_vehicles.TryGet(vehicleId, out var vehicle) || vehicle is null)
        {
            return Task.FromResult(new ManualControlReadiness(false,
                [new OperatorPreflightFinding(
                    "VEHICLE_MISSING",
                    OperatorPreflightSeverity.Blocking,
                    "The selected vehicle is no longer available.")]));
        }

        var connection = ResolveConnection(vehicle);
        var diagnostics = LatestDiagnostics(vehicle.Id, connection?.Id);
        return Task.FromResult(OperatorControlRules.EvaluateManualControl(
            vehicle,
            LatestTelemetry(vehicle.Id),
            connection,
            diagnostics,
            diagnostics?.Backend,
            diagnostics?.Mode));
    }

    public Task<OperatorCommandPlan> PrepareAsync(
        string vehicleId,
        OperatorCommandKind command,
        string reason,
        CancellationToken cancellationToken = default)
        => PrepareAsync(
            vehicleId,
            command,
            reason,
            OperatorCommandParameters.None,
            cancellationToken);

    public async Task<OperatorCommandPlan> PrepareAsync(
        string vehicleId,
        OperatorCommandKind command,
        string reason,
        OperatorCommandParameters parameters,
        CancellationToken cancellationToken = default)
        => await BuildPlanAsync(vehicleId, command, reason, parameters, recordCommand: true, cancellationToken);

    public async Task<OperatorCommandPlan> AssessAsync(
        string vehicleId,
        OperatorCommandKind command,
        string reason,
        OperatorCommandParameters parameters,
        CancellationToken cancellationToken = default)
        => await BuildPlanAsync(vehicleId, command, reason, parameters, recordCommand: false, cancellationToken);

    private async Task<OperatorCommandPlan> BuildPlanAsync(
        string vehicleId,
        OperatorCommandKind command,
        string reason,
        OperatorCommandParameters parameters,
        bool recordCommand,
        CancellationToken cancellationToken)
    {
        vehicleId = _reconciliation?.ResolveCommandSource(vehicleId) ?? vehicleId;
        if (!_vehicles.TryGet(vehicleId, out var vehicle) || vehicle is null)
        {
            throw new KeyNotFoundException($"Vehicle '{vehicleId}' was not found.");
        }

        var telemetry = LatestTelemetry(vehicle.Id);
        var connection = ResolveConnection(vehicle);
        if (connection?.Mode == ConnectionMode.TeamObserver)
        {
            throw new InvalidOperationException("This unit is mirrored from another Robot Command and is read-only.");
        }
        var backend = BackendName(connection);
        var policySource = PolicySource(connection);
        var now = DateTimeOffset.UtcNow;
        var commandId = $"cmd-{Guid.NewGuid():N}";
        var correlationId = $"robot-command-{Guid.NewGuid():N}";
        var idempotencyKey = $"operator-{Guid.NewGuid():N}";
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "Operator request" : reason.Trim();
        var normalizedParameters = command is OperatorCommandKind.Takeoff or
            OperatorCommandKind.GoTo or
            OperatorCommandKind.ChangeAltitude or
            OperatorCommandKind.SetHeading
            ? parameters ?? OperatorCommandParameters.None
            : OperatorCommandParameters.None;
        var target = new OperatorCommandTarget(
            connection?.Id ?? vehicle.ConnectionIds.FirstOrDefault() ?? string.Empty,
            vehicle.Id,
            vehicle.LogosInstanceId,
            now);
        var findings = OperatorControlRules.Evaluate(
            vehicle,
            telemetry,
            connection,
            command,
            normalizedParameters,
            LatestDiagnostics(vehicle.Id, connection?.Id)).ToList();
        if (string.Equals(_manualControl?.ActiveVehicleId, vehicle.Id, StringComparison.Ordinal) &&
            _manualControl?.IsManualSubmission != true)
        {
            findings.Add(new OperatorPreflightFinding(
                "MANUAL_CONTROL_ACTIVE", OperatorPreflightSeverity.Blocking,
                "Manual control owns this unit. Release manual control before queuing another operation.", "Manual control"));
        }
        var localBlocked = findings.Any(item => item.Severity == OperatorPreflightSeverity.Blocking);
        var policy = OperatorPolicyEvaluation.Unavailable("Policy preflight was not attempted.");

        if (!localBlocked && connection is not null)
        {
            try
            {
                var policyRequest = new OperatorPolicyRequest(
                    ActionFor(command),
                    vehicle.Id,
                    vehicle.LogosInstanceId,
                    correlationId,
                    BuildPolicyContext(
                        vehicle,
                        telemetry,
                        connection,
                        command,
                        normalizedReason,
                        normalizedParameters));
                policy = _ghosts?.IsGhostConnection(connection.Id) == true
                    ? _ghosts.EvaluatePolicy(policyRequest)
                    : await _connections.EvaluateOperatorPolicyAsync(
                        connection.Id, policyRequest, cancellationToken);
                AddPolicyFindings(policy, findings, policySource);
            }
            catch (Exception ex)
            {
                policy = OperatorPolicyEvaluation.Unavailable(ex.Message);
                findings.Add(new OperatorPreflightFinding(
                    "POLICY_PREFLIGHT_FAILED",
                    OperatorPreflightSeverity.Blocking,
                    $"{backend} policy preflight failed: {ex.Message}",
                    policySource));
            }
        }

        if (!_gateway.Status.Available)
        {
            findings.Add(new OperatorPreflightFinding(
                "OPERATOR_API_UNAVAILABLE",
                OperatorPreflightSeverity.Blocking,
                _gateway.Status.Message,
                backend));
        }

        PreparedVehicleOperation? serverPreparation = null;
        if (_gateway.Status.Available &&
            connection is not null &&
            findings.All(item => item.Severity != OperatorPreflightSeverity.Blocking))
        {
            var preparation = await _gateway.PrepareAsync(
                new OperatorCommandRequest(
                    commandId,
                    correlationId,
                    idempotencyKey,
                    command,
                    target,
                    normalizedReason,
                    OperatorControlRules.SafetyFor(command, telemetry) == OperatorCommandSafety.Critical,
                    now,
                    Parameters: normalizedParameters),
                cancellationToken);
            findings.AddRange(preparation.Findings);
            if (preparation.Accepted && preparation.Preparation is not null)
            {
                serverPreparation = preparation.Preparation;
            }
            else if (preparation.Findings.Count == 0)
            {
                findings.Add(new OperatorPreflightFinding(
                    "VEHICLE_OPERATION_PREPARE_REJECTED",
                    OperatorPreflightSeverity.Blocking,
                    preparation.Message,
                    backend));
            }
        }

        var availability = DetermineAvailability(findings, _gateway.Status.Available);
        var safety = OperatorControlRules.SafetyFor(command, telemetry);
        var confirmation = OperatorControlRules.ConfirmationPhrase(
            command,
            vehicle.Name,
            _configuration.RequireTypedOperatorConfirmation);
        var localExpiry = now.AddSeconds(_configuration.OperatorConfirmationTimeoutSeconds);
        var expiresAt = serverPreparation is null || serverPreparation.ExpiresAt >= localExpiry
            ? localExpiry
            : serverPreparation.ExpiresAt;
        var plan = new OperatorCommandPlan(
            commandId,
            correlationId,
            idempotencyKey,
            command,
            OperatorControlRules.DisplayName(command),
            safety,
            availability,
            target,
            vehicle.Name,
            normalizedReason,
            safety == OperatorCommandSafety.Critical,
            confirmation,
            findings,
            policy,
            now,
            expiresAt,
            serverPreparation,
            normalizedParameters);

        if (recordCommand)
        {
            UpsertCommand(new OperationalCommandRecord(
            commandId,
            command.ToString(),
            "Vehicle",
            vehicle.Id,
            target.ConnectionId,
            OperationalCommandState.Draft,
            $"{plan.DisplayName} {vehicle.Name}",
            availability switch
            {
                OperatorControlAvailability.Ready => $"Prepared by {backend} and ready for confirmation.",
                OperatorControlAvailability.Warning => $"Prepared by {backend} with warnings.",
                OperatorControlAvailability.Unavailable => _gateway.Status.Message,
                _ => $"Blocked by operator preflight or {backend} readiness."
            },
            correlationId,
            now,
            now,
            vehicle.Id,
            vehicle.LogosInstanceId,
            idempotencyKey,
            serverPreparation?.AuthorizationDecision ?? policy.Decision,
            normalizedReason,
            plan.Emergency));
        }

        return plan;
    }

    public async Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.CanSubmit)
        {
            var blocker = plan.Findings.FirstOrDefault(item => item.Severity == OperatorPreflightSeverity.Blocking);
            return await RejectAsync(
                plan,
                blocker is null
                    ? "The command is not eligible for submission. Review the preflight findings."
                    : blocker.Message,
                cancellationToken);
        }

        if (DateTimeOffset.UtcNow > plan.ExpiresAt || plan.Preparation is null || plan.Preparation.Expired)
        {
            return await RejectAsync(plan, "The confirmation window expired. Prepare the command again.", cancellationToken);
        }

        if (!_gateway.Status.Available)
        {
            return await RejectAsync(plan, _gateway.Status.Message, cancellationToken);
        }

        if (!_vehicles.TryGet(plan.Target.VehicleId, out var currentVehicle) || currentVehicle is null)
        {
            return await RejectAsync(plan, "The target vehicle is no longer available.", cancellationToken);
        }

        if (!PreparedTargetMatches(plan, currentVehicle))
        {
            return await RejectAsync(
                plan,
                "The runtime or vehicle identity changed after preparation.",
                cancellationToken);
        }

        var currentConnection = ResolveConnection(currentVehicle, plan.Target.ConnectionId);
        var backend = BackendName(currentConnection);
        var currentTelemetry = LatestTelemetry(currentVehicle.Id);
        var currentFindings = OperatorControlRules.Evaluate(
            currentVehicle,
            currentTelemetry,
            currentConnection,
            plan.Command,
            plan.Parameters,
            LatestDiagnostics(currentVehicle.Id, currentConnection?.Id));
        if (currentFindings.Any(item => item.Severity == OperatorPreflightSeverity.Blocking) ||
            currentConnection is null)
        {
            var blocker = currentFindings.FirstOrDefault(item => item.Severity == OperatorPreflightSeverity.Blocking);
            return await RejectAsync(
                plan,
                blocker?.Message ??
                (currentConnection is null
                    ? "The vehicle connection is no longer available."
                    : "Vehicle state changed after confirmation; local preflight now blocks the command."),
                cancellationToken);
        }

        try
        {
            var policyRequest = new OperatorPolicyRequest(
                ActionFor(plan.Command),
                currentVehicle.Id,
                currentVehicle.LogosInstanceId,
                plan.CorrelationId,
                BuildPolicyContext(
                    currentVehicle,
                    currentTelemetry,
                    currentConnection,
                    plan.Command,
                    plan.Reason,
                    plan.Parameters));
            var policy = _ghosts?.IsGhostConnection(currentConnection.Id) == true
                ? _ghosts.EvaluatePolicy(policyRequest)
                : await _connections.EvaluateOperatorPolicyAsync(
                    currentConnection.Id, policyRequest, cancellationToken);
            UpdateCommandPolicy(plan.CommandId, policy.Decision);
            if (!policy.Evaluated || !policy.Allowed)
            {
                return await RejectAsync(
                    plan,
                    string.IsNullOrWhiteSpace(policy.Summary)
                        ? $"{backend} policy no longer permits the command."
                        : policy.Summary,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            return await RejectAsync(
                plan,
                $"{backend} policy revalidation failed: {ex.Message}",
                cancellationToken);
        }

        UpdateCommand(plan.CommandId, OperationalCommandState.Submitting, $"Submitting prepared operation to {backend}...");
        try
        {
            var result = await _gateway.ExecuteAsync(
                new OperatorCommandRequest(
                    plan.CommandId,
                    plan.CorrelationId,
                    plan.IdempotencyKey,
                    plan.Command,
                    plan.Target,
                    plan.Reason,
                    plan.Emergency,
                    plan.CreatedAt,
                    plan.Preparation,
                    plan.Parameters),
                cancellationToken);
            UpdateCommand(plan.CommandId, result.State, result.Message);
            return result;
        }
        catch (Exception ex)
        {
            UpdateCommand(plan.CommandId, OperationalCommandState.Failed, ex.Message);
            throw;
        }
    }

    public Task CancelAsync(
        OperatorCommandPlan plan,
        string message = "Cancelled before submission.",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateCommand(plan.CommandId, OperationalCommandState.Cancelled, message);
        return Task.CompletedTask;
    }

    private Task<OperatorCommandResult> RejectAsync(
        OperatorCommandPlan plan,
        string message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateCommand(plan.CommandId, OperationalCommandState.Rejected, message);
        return Task.FromResult(new OperatorCommandResult(
            false,
            OperationalCommandState.Rejected,
            message));
    }

    private VehicleTelemetryRecord? LatestTelemetry(string vehicleId)
        => _telemetry.Items
            .Where(item => string.Equals(item.VehicleId, vehicleId, StringComparison.Ordinal))
            .OrderByDescending(item => item.ObservedAt)
            .FirstOrDefault();

    private VehicleDiagnosticsSnapshot? LatestDiagnostics(string vehicleId, string? connectionId)
        => _diagnostics?.Items
            .Where(item => string.Equals(item.VehicleId, vehicleId, StringComparison.Ordinal) &&
                           (string.IsNullOrWhiteSpace(connectionId) ||
                            string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal)))
            .OrderByDescending(item => item.ObservedAt)
            .FirstOrDefault();

    private ConnectionRecord? ResolveConnection(VehicleRecord vehicle, string? preferredConnectionId = null)
    {
        var ids = vehicle.ConnectionIds;
        if (!string.IsNullOrWhiteSpace(preferredConnectionId) &&
            ids.Contains(preferredConnectionId, StringComparer.Ordinal) &&
            _connectionStore.TryGet(preferredConnectionId, out var preferred) &&
            preferred is not null)
        {
            return preferred;
        }

        return _connectionStore.Items
            .Where(item => ids.Contains(item.Id, StringComparer.Ordinal))
            .OrderByDescending(item => ConnectionRank(item.State))
            .ThenByDescending(item => item.LastSeen)
            .FirstOrDefault();
    }

    private static bool PreparedTargetMatches(OperatorCommandPlan plan, VehicleRecord currentVehicle)
    {
        var target = plan.Preparation?.Target;
        if (target is null) return false;
        if (!string.IsNullOrWhiteSpace(target.VehicleId) &&
            !string.Equals(target.VehicleId, currentVehicle.Id, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(target.LogosInstanceId) ||
               string.Equals(target.LogosInstanceId, currentVehicle.LogosInstanceId, StringComparison.Ordinal);
    }

    private static int ConnectionRank(AvailabilityState state)
        => state switch
        {
            AvailabilityState.Online => 5,
            AvailabilityState.Degraded => 4,
            AvailabilityState.Stale => 3,
            AvailabilityState.Reconnecting => 2,
            AvailabilityState.Connecting => 1,
            _ => 0
        };

    private static string ActionFor(OperatorCommandKind command)
        => command switch
        {
            OperatorCommandKind.Recover => "vehicle.operator.return_home",
            OperatorCommandKind.GoTo => "vehicle.operator.go_to",
            OperatorCommandKind.ChangeAltitude => "vehicle.operator.change_altitude",
            OperatorCommandKind.SetHeading => "vehicle.operator.set_heading",
            _ => $"vehicle.operator.{command.ToString().ToLowerInvariant()}"
        };

    private static string BuildPolicyContext(
        VehicleRecord vehicle,
        VehicleTelemetryRecord? telemetry,
        ConnectionRecord connection,
        OperatorCommandKind command,
        string reason,
        OperatorCommandParameters? parameters)
        => JsonSerializer.Serialize(new
        {
            source = "logos-robot-command",
            command = command.ToString(),
            reason,
            connection_id = connection.Id,
            vehicle_id = vehicle.Id,
            logos_instance_id = vehicle.LogosInstanceId,
            team_id = vehicle.TeamId,
            vehicle_domain = vehicle.Domain,
            vehicle_class = vehicle.VehicleClass,
            vehicle_state = vehicle.State.ToString(),
            readiness = telemetry?.Readiness ?? vehicle.Readiness,
            armed = telemetry?.Armed,
            landed_state = telemetry?.LandedState,
            telemetry_observed_at = telemetry?.ObservedAt,
            telemetry_stale = telemetry?.IsStale,
            takeoff_altitude_agl_m = parameters?.TakeoffAltitudeAglMetres,
            go_to_target_kind = parameters?.GoToTargetKind?.ToString(),
            go_to_latitude_deg = parameters?.GoToLatitudeDegrees,
            go_to_longitude_deg = parameters?.GoToLongitudeDegrees,
            go_to_altitude_amsl_m = parameters?.GoToAltitudeAmslMetres,
            go_to_north_m = parameters?.GoToNorthMetres,
            go_to_east_m = parameters?.GoToEastMetres,
            go_to_down_m = parameters?.GoToDownMetres,
            go_to_yaw_deg = parameters?.GoToYawDegrees,
            go_to_acceptance_radius_m = parameters?.GoToAcceptanceRadiusMetres,
            altitude_target_kind = parameters?.AltitudeTargetKind?.ToString(),
            altitude_amsl_m = parameters?.AltitudeAmslMetres,
            altitude_agl_m = parameters?.AltitudeAglMetres,
            altitude_relative_delta_m = parameters?.AltitudeRelativeDeltaMetres,
            heading_target_kind = parameters?.HeadingTargetKind?.ToString(),
            heading_deg = parameters?.HeadingDegrees,
            relative_yaw_deg = parameters?.RelativeYawDegrees
        });

    private static void AddPolicyFindings(
        OperatorPolicyEvaluation policy,
        ICollection<OperatorPreflightFinding> findings,
        string source)
    {
        if (!policy.Evaluated)
        {
            findings.Add(new OperatorPreflightFinding(
                "POLICY_NOT_EVALUATED",
                OperatorPreflightSeverity.Blocking,
                policy.Summary,
                source));
            return;
        }

        if (!policy.Allowed)
        {
            findings.Add(new OperatorPreflightFinding(
                "POLICY_DENIED",
                OperatorPreflightSeverity.Blocking,
                string.IsNullOrWhiteSpace(policy.Summary) ? "Policy denied the command." : policy.Summary,
                source));
        }
        else if (string.Equals(policy.Decision, "Warn", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new OperatorPreflightFinding(
                "POLICY_WARNING",
                OperatorPreflightSeverity.Warning,
                policy.Summary,
                source));
        }
        else
        {
            findings.Add(new OperatorPreflightFinding(
                "POLICY_ALLOWED",
                OperatorPreflightSeverity.Info,
                string.IsNullOrWhiteSpace(policy.Summary) ? "Policy allowed the command." : policy.Summary,
                source));
        }

        foreach (var item in policy.Findings)
        {
            findings.Add(new OperatorPreflightFinding(
                string.IsNullOrWhiteSpace(item.Code) ? "POLICY_FINDING" : item.Code,
                MapPolicySeverity(item.Severity, policy.Allowed),
                item.Message,
                source));
        }
    }

    private static string BackendName(ConnectionRecord? connection)
        => connection?.Mode switch
        {
            ConnectionMode.Mavlink => "MAVLink autopilot",
            ConnectionMode.Ghost => "ghost simulator",
            ConnectionMode.FieldLink => "Logos LinkD ground link",
            _ => "Logos"
        };

    private static string PolicySource(ConnectionRecord? connection)
        => connection?.Mode switch
        {
            ConnectionMode.Mavlink => "Robot Command MAVLink policy",
            ConnectionMode.Ghost => "Ghost simulator policy",
            ConnectionMode.FieldLink => "LinkD operations unavailable",
            _ => "Logos PolicyService"
        };

    private static OperatorPreflightSeverity MapPolicySeverity(string severity, bool allowed)
    {
        if (!allowed || severity.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            severity.Contains("critical", StringComparison.OrdinalIgnoreCase))
        {
            return OperatorPreflightSeverity.Blocking;
        }

        return severity.Contains("warn", StringComparison.OrdinalIgnoreCase)
            ? OperatorPreflightSeverity.Warning
            : OperatorPreflightSeverity.Info;
    }

    private static OperatorControlAvailability DetermineAvailability(
        IReadOnlyCollection<OperatorPreflightFinding> findings,
        bool gatewayAvailable)
    {
        if (!gatewayAvailable)
        {
            return OperatorControlAvailability.Unavailable;
        }

        if (findings.Any(item => item.Severity == OperatorPreflightSeverity.Blocking))
        {
            return OperatorControlAvailability.Blocked;
        }

        return findings.Any(item => item.Severity == OperatorPreflightSeverity.Warning)
            ? OperatorControlAvailability.Warning
            : OperatorControlAvailability.Ready;
    }

    private void UpdateCommandPolicy(string commandId, string decision)
    {
        if (_commands.TryGet(commandId, out var existing) && existing is not null)
        {
            UpsertCommand(existing with
            {
                PolicyDecision = decision,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private void UpdateCommand(
        string commandId,
        OperationalCommandState state,
        string message)
    {
        if (_commands.TryGet(commandId, out var existing) && existing is not null)
        {
            UpsertCommand(existing with
            {
                State = state,
                Message = message,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private void UpsertCommand(OperationalCommandRecord command)
    {
        _commands.Upsert(command);
        var overflow = _commands.Items.Count - _configuration.MaxCommandHistory;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var item in _commands.Items
                     .OrderBy(record => record.CreatedAt)
                     .Take(overflow)
                     .ToArray())
        {
            _commands.Remove(item.Id);
        }
    }
}
