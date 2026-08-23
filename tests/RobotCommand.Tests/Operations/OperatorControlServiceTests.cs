using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Operations;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperatorControlServiceTests
{
    [Fact]
    public async Task ManualControlReadiness_UsesSharedConnectionAndTelemetryChecks_NotDisplayHealth()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Vehicles.TryGet("vehicle-1", out var vehicle));
        Assert.True(fixture.Telemetry.TryGet("connection-1:vehicle-1", out var telemetry));
        Assert.True(fixture.Connections.TryGet("connection-1", out var connection));

        // PX4 projects diagnostic status values (for example, Ready) into the
        // display health field. Manual-control eligibility must use the shared
        // preflight, not require the Logos-specific word "Healthy".
        fixture.Connections.Upsert(connection! with { Mode = ConnectionMode.Mavlink });
        fixture.Vehicles.Upsert(vehicle! with { Health = "Ready" });
        fixture.Telemetry.Upsert(telemetry! with { Health = "Ready" });

        var readiness = await fixture.Service.GetManualControlReadinessAsync("vehicle-1");

        Assert.True(readiness.IsReady);
        Assert.Contains(readiness.Findings, item => item.Code == "MANUAL_CONTROL_PREFLIGHT_OK");
    }

    [Fact]
    public async Task ManualControlReadiness_WarnsWhenSharedTelemetryPreflightIsStale()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Telemetry.TryGet("connection-1:vehicle-1", out var telemetry));
        fixture.Telemetry.Upsert(telemetry! with { IsStale = true, State = AvailabilityState.Stale });

        var readiness = await fixture.Service.GetManualControlReadinessAsync("vehicle-1");

        Assert.True(readiness.IsReady);
        Assert.Contains(readiness.Findings, item =>
            item.Code == "TELEMETRY_STALE" &&
            item.Severity == OperatorPreflightSeverity.Warning);
    }

    [Fact]
    public async Task ManualControlReadiness_AllowsDegradedConnectionAsWarning()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Connections.TryGet("connection-1", out var connection));
        fixture.Connections.Upsert(connection! with { State = AvailabilityState.Degraded });

        var readiness = await fixture.Service.GetManualControlReadinessAsync("vehicle-1");

        Assert.True(readiness.IsReady);
        Assert.Contains(readiness.Findings, item =>
            item.Code == "CONNECTION_DEGRADED" &&
            item.Severity == OperatorPreflightSeverity.Warning);
    }

    [Fact]
    public async Task PrepareAndExecute_CapturesImmutableVehicleRouteAndServerPreparation()
    {
        var fixture = new Fixture(new RecordingGateway(available: true));

        var plan = await fixture.Service.PrepareAsync(
            "vehicle-1",
            OperatorCommandKind.Arm,
            "Bench readiness check");
        var result = await fixture.Service.ExecuteAsync(plan);

        Assert.True(plan.CanSubmit);
        Assert.NotNull(plan.Preparation);
        Assert.True(result.Accepted);
        Assert.NotNull(fixture.Gateway.LastPreparedRequest);
        Assert.NotNull(fixture.Gateway.LastRequest);
        Assert.Equal("connection-1", fixture.Gateway.LastRequest!.Target.ConnectionId);
        Assert.Equal("vehicle-1", fixture.Gateway.LastRequest.Target.VehicleId);
        Assert.Equal("logos-1", fixture.Gateway.LastRequest.Target.LogosInstanceId);
        Assert.Equal(plan.IdempotencyKey, fixture.Gateway.LastRequest.IdempotencyKey);
        Assert.Equal(
            plan.Preparation!.Reference,
            fixture.Gateway.LastRequest.Preparation!.Reference);
        Assert.True(fixture.Commands.TryGet(plan.CommandId, out var command));
        Assert.Equal(OperationalCommandState.Accepted, command!.State);
        Assert.Equal("Allow", command.PolicyDecision);
    }

    [Fact]
    public async Task Assess_RevalidatesWithoutAddingACommandHistoryRecord()
    {
        var fixture = new Fixture(new RecordingGateway(available: true));

        var assessment = await fixture.Service.AssessAsync(
            "vehicle-1",
            OperatorCommandKind.Arm,
            "Queued operator request",
            OperatorCommandParameters.None);

        Assert.True(assessment.CanSubmit);
        Assert.Empty(fixture.Commands.Items);
    }

    [Fact]
    public async Task Takeoff_PreparationCarriesAltitudeToGateway()
    {
        var fixture = new Fixture(new RecordingGateway(available: true));
        fixture.Telemetry.Upsert(new VehicleTelemetryRecord(
            "connection-1:vehicle-1",
            "vehicle-1",
            "connection-1",
            "logos-1",
            AvailabilityState.Online,
            true,
            "Landed",
            "Multicopter",
            "Idle",
            "Healthy",
            "Ready",
            43.0,
            -79.0,
            100,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            "OK",
            string.Empty,
            DateTimeOffset.UtcNow.AddMilliseconds(1)));

        var plan = await fixture.Service.PrepareAsync(
            "vehicle-1",
            OperatorCommandKind.Takeoff,
            "SITL takeoff",
            new OperatorCommandParameters(6));

        Assert.True(plan.CanSubmit);
        Assert.Equal(6, plan.Parameters?.TakeoffAltitudeAglMetres);
        Assert.Equal(6, fixture.Gateway.LastPreparedRequest?.Parameters?.TakeoffAltitudeAglMetres);
    }

    [Fact]
    public async Task GoTo_PreparationCarriesGlobalTargetToGateway()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Telemetry.TryGet("connection-1:vehicle-1", out var telemetry));
        fixture.Telemetry.Upsert(telemetry! with { Armed = true, LandedState = "Flying" });
        var parameters = OperatorCommandParameters.GlobalGoTo(43.65, -79.38, 130, 2.5);

        var plan = await fixture.Service.PrepareAsync(
            "vehicle-1",
            OperatorCommandKind.GoTo,
            "Divert to target",
            parameters);

        Assert.Equal(OperatorCommandKind.GoTo, plan.Command);
        Assert.Equal(43.65, plan.Parameters?.GoToLatitudeDegrees);
        Assert.Equal(-79.38, plan.Parameters?.GoToLongitudeDegrees);
        Assert.Equal(130, plan.Parameters?.GoToAltitudeAmslMetres);
        Assert.Equal(2.5, plan.Parameters?.GoToAcceptanceRadiusMetres);
        Assert.Equal(parameters, fixture.Gateway.LastPreparedRequest?.Parameters);
    }

    [Fact]
    public async Task AltitudeAndHeading_PreparationCarryTypedParametersToGateway()
    {
        var fixture = new Fixture();
        fixture.Telemetry.Upsert(AirborneTelemetry());

        var altitude = OperatorCommandParameters.ChangeAltitudeRelative(12);
        var altitudePlan = await fixture.Service.PrepareAsync(
            "vehicle-1",
            OperatorCommandKind.ChangeAltitude,
            "Climb above obstruction",
            altitude);

        Assert.True(altitudePlan.CanSubmit);
        Assert.Equal(altitude, altitudePlan.Parameters);
        Assert.Equal(altitude, fixture.Gateway.LastPreparedRequest?.Parameters);

        var heading = OperatorCommandParameters.AbsoluteHeading(225);
        var headingPlan = await fixture.Service.PrepareAsync(
            "vehicle-1",
            OperatorCommandKind.SetHeading,
            "Face the inspection target",
            heading);

        Assert.True(headingPlan.CanSubmit);
        Assert.Equal(heading, headingPlan.Parameters);
        Assert.Equal(heading, fixture.Gateway.LastPreparedRequest?.Parameters);
    }

    [Fact]
    public async Task CancellingPreparedCommand_UsesCancelledState()
    {
        var fixture = new Fixture();

        var plan = await fixture.Service.PrepareAsync(
            "vehicle-1",
            OperatorCommandKind.ChangeAltitude,
            "Climb");
        await fixture.Service.CancelAsync(plan, "Replaced by a newer command.");

        Assert.True(fixture.Commands.TryGet(plan.CommandId, out var command));
        Assert.Equal(OperationalCommandState.Cancelled, command!.State);
        Assert.Equal("Replaced by a newer command.", command.Message);
    }

    [Fact]
    public async Task Prepare_ReportsUnavailable_WhenSdkGatewayIsMissing()
    {
        var fixture = new Fixture(new RecordingGateway(available: false));

        var plan = await fixture.Service.PrepareAsync(
            "vehicle-1",
            OperatorCommandKind.Arm,
            "Operator request");

        Assert.Equal(OperatorControlAvailability.Unavailable, plan.Availability);
        Assert.False(plan.CanSubmit);
        Assert.Null(plan.Preparation);
        Assert.Contains(plan.Findings, item => item.Code == "OPERATOR_API_UNAVAILABLE");
        Assert.True(fixture.Commands.TryGet(plan.CommandId, out var command));
        Assert.Equal(OperationalCommandState.Draft, command!.State);
    }

    [Fact]
    public async Task Prepare_IsBlocked_WhenLogosReadinessRejectsOperation()
    {
        var fixture = new Fixture(new RecordingGateway(available: true, rejectPreparation: true));

        var plan = await fixture.Service.PrepareAsync(
            "vehicle-1",
            OperatorCommandKind.Arm,
            "Operator request");

        Assert.Equal(OperatorControlAvailability.Blocked, plan.Availability);
        Assert.False(plan.CanSubmit);
        Assert.Null(plan.Preparation);
        Assert.Contains(plan.Findings, item => item.Code == "TEST_NOT_READY");
    }

    private static VehicleTelemetryRecord AirborneTelemetry()
        => new(
            "connection-1:vehicle-1",
            "vehicle-1",
            "connection-1",
            "logos-1",
            AvailabilityState.Online,
            true,
            "InAir",
            "Multicopter",
            "Offboard",
            "Healthy",
            "Ready",
            43.0,
            -79.0,
            120,
            20,
            0,
            0,
            -20,
            0,
            0,
            0,
            0,
            false,
            "OK",
            string.Empty,
            DateTimeOffset.UtcNow.AddMilliseconds(1));

    private sealed class Fixture
    {
        public Fixture(RecordingGateway? gateway = null)
        {
            Gateway = gateway ?? new RecordingGateway(available: true);
            Connections.Upsert(new ConnectionRecord(
                "connection-1",
                "Local Logos",
                "http://localhost:50051",
                ConnectionMode.Direct,
                AvailabilityState.Online,
                true,
                "logos-1",
                "unit",
                DateTimeOffset.UtcNow));
            Vehicles.Upsert(new VehicleRecord(
                "vehicle-1",
                "Dracula",
                ["connection-1"],
                "logos-1",
                null,
                "Multicopter",
                "Air",
                "default",
                AvailabilityState.Online,
                Readiness: "Ready",
                Lifecycle: "Idle",
                ArmState: "Disarmed",
                Health: "Healthy",
                CapabilityKeys: ["operator_control"],
                LastSeen: DateTimeOffset.UtcNow));
            Telemetry.Upsert(new VehicleTelemetryRecord(
                "connection-1:vehicle-1",
                "vehicle-1",
                "connection-1",
                "logos-1",
                AvailabilityState.Online,
                false,
                "Landed",
                "Multicopter",
                "Idle",
                "Healthy",
                "Ready",
                43.0,
                -79.0,
                100,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                "OK",
                string.Empty,
                DateTimeOffset.UtcNow));

            Service = new OperatorControlService(
                new AppConfiguration
                {
                    Connections = [],
                    RequireTypedOperatorConfirmation = true
                },
                Gateway,
                new PolicyAllowingConnectionManager(),
                Connections,
                Vehicles,
                Telemetry,
                Commands);
        }

        public RecordingGateway Gateway { get; }
        public EntityStore<string, ConnectionRecord> Connections { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleRecord> Vehicles { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleTelemetryRecord> Telemetry { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, OperationalCommandRecord> Commands { get; } = new(item => item.Id, StringComparer.Ordinal);
        public OperatorControlService Service { get; }
    }

    private sealed class RecordingGateway(bool available, bool rejectPreparation = false) : IOperatorCommandGateway
    {
        public OperatorGatewayStatus Status { get; } = new(
            available,
            available ? "Available" : "Operator API unavailable");

        public OperatorCommandRequest? LastPreparedRequest { get; private set; }
        public OperatorCommandRequest? LastRequest { get; private set; }

        public Task<OperatorCommandPreparationResult> PrepareAsync(
            OperatorCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPreparedRequest = request;
            if (rejectPreparation)
            {
                return Task.FromResult(new OperatorCommandPreparationResult(
                    false,
                    "Not ready",
                    null,
                    [new OperatorPreflightFinding(
                        "TEST_NOT_READY",
                        OperatorPreflightSeverity.Blocking,
                        "Test gateway reports not ready.",
                        "Test")]));
            }

            var prepared = new PreparedVehicleOperation(
                new PreparedOperationReference("prepare-1", "token-1"),
                new PreparedOperationTargetSnapshot(
                    request.Target.LogosInstanceId,
                    request.Target.VehicleId,
                    "binding-1",
                    null,
                    null,
                    null,
                    null,
                    7),
                "vehicle.arm",
                "Allowed",
                "Ready",
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(1));
            return Task.FromResult(new OperatorCommandPreparationResult(
                true,
                "Prepared",
                prepared,
                []));
        }

        public Task<OperatorCommandResult> ExecuteAsync(
            OperatorCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(new OperatorCommandResult(
                true,
                OperationalCommandState.Accepted,
                "Accepted by test gateway"));
        }
    }

    private sealed class PolicyAllowingConnectionManager : ILogosConnectionManager
    {
        public IReadOnlyList<ConnectionDefinition> Definitions => [];

        public bool TryGetDefinition(string connectionId, out ConnectionDefinition? definition)
        {
            definition = null;
            return false;
        }

        public Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
            string connectionId,
            OperatorPolicyRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OperatorPolicyEvaluation(
                true,
                true,
                "Allow",
                "Allowed by test policy",
                Array.Empty<OperatorPolicyFinding>()));

        public Task RegisterAsync(ConnectionDefinition definition, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(ConnectionDefinition definition, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectAsync(string connectionId, ConnectionCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CameraStreamRecord> OpenCameraStreamAsync(string connectionId, CameraStreamOpenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseCameraStreamAsync(string connectionId, string streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAutoConnectionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SuperviseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
