using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class MavlinkConnectionTests
{
    [Fact]
    public async Task AuxiliaryAckBetweenTelemetryFramesDoesNotCountAsPacketLoss()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1) with { Sequence = 10 });

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        transport.Emit(CommandAck(1, MavlinkCommandIds.MissionStart, MavlinkValues.MavResultAccepted) with { Sequence = 11 });
        transport.Emit(GlobalPosition(1, 47.0, 8.0, 500, 10) with { Sequence = 12 });

        var link = Assert.Single(connection.LiveSnapshot.Links);
        Assert.Equal(0d, link.PacketLoss);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task FreshNonHeartbeatTrafficKeepsVehicleOnlineWhenHeartbeatIsDelayed()
    {
        var reference = DateTimeOffset.UtcNow;
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1) with { ReceivedAt = reference.AddSeconds(-5) });

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 10) with { ReceivedAt = reference });
        connection.EvaluateFreshness(reference.AddSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));

        var telemetry = Assert.Single(connection.LiveSnapshot.Telemetry);
        Assert.Equal(AvailabilityState.Online, telemetry.State);
        Assert.Equal(reference, connection.LastSeen);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task OperatorCommandTimeoutReturnsStructuredResultInsteadOfEscaping()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "arm-command",
                "correlation",
                "idempotency",
                OperatorCommandKind.Arm,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "test",
                false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Equal(OperationalCommandState.TimedOut, result.State);
        Assert.Contains("no MAVLink acknowledgement", result.Message, StringComparison.OrdinalIgnoreCase);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task TakeoffAuditCapturesDispatchAckModeAndLandedStateWithoutAutomaticHold()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () =>
        {
            transport.Emit(Heartbeat(1));
            transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 0));
            transport.Emit(ExtendedSystemState(1, MavlinkValues.MavLandedStateOnGround));
        };
        var commandSends = 0;
        transport.OnSend = payload =>
        {
            if (payload is [2])
            {
                commandSends++;
                transport.Emit(CommandAck(1, MavlinkCommandIds.NavTakeoff, MavlinkValues.MavResultAccepted));
            }
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        commandSends = 0;
        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "takeoff-audit",
                "correlation",
                "idempotency",
                OperatorCommandKind.Takeoff,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "Xbox controller",
                false,
                DateTimeOffset.UtcNow,
                Parameters: new OperatorCommandParameters(TakeoffAltitudeAglMetres: 10)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Accepted, result.Message);
        transport.Emit(Heartbeat(1, baseMode: MavlinkValues.MavModeFlagCustomModeEnabled | MavlinkValues.MavModeFlagSafetyArmed, customMode: (4u << 16) | (2u << 24)));
        transport.Emit(ExtendedSystemState(1, MavlinkValues.MavLandedStateInAir));
        transport.Emit(GlobalPosition(1, 43.7, -79.4, 510, 10));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, commandSends);
        Assert.Contains(connection.LiveSnapshot.Events, item =>
            item.Code == "MAVLINK_COMMAND_DISPATCH" &&
            item.SubjectId == "takeoff-audit" &&
            item.Message.Contains("Xbox controller", StringComparison.Ordinal));
        Assert.Contains(connection.LiveSnapshot.Events, item =>
            item.Code == "MAVLINK_COMMAND_ACK" &&
            item.SubjectId == "takeoff-audit" &&
            item.Message.Contains("Accepted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(connection.LiveSnapshot.Events, item => item.Code == "MAVLINK_MODE_CHANGED" && item.Message.Contains("Takeoff", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(connection.LiveSnapshot.Events, item => item.Code == "MAVLINK_LANDED_STATE_CHANGED" && item.Message.Contains("Flying", StringComparison.OrdinalIgnoreCase));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task NoAcknowledgementCanLaterBeConfirmedByAltitudeTelemetry()
    {
        var transport = new FakeTransport();
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var connection = CreateConnection(transport, commands: commands);
        transport.OnOpen = () =>
        {
            transport.Emit(Heartbeat(1));
            transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 10));
        };
        commands.Upsert(new OperationalCommandRecord(
            "altitude-command",
            "ChangeAltitude",
            "Vehicle",
            "mavlink:mavlink:1",
            "mavlink",
            OperationalCommandState.InProgress,
            "Change altitude",
            "Change altitude pending",
            "correlation",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "altitude-command",
                "correlation",
                "idempotency",
                OperatorCommandKind.ChangeAltitude,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "test",
                false,
                DateTimeOffset.UtcNow,
                Parameters: OperatorCommandParameters.ChangeAltitudeAgl(30)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Equal(OperationalCommandState.TimedOut, result.State);
        Assert.Contains("Monitoring telemetry", result.Message, StringComparison.OrdinalIgnoreCase);

        // The post-dispatch heartbeat establishes a fresh sample, while the
        // position sample reaches the requested MSL altitude (500 - 10 + 30).
        transport.Emit(Heartbeat(1));
        transport.Emit(GlobalPosition(1, 43.7, -79.4, 520, 30));

        Assert.Equal(OperationalCommandState.Succeeded, commands.Items.Single().State);
        Assert.Contains("no MAVLink acknowledgement", commands.Items.Single().Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("telemetry indicates", commands.Items.Single().Message, StringComparison.OrdinalIgnoreCase);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task OneUdpConnection_DiscoversMultiplePx4Systems()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));

        await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);
        transport.Emit(Heartbeat(2));

        Assert.Equal(2, connection.Observations.Count);
        Assert.Equal(["PX4 System 1", "PX4 System 2"],
            connection.Observations.Select(item => item.Runtime.DisplayName).ToArray());
        Assert.All(connection.Observations, item =>
            Assert.Contains("go_to", item.Runtime.CapabilityKeys));

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task UdpBootstrapPeer_ReceivesInitialGcsHeartbeatBeforeVehicleDiscovery()
    {
        var transport = new FakeTransport();
        var connection = new MavlinkConnection(
            new ConnectionDefinition(
                "mavlink-bootstrap",
                "PX4 Docker SITL",
                "udp-listen://0.0.0.0:14550?bootstrap=127.0.0.1:18571",
                ConnectionMode.Mavlink,
                Mavlink: new MavlinkConnectionOptions()),
            transport,
            transport.Codec,
            [new Px4MavlinkAutopilotAdapter()],
            new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal),
            new ImmediateDispatcher(),
            new MavlinkConnectionRegistry(),
            NullLogger<MavlinkConnection>.Instance,
            TimeSpan.FromSeconds(1),
            new MavlinkTransportRoute("127.0.0.1:18571", new IPEndPoint(IPAddress.Loopback, 18571)));
        transport.OnSend = payload =>
        {
            if (payload is [1]) transport.Emit(Heartbeat(1));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);

        Assert.Contains([1], transport.SentPayloads);
        Assert.Contains(transport.SentRoutes, route => route.NativeRoute is IPEndPoint endpoint &&
            endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == 18571);
        await connection.DisposeAsync();
    }

    [Theory]
    [InlineData("udp-listen://0.0.0.0:14550?bootstrap=127.0.0.1:18571", "127.0.0.1", 18571)]
    [InlineData("udp-listen://0.0.0.0:14550", null, 0)]
    public void UdpBootstrapPeer_ParsesOnlyExplicitBootstrapEndpoint(string target, string? address, int port)
    {
        var endpoint = MavlinkConnectionProvider.ParseUdpBootstrapPeer(target);

        if (address is null)
        {
            Assert.Null(endpoint);
            return;
        }
        Assert.NotNull(endpoint);
        Assert.Equal(IPAddress.Parse(address), endpoint.Address);
        Assert.Equal(port, endpoint.Port);
    }

    [Fact]
    public async Task HeartbeatAlone_ProjectsLimitedVehicleDiagnostics()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));

        await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);

        var diagnostics = Assert.Single(connection.LiveSnapshot.VehicleDiagnostics);
        Assert.Equal(VehicleDiagnosticStatus.Limited, diagnostics.OverallStatus);
        Assert.NotEqual(VehicleDiagnosticStatus.Ready, diagnostics.ArmReadiness);
        Assert.NotEqual(VehicleDiagnosticStatus.Ready, diagnostics.NavigationReadiness);
        Assert.Equal("Limited", Assert.Single(connection.Observations).Vehicle!.Health);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task MissionUpload_RespondsToRequestedItemsAndTracksReachedProgress()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        transport.OnSend = payload =>
        {
            if (payload.SequenceEqual(new byte[] { 10 })) transport.Emit(MissionRequest(1, 0));
            if (payload.SequenceEqual(new byte[] { 11 })) transport.Emit(MissionAck(1, 0));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var upload = await connection.UploadMissionAsync(
            "mavlink:mavlink:1",
            [new MavlinkMissionItem(0, MavlinkCommandIds.NavTakeoff, 6, 0, 0, 20)],
            TestContext.Current.CancellationToken);

        Assert.True(upload.Succeeded, upload.Summary);
        Assert.Equal(new byte[] { 10 }, transport.SentPayloads[^2]);
        Assert.Equal(new byte[] { 11 }, transport.SentPayloads[^1]);

        transport.Emit(MissionItemReached(1, 0));
        Assert.True(connection.TryGetMissionProgress("mavlink:mavlink:1", out var index, out var updated));
        Assert.Equal(0, index);
        Assert.NotEqual(DateTimeOffset.MinValue, updated);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task MissionStart_UsesSetModeAndConfirmsMissionHeartbeat()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        transport.OnSend = payload =>
        {
            if (payload is [5])
                transport.Emit(Heartbeat(1, customMode: MavlinkValues.Px4AutoMissionCustomMode));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SetMissionModeAsync(
            "mavlink:mavlink:1",
            paused: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Contains(transport.SentPayloads, payload => payload is [15]);
        Assert.Contains(transport.SentPayloads, payload => payload is [5]);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotMissionStart_ConfirmsAutoThenSendsMissionStartCommand()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () => transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot, customMode: MavlinkValues.ArduPilotLoiterCustomMode));
        transport.OnSend = payload =>
        {
            if (payload is [5])
                transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot, customMode: MavlinkValues.ArduPilotAutoCustomMode));
            else if (payload is [2])
                transport.Emit(CommandAck(1, MavlinkCommandIds.MissionStart, MavlinkValues.MavResultAccepted));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SetMissionModeAsync(
            "mavlink:mavlink:1",
            paused: false,
            TestContext.Current.CancellationToken,
            restartFromBeginning: true,
            sendMissionStartCommand: true);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Contains(transport.SentPayloads, payload => payload is [15]);
        Assert.Contains(transport.SentPayloads, payload => payload is [5]);
        Assert.Contains(transport.SentPayloads, payload => payload is [2]);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotMissionUpload_UsesSharedMissionTransferAndReportsReadableAck()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () => transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot));
        transport.OnSend = payload =>
        {
            if (payload is [10]) transport.Emit(new MavlinkPacket(2, 2, 1, MavlinkValues.MavCompIdAutopilot1, MavlinkMessageIds.MissionRequest,
                new Dictionary<string, object> { ["seq"] = (ushort)0 }, DateTimeOffset.UtcNow));
            if (payload is [11]) transport.Emit(MissionAck(1, 0));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.UploadMissionAsync(
            "mavlink:mavlink:1",
            [new MavlinkMissionItem(0, MavlinkCommandIds.NavTakeoff, 6, 0, 0, 20)],
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Contains("Uploaded 1 mission items", result.Summary);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task StaleTelemetryCancelsActiveMovementAndRecordsSafetyReason()
    {
        var transport = new FakeTransport();
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot,
            commands: commands);
        transport.OnOpen = () =>
        {
            transport.Emit(Heartbeat(
                1,
                autopilot: MavlinkValues.MavAutopilotArduPilot,
                customMode: 4));
            transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 10));
        };
        transport.OnSend = payload =>
        {
            if (payload is [9])
                transport.Emit(CommandAck(1, MavlinkCommandIds.DoReposition, MavlinkValues.MavResultAccepted));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        commands.Upsert(new OperationalCommandRecord(
            "stale-go-to",
            "GoTo",
            "Vehicle",
            "mavlink:mavlink:1",
            "mavlink",
            OperationalCommandState.InProgress,
            "Go To",
            "Go To pending",
            "correlation",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "stale-go-to",
                "correlation",
                "idempotency",
                OperatorCommandKind.GoTo,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "test",
                false,
                DateTimeOffset.UtcNow,
                Parameters: OperatorCommandParameters.GlobalGoTo(43.7001, -79.4001, 510, 2)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Accepted, result.Message);
        connection.EvaluateFreshness(
            DateTimeOffset.UtcNow.AddSeconds(4),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10));

        var command = Assert.Single(commands.Items);
        Assert.Equal(OperationalCommandState.Cancelled, command.State);
        Assert.Equal("MAVLINK_TELEMETRY_LOSS", command.Reason);
        Assert.Contains("telemetry is stale", command.Message, StringComparison.OrdinalIgnoreCase);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task MissionTransfer_IgnoresRequestsFromTheWrongComponent()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        transport.OnSend = payload =>
        {
            if (payload is [10])
            {
                transport.Emit(new MavlinkPacket(
                    2,
                    2,
                    1,
                    42,
                    MavlinkMessageIds.MissionRequestInt,
                    new Dictionary<string, object> { ["seq"] = (ushort)0 },
                    DateTimeOffset.UtcNow));
            }
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.UploadMissionAsync(
            "mavlink:mavlink:1",
            [new MavlinkMissionItem(0, MavlinkCommandIds.NavTakeoff, 6, 0, 0, 20)],
            cancellation.Token));

        Assert.DoesNotContain(transport.SentPayloads, payload => payload is [11]);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotLivePreArmStatusTextBecomesAReadinessBlocker()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () =>
        {
            transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot));
            transport.Emit(StatusText(1, "PreArm: GPS not healthy"));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);

        var diagnostics = Assert.Single(connection.LiveSnapshot.VehicleDiagnostics);
        var blocker = Assert.Single(diagnostics.Blockers, item => item.Code.Contains("PREARM", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("PreArm: GPS not healthy", blocker.Detail);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotFailedArmAckUsesFreshPreArmStatusTextInsteadOfResultParam2()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () => transport.Emit(Heartbeat(
            1,
            autopilot: MavlinkValues.MavAutopilotArduPilot,
            customMode: 0));
        transport.OnSend = payload =>
        {
            if (payload is [2])
            {
                transport.Emit(CommandAck(1, MavlinkCommandIds.ComponentArmDisarm, MavlinkValues.MavResultFailed));
                transport.Emit(StatusText(1, "PreArm: GPS not healthy"));
            }
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "arm-command",
                "correlation",
                "idempotency",
                OperatorCommandKind.Arm,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "test",
                false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Contains("ArduPilot rejected Arm: PreArm: GPS not healthy", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed (0)", result.Message, StringComparison.OrdinalIgnoreCase);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotFailedArmAckWithoutStatusTextIncludesModeContext()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () => transport.Emit(Heartbeat(
            1,
            autopilot: MavlinkValues.MavAutopilotArduPilot,
            customMode: MavlinkValues.ArduPilotLandCustomMode));
        transport.OnSend = payload =>
        {
            if (payload is [2])
                transport.Emit(CommandAck(1, MavlinkCommandIds.ComponentArmDisarm, MavlinkValues.MavResultFailed));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "arm-command",
                "correlation",
                "idempotency",
                OperatorCommandKind.Arm,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "test",
                false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Contains("MAV_RESULT_FAILED", result.Message, StringComparison.Ordinal);
        Assert.Contains("The vehicle is currently in", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed (0)", result.Message, StringComparison.OrdinalIgnoreCase);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotArmFromLandedModeReturnsToStabilizeBeforeArm()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () =>
        {
            transport.Emit(Heartbeat(
                1,
                autopilot: MavlinkValues.MavAutopilotArduPilot,
                customMode: MavlinkValues.ArduPilotLandCustomMode));
            transport.Emit(ExtendedSystemState(1, MavlinkValues.MavLandedStateOnGround));
        };
        var stabilizeConfirmed = false;
        transport.OnSend = payload =>
        {
            if (payload is [5] && transport.Codec.LastCustomMode == MavlinkValues.ArduPilotStabilizeCustomMode)
            {
                stabilizeConfirmed = true;
                transport.Emit(Heartbeat(
                    1,
                    autopilot: MavlinkValues.MavAutopilotArduPilot,
                    customMode: MavlinkValues.ArduPilotStabilizeCustomMode));
            }
            else if (payload is [2] && stabilizeConfirmed)
            {
                transport.Emit(CommandAck(1, MavlinkCommandIds.ComponentArmDisarm, MavlinkValues.MavResultAccepted));
                transport.Emit(Heartbeat(
                    1,
                    autopilot: MavlinkValues.MavAutopilotArduPilot,
                    baseMode: MavlinkValues.MavModeFlagCustomModeEnabled | MavlinkValues.MavModeFlagSafetyArmed,
                    customMode: MavlinkValues.ArduPilotStabilizeCustomMode));
            }
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "arm-command",
                "correlation",
                "idempotency",
                OperatorCommandKind.Arm,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "test",
                false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.True(result.Accepted, result.Message);
        var modeIndex = transport.SentPayloads.FindIndex(payload => payload is [5]);
        var armIndex = transport.SentPayloads.FindIndex(modeIndex + 1, payload => payload is [2]);
        Assert.True(modeIndex >= 0, "The Arm request did not leave ArduPilot Land mode.");
        Assert.True(armIndex > modeIndex, "ArduPilot Arm was sent before Stabilize mode recovery.");
        Assert.Equal(MavlinkValues.ArduPilotStabilizeCustomMode, transport.Codec.LastCustomMode);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotGlobalMovementConfirmsGuidedBeforeDispatch()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () =>
        {
            transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot, customMode: 5));
            transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 10));
        };
        transport.OnSend = payload =>
        {
            if (payload is [5])
            {
                transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot, customMode: 4));
            }
            else if (payload is [9])
            {
                transport.Emit(CommandAck(1, MavlinkCommandIds.DoReposition, MavlinkValues.MavResultAccepted));
            }
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "command",
                "correlation",
                "idempotency",
                OperatorCommandKind.GoTo,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "test",
                false,
                DateTimeOffset.UtcNow,
                Parameters: OperatorCommandParameters.GlobalGoTo(43.7001, -79.4001, 510, 2)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Accepted, result.Message);
        Assert.Contains(transport.SentPayloads, payload => payload is [5]);
        Assert.Contains(transport.SentPayloads, payload => payload is [9]);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotFormation_StreamsGlobalRelativeTargets_AndReleasesToStableBrake()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () =>
        {
            transport.Emit(Heartbeat(
                1,
                autopilot: MavlinkValues.MavAutopilotArduPilot,
                baseMode: MavlinkValues.MavModeFlagCustomModeEnabled | MavlinkValues.MavModeFlagSafetyArmed,
                customMode: MavlinkValues.ArduPilotLoiterCustomMode));
            transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 10));
        };
        transport.OnSend = payload =>
        {
            if (payload is [5])
            {
                var mode = transport.Codec.LastCustomMode;
                transport.Emit(Heartbeat(
                    1,
                    autopilot: MavlinkValues.MavAutopilotArduPilot,
                    baseMode: MavlinkValues.MavModeFlagCustomModeEnabled | MavlinkValues.MavModeFlagSafetyArmed,
                    customMode: mode));
            }

            // Feed fresh position samples while the session is active and
            // while Brake stability is being confirmed on release.
            if (payload is [18])
                transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 10));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var begin = await connection.BeginArduPilotFormationControlAsync(
            "mavlink:mavlink:1",
            "formation-1",
            new ArduPilotFormationSetpoint(43.7001, -79.4001, 12, 1.5f, -0.5f, 0.25f),
            TestContext.Current.CancellationToken);

        Assert.True(begin.Accepted, begin.Message);
        Assert.Equal("Guided active", begin.State);
        await Task.Delay(180, TestContext.Current.CancellationToken);
        Assert.True(transport.SentPayloads.Count(payload => payload is [18]) >= 2);

        var updatesBefore = transport.SentPayloads.Count(payload => payload is [18]);
        var updated = await connection.UpdateArduPilotFormationControlAsync(
            "mavlink:mavlink:1",
            "formation-1",
            new ArduPilotFormationSetpoint(43.7002, -79.4002, 14, 0, 0, 0),
            TestContext.Current.CancellationToken);
        Assert.True(updated.Accepted, updated.Message);
        await Task.Delay(140, TestContext.Current.CancellationToken);
        Assert.True(transport.SentPayloads.Count(payload => payload is [18]) > updatesBefore);
        Assert.Equal(4u, transport.Codec.LastCustomMode);

        var stopped = await connection.StopArduPilotFormationControlAsync(
            "mavlink:mavlink:1",
            "formation-1",
            "Formation released.",
            TestContext.Current.CancellationToken);
        Assert.True(stopped.Accepted, stopped.Message);
        Assert.Equal("Holding", stopped.State);
        Assert.Equal(MavlinkValues.ArduPilotBrakeCustomMode, transport.Codec.LastCustomMode);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotHoldWaitsForStablePositionAndAltitudeTelemetry()
    {
        var transport = new FakeTransport();
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot,
            commands: commands);
        transport.OnOpen = () =>
            transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot, customMode: 4));
        transport.OnSend = payload =>
        {
            if (payload is [5] && transport.Codec.LastCustomMode == MavlinkValues.ArduPilotBrakeCustomMode)
            {
                transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot, customMode: MavlinkValues.ArduPilotBrakeCustomMode));
            }
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var command = new OperatorCommandRequest(
            "hold-command",
            "correlation",
            "idempotency",
            OperatorCommandKind.Hold,
            new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
            "test",
            false,
            DateTimeOffset.UtcNow);
        commands.Upsert(new OperationalCommandRecord(
            "hold-command",
            "Hold",
            "Vehicle",
            "mavlink:mavlink:1",
            "mavlink",
            OperationalCommandState.InProgress,
            "Hold",
            "Hold pending",
            "correlation",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        var result = await connection.SendOperatorCommandAsync(command, TestContext.Current.CancellationToken);
        Assert.True(result.Accepted, result.Message);
        Assert.Equal(OperationalCommandState.InProgress, result.State);

        transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 10));
        Assert.Equal(OperationalCommandState.InProgress, commands.Items.Single().State);

        await Task.Delay(1100, TestContext.Current.CancellationToken);
        transport.Emit(GlobalPosition(1, 43.700001, -79.400001, 500, 10));

        Assert.Equal(OperationalCommandState.Succeeded, commands.Items.Single().State);
        Assert.Contains("telemetry confirmed", commands.Items.Single().Message, StringComparison.OrdinalIgnoreCase);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArmRequiresPostDispatchArmedHeartbeatBeforeCompletion()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () =>
            transport.Emit(Heartbeat(1, autopilot: MavlinkValues.MavAutopilotArduPilot));
        transport.OnSend = payload =>
        {
            if (payload is [2])
                transport.Emit(CommandAck(1, MavlinkCommandIds.ComponentArmDisarm, MavlinkValues.MavResultAccepted));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var result = await connection.SendOperatorCommandAsync(
            new OperatorCommandRequest(
                "arm-command",
                "correlation",
                "idempotency",
                OperatorCommandKind.Arm,
                new OperatorCommandTarget("mavlink", "mavlink:mavlink:1", null, DateTimeOffset.UtcNow),
                "test",
                false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(OperationalCommandState.InProgress, result.State);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task MissionResume_DoesNotResetCurrentItem()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        transport.OnSend = payload =>
        {
            if (payload is [5])
                transport.Emit(Heartbeat(1, customMode: MavlinkValues.Px4AutoMissionCustomMode));
        };

        await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        transport.SentPayloads.Clear();
        var result = await connection.SetMissionModeAsync(
            "mavlink:mavlink:1",
            paused: false,
            TestContext.Current.CancellationToken,
            restartFromBeginning: false);

        Assert.True(result.Succeeded, result.Summary);
        Assert.DoesNotContain(transport.SentPayloads, payload => payload is [15]);
        Assert.Contains(transport.SentPayloads, payload => payload is [5]);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task UnsupportedHeartbeat_DoesNotCompleteConnection()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(
            1,
            mavType: 1,
            autopilot: MavlinkValues.MavAutopilotPx4));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            new CancellationTokenSource(TimeSpan.FromMilliseconds(50)).Token));

        Assert.Single(connection.Observations);
        Assert.NotEqual(AvailabilityState.Online, connection.State);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task CancelledHeartbeatWait_ClosesSessionAndAllowsFreshReconnect()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);

        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ConnectAsync(
                ConnectionCredentials.Empty,
                reconnecting: false,
                timeout.Token));
        }

        Assert.False(transport.IsOpen);
        Assert.Equal(1, transport.OpenCount);

        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            reconnecting: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.OpenCount);
        Assert.Equal(AvailabilityState.Online, connection.State);
        Assert.Single(connection.Observations);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Px4ManualControl_PrimesInputAndRequiresPx4ManualInputConfirmation()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        transport.OnSend = payload =>
        {
            if (payload is [7])
            {
                transport.Emit(ParameterValue(1, "COM_RC_IN_MODE", 3, 6, 0, 1));
            }
            else if (payload is [5])
            {
                transport.Emit(Heartbeat(
                    1,
                    baseMode: MavlinkValues.MavModeFlagCustomModeEnabled | MavlinkValues.MavModeFlagManualInputEnabled,
                    customMode: MavlinkValues.Px4PositionCustomMode));
            }
        };

        var observation = await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);
        var vehicleId = observation.Vehicle!.VehicleId;

        var begin = await connection.BeginManualControlAsync(vehicleId, TestContext.Current.CancellationToken);
        Assert.True(begin.Accepted, begin.Message);

        var streamed = await connection.SendManualControlAsync(
            vehicleId,
            ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow),
            new ManualControlProfile(),
            TestContext.Current.CancellationToken);
        Assert.True(streamed.Accepted, streamed.Message);

        var released = await connection.EndManualControlAsync(vehicleId, TestContext.Current.CancellationToken);
        Assert.True(released.Accepted, released.Message);

        Assert.Contains([5], transport.SentPayloads);
        Assert.True(transport.SentPayloads.Count(payload => payload is [4]) >= 8);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Px4ManualControl_RejectsWhenPx4IsConfiguredForRcOnlyInput()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        transport.OnSend = payload =>
        {
            if (payload is [7])
                transport.Emit(ParameterValue(1, "COM_RC_IN_MODE", 0, 6, 0, 1));
        };

        var observation = await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);

        var result = await connection.BeginManualControlAsync(
            observation.Vehicle!.VehicleId,
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Contains("RC-only", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain([5], transport.SentPayloads);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Px4ManualControl_RejectsWhenPx4DoesNotConfirmMavlinkManualInput()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        transport.OnSend = payload =>
        {
            if (payload is [7])
                transport.Emit(ParameterValue(1, "COM_RC_IN_MODE", 3, 6, 0, 1));
        };

        var observation = await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);

        var result = await connection.BeginManualControlAsync(
            observation.Vehicle!.VehicleId,
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Contains("did not enable MAVLink manual input", result.Message, StringComparison.Ordinal);
        Assert.Contains(transport.SentPayloads, payload => payload is [5]);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotManualControl_UsesNativeManualControlStreamAndSafeRelease()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () => transport.Emit(Heartbeat(
            1,
            autopilot: MavlinkValues.MavAutopilotArduPilot,
            customMode: 0)); // Stabilize: no GPS is required.
        transport.OnOpen += () => transport.Emit(GlobalPosition(1, 43.7, -79.4, 500, 10));
        transport.OnSend = payload =>
        {
            if (payload is [7])
            {
                // The fake codec does not preserve the requested name, so
                // provide all three optional admission values on every read.
                transport.Emit(ParameterValue(1, "MAV_GCS_SYSID", 255, 6, 0, 1));
                transport.Emit(ParameterValue(1, "MAV_OPTIONS", 1, 6, 0, 1));
                transport.Emit(ParameterValue(1, "RC_OPTIONS", 0, 6, 0, 1));
            }
            else if (payload is [5])
            {
                transport.Emit(Heartbeat(
                    1,
                    autopilot: MavlinkValues.MavAutopilotArduPilot,
                    customMode: 5)); // Loiter safe-release confirmation.
            }
        };

        var observation = await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);
        var vehicleId = observation.Vehicle!.VehicleId;

        var begin = await connection.BeginManualControlAsync(vehicleId, TestContext.Current.CancellationToken);
        Assert.True(begin.Accepted, begin.Message);
        await Task.Delay(150, TestContext.Current.CancellationToken);

        Assert.True(connection.TryGetManualControlStatus(vehicleId, out var status));
        Assert.Equal("Stabilize", status.Mode);
        Assert.Equal(ManualControlModeClass.GpsIndependent, status.ModeClass);
        Assert.True(status.ParameterAdmissionVerified);
        Assert.True(status.StreamRateHertz >= 10, $"Expected a live 20 Hz stream, got {status.StreamRateHertz:0.0} Hz.");
        Assert.Contains(transport.SentPayloads, payload => payload is [4]);

        var sent = await connection.SendManualControlAsync(
            vehicleId,
            new ManualControlSetpoint(250, -100, 300, 50, true, DateTimeOffset.UtcNow),
            new ManualControlProfile(),
            TestContext.Current.CancellationToken);
        Assert.True(sent.Accepted, sent.Message);

        var released = await connection.EndManualControlAsync(vehicleId, TestContext.Current.CancellationToken);
        Assert.True(released.Accepted, released.Message);
        Assert.Contains(transport.SentPayloads, payload => payload is [5]);
        Assert.True(transport.SentPayloads.Count(payload => payload is [4]) >= 8);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ArduPilotManualControl_RejectsAutomatedModesWithoutStartingAStream()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(
            transport,
            adapters: [new ArduPilotMavlinkAutopilotAdapter()],
            autopilot: MavlinkAutopilotProfile.ArduPilot);
        transport.OnOpen = () => transport.Emit(Heartbeat(
            1,
            autopilot: MavlinkValues.MavAutopilotArduPilot,
            customMode: 4)); // Guided is automated guidance, not pilot input.

        var observation = await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);
        var result = await connection.BeginManualControlAsync(
            observation.Vehicle!.VehicleId,
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Contains("compatible mode", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(transport.SentPayloads, payload => payload is [4]);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ParameterDownload_CollectsParamValuesAfterRequestList()
    {
        var transport = new FakeTransport();
        var connection = CreateConnection(transport);
        transport.OnOpen = () => transport.Emit(Heartbeat(1));
        transport.OnSend = payload =>
        {
            if (payload.Length > 0 && payload[0] == 6)
            {
                transport.Emit(ParameterValue(1, "MPC_XY_VEL_MAX", 12.5f, 9, 0, 2));
                transport.Emit(ParameterValue(1, "MPC_Z_VEL_MAX_UP", 3f, 9, 1, 2));
            }
        };

        var observation = await connection.ConnectAsync(ConnectionCredentials.Empty, false, TestContext.Current.CancellationToken);
        var document = await connection.DownloadParametersAsync(observation.Vehicle!.VehicleId, TestContext.Current.CancellationToken);

        Assert.Equal(["MPC_XY_VEL_MAX", "MPC_Z_VEL_MAX_UP"], document.Parameters.Select(item => item.Name).ToArray());
        Assert.Equal("12.5", document.Parameters[0].Value);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task SerialRadioWithoutHeartbeat_RemainsOpenAndDegraded()
    {
        var transport = new FakeTransport { KeepOpenWithoutHeartbeat = true };
        var connection = CreateConnection(transport, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TimeoutException>(() => connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken));

        Assert.True(transport.IsOpen);
        Assert.Equal(AvailabilityState.Degraded, connection.State);
        Assert.Contains("no PX4 MAVLink heartbeat", connection.LastError, StringComparison.OrdinalIgnoreCase);
        connection.EvaluateFreshness(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
        Assert.Equal(AvailabilityState.Degraded, connection.State);
        Assert.Single(connection.LiveSnapshot.Links);
        await connection.DisposeAsync();
    }

    private static MavlinkConnection CreateConnection(
        FakeTransport transport,
        TimeSpan? heartbeatTimeout = null,
        IReadOnlyList<IMavlinkAutopilotAdapter>? adapters = null,
        MavlinkAutopilotProfile autopilot = MavlinkAutopilotProfile.Px4,
        IEntityStore<string, OperationalCommandRecord>? commands = null)
        => new(
            new ConnectionDefinition(
                "mavlink",
                "PX4 SITL",
                "udp-listen://0.0.0.0:14550",
                ConnectionMode.Mavlink,
                Mavlink: new MavlinkConnectionOptions(Autopilot: autopilot)),
            transport,
            transport.Codec,
            adapters ?? (autopilot == MavlinkAutopilotProfile.ArduPilot
                ? [new ArduPilotMavlinkAutopilotAdapter()]
                : [new Px4MavlinkAutopilotAdapter()]),
            commands ?? new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal),
            new ImmediateDispatcher(),
            new MavlinkConnectionRegistry(),
            NullLogger<MavlinkConnection>.Instance,
            heartbeatTimeout);

    private static MavlinkPacket Heartbeat(
        byte systemId,
        byte mavType = 2,
        byte autopilot = MavlinkValues.MavAutopilotPx4,
        byte baseMode = MavlinkValues.MavModeFlagCustomModeEnabled,
        uint customMode = MavlinkValues.Px4AutoLoiterCustomMode)
        => new(
            2,
            1,
            systemId,
            MavlinkValues.MavCompIdAutopilot1,
            MavlinkMessageIds.Heartbeat,
            new Dictionary<string, object>
            {
                ["type"] = mavType,
                ["autopilot"] = autopilot,
                ["base_mode"] = baseMode,
                ["custom_mode"] = customMode,
                ["system_status"] = MavlinkValues.MavStateActive
            },
            DateTimeOffset.UtcNow);

    private static MavlinkPacket StatusText(byte systemId, string text)
        => new(
            2,
            3,
            systemId,
            MavlinkValues.MavCompIdAutopilot1,
            MavlinkMessageIds.StatusText,
            new Dictionary<string, object>
            {
                ["severity"] = (byte)4,
                ["text"] = text,
                ["id"] = (ushort)0,
                ["chunk_seq"] = (byte)0
            },
            DateTimeOffset.UtcNow);

    private static MavlinkPacket GlobalPosition(byte systemId, double latitude, double longitude, int altitudeMsl, int altitudeAgl)
        => new(
            2,
            4,
            systemId,
            MavlinkValues.MavCompIdAutopilot1,
            MavlinkMessageIds.GlobalPositionInt,
            new Dictionary<string, object>
            {
                ["lat"] = (int)Math.Round(latitude * 10_000_000),
                ["lon"] = (int)Math.Round(longitude * 10_000_000),
                ["alt"] = altitudeMsl * 1000,
                ["relative_alt"] = altitudeAgl * 1000,
                ["vx"] = 0,
                ["vy"] = 0,
                ["vz"] = 0,
                ["hdg"] = (ushort)9000
            },
            DateTimeOffset.UtcNow);

    private static MavlinkPacket ExtendedSystemState(byte systemId, byte landedState)
        => new(
            2,
            1,
            systemId,
            MavlinkValues.MavCompIdAutopilot1,
            MavlinkMessageIds.ExtendedSystemState,
            new Dictionary<string, object> { ["landed_state"] = landedState },
            DateTimeOffset.UtcNow);

    private static MavlinkPacket CommandAck(byte systemId, ushort command, byte result)
        => new(
            2,
            5,
            systemId,
            MavlinkValues.MavCompIdAutopilot1,
            MavlinkMessageIds.CommandAck,
            new Dictionary<string, object>
            {
                ["command"] = command,
                ["result"] = result,
                ["result_param2"] = 0,
                ["progress"] = (byte)0
            },
            DateTimeOffset.UtcNow);

    private static MavlinkPacket ParameterValue(byte systemId, string name, float value, byte type, short index, ushort count)
        => new(
            2,
            2,
            systemId,
            MavlinkValues.MavCompIdAutopilot1,
            MavlinkMessageIds.ParamValue,
            new Dictionary<string, object>
            {
                ["param_id"] = name,
                ["param_value"] = value,
                ["param_type"] = type,
                ["param_index"] = index,
                ["param_count"] = count
            },
            DateTimeOffset.UtcNow);

    private static MavlinkPacket MissionRequest(byte systemId, ushort sequence)
        => new(2, 2, systemId, MavlinkValues.MavCompIdAutopilot1, MavlinkMessageIds.MissionRequestInt,
            new Dictionary<string, object> { ["seq"] = sequence }, DateTimeOffset.UtcNow);

    private static MavlinkPacket MissionAck(byte systemId, byte result)
        => new(2, 2, systemId, MavlinkValues.MavCompIdAutopilot1, MavlinkMessageIds.MissionAck,
            new Dictionary<string, object> { ["type"] = result }, DateTimeOffset.UtcNow);

    private static MavlinkPacket MissionItemReached(byte systemId, ushort sequence)
        => new(2, 2, systemId, MavlinkValues.MavCompIdAutopilot1, MavlinkMessageIds.MissionItemReached,
            new Dictionary<string, object> { ["seq"] = sequence }, DateTimeOffset.UtcNow);

    private sealed class FakeCodec : IMavlinkCodec
    {
        private readonly Queue<MavlinkPacket> _packets = new();
        public uint LastCustomMode { get; private set; }

        public void Enqueue(MavlinkPacket packet) => _packets.Enqueue(packet);

        public bool TryDecode(
            ReadOnlySpan<byte> bytes,
            DateTimeOffset receivedAt,
            out MavlinkPacket? packet,
            out string? error)
        {
            packet = _packets.Dequeue();
            error = null;
            return true;
        }

        public byte[] EncodeHeartbeat(byte sourceSystemId, byte sourceComponentId) => [1];

        public byte[] EncodePing(
            byte sourceSystemId,
            byte sourceComponentId,
            ulong timeUsec,
            uint sequence,
            byte targetSystemId,
            byte targetComponentId) => [3];

        public byte[] EncodeCommandLong(
            byte sourceSystemId,
            byte sourceComponentId,
            byte targetSystemId,
            byte targetComponentId,
            ushort command,
            ReadOnlySpan<float> parameters,
            byte confirmation = 0) => [2];

        public byte[] EncodeCommandInt(
            byte sourceSystemId,
            byte sourceComponentId,
            byte targetSystemId,
            byte targetComponentId,
            byte frame,
            ushort command,
            ReadOnlySpan<float> parameters,
            int x,
            int y,
            float z,
            byte confirmation = 0) => [9];

        public byte[] EncodeSetMode(
            byte sourceSystemId,
            byte sourceComponentId,
            byte targetSystemId,
            byte baseMode,
            uint customMode)
        {
            LastCustomMode = customMode;
            return [5];
        }

        public byte[] EncodeSetPositionTargetLocalNed(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, uint timeBootMilliseconds, byte coordinateFrame, ushort typeMask, float north, float east, float down, float velocityNorth, float velocityEast, float velocityDown) => [17];

        public byte[] EncodeSetPositionTargetGlobalInt(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, uint timeBootMilliseconds, byte coordinateFrame, ushort typeMask, int latitudeE7, int longitudeE7, float altitude, float velocityNorth, float velocityEast, float velocityDown) => [18];

        public byte[] EncodeManualControl(
            byte sourceSystemId,
            byte sourceComponentId,
            byte targetSystemId,
            short x,
            short y,
            short z,
            short r,
            ushort buttons = 0) => [4];

        public byte[] EncodeParameterRequestList(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId) => [6];

        public byte[] EncodeParameterRequestRead(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, string parameterName, short parameterIndex) => [7];

        public byte[] EncodeParameterSet(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, string parameterName, float value, byte parameterType) => [8];

        public byte[] EncodeMissionCount(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort count, byte missionType = 0) => [10];

        public byte[] EncodeMissionSetCurrent(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort sequence) => [15];

        public byte[] EncodeMissionClearAll(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte missionType = 0) => [16];

        public byte[] EncodeMissionItemInt(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, MavlinkMissionItem item) => [11];

        public byte[] EncodeMissionRequestList(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte missionType = 0) => [12];

        public byte[] EncodeMissionRequestInt(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort sequence, byte missionType = 0) => [13];

        public byte[] EncodeMissionAck(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte result, byte missionType = 0) => [14];
    }

    private sealed class FakeTransport : IMavlinkTransport
    {
        public FakeCodec Codec { get; } = new();
        public event EventHandler<MavlinkTransportChunk>? ChunkReceived;
        public event EventHandler<MavlinkTransportFault>? Faulted;
        public Action? OnOpen { get; set; }
        public Action<byte[]>? OnSend { get; set; }
        public bool IsOpen { get; private set; }
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public List<byte[]> SentPayloads { get; } = [];
        public List<MavlinkTransportRoute> SentRoutes { get; } = [];
        public bool KeepOpenWithoutHeartbeat { get; init; }
        public string TransportName => "UDP";
        public string? LocalEndpoint => "0.0.0.0:14550";
        public MavlinkTransportStatistics Statistics => new();

        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            IsOpen = true;
            OpenCount++;
            OnOpen?.Invoke();
            return Task.CompletedTask;
        }

        public Task SendAsync(
            ReadOnlyMemory<byte> payload,
            MavlinkTransportRoute route,
            CancellationToken cancellationToken = default)
        {
            SentPayloads.Add(payload.ToArray());
            SentRoutes.Add(route);
            OnSend?.Invoke(payload.ToArray());
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            IsOpen = false;
            CloseCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(MavlinkPacket packet)
        {
            Codec.Enqueue(packet);
            ChunkReceived?.Invoke(
                this,
                new MavlinkTransportChunk(
                    new byte[] { 0 },
                    new MavlinkTransportRoute("127.0.0.1:18571", new IPEndPoint(IPAddress.Loopback, 18571)),
                    packet.ReceivedAt));
        }

    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
