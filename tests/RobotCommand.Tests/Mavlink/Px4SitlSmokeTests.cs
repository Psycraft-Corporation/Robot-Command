using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class Px4SitlSmokeTests
{
    [Fact]
    [Trait("Category", "SITL")]
    public async Task ReceivesPx4HeartbeatFromUnitFc()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ROBOT_COMMAND_PX4_SITL"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        await using var transport = new UdpMavlinkTransport(
            new IPEndPoint(IPAddress.Any, 14550));
        var codec = new MavlinkSharpCodec();
        var heartbeat = new TaskCompletionSource<MavlinkPacket>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        transport.ChunkReceived += (sender, chunk) =>
        {
            _ = sender;
            if (codec.TryDecode(
                    chunk.Payload.Span,
                    chunk.ReceivedAt,
                    out var packet,
                    out _) &&
                packet is
                {
                    MessageId: MavlinkMessageIds.Heartbeat
                } &&
                packet.Byte("autopilot") == MavlinkValues.MavAutopilotPx4)
            {
                heartbeat.TrySetResult(packet);
            }
        };

        await transport.OpenAsync(TestContext.Current.CancellationToken);
        var received = await heartbeat.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(MavlinkValues.MavCompIdAutopilot1, received.ComponentId);
        Assert.Equal(2, received.ProtocolVersion);
    }

    [Fact]
    [Trait("Category", "SITL")]
    public async Task ConnectsArmsAndDisarmsPx4Unit()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ROBOT_COMMAND_PX4_SITL"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var definition = new ConnectionDefinition(
            "px4-sitl-smoke",
            "PX4 SITL smoke",
            "udp-listen://0.0.0.0:14550",
            ConnectionMode.Mavlink,
            Mavlink: new MavlinkConnectionOptions());
        var transport = new UdpMavlinkTransport(
            new IPEndPoint(IPAddress.Any, 14550));
        await using var connection = new MavlinkConnection(
            definition,
            transport,
            new MavlinkSharpCodec(),
            [new Px4MavlinkAutopilotAdapter()],
            new EntityStore<string, OperationalCommandRecord>(
                item => item.Id,
                StringComparer.Ordinal),
            new ImmediateDispatcher(),
            new MavlinkConnectionRegistry(),
            NullLogger<MavlinkConnection>.Instance);

        var observation = await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);
        var vehicleId = observation.Vehicle!.VehicleId;
        var target = new OperatorCommandTarget(
            definition.Id,
            vehicleId,
            observation.Runtime.LogosInstanceId,
            DateTimeOffset.UtcNow);

        try
        {
            var arm = await connection.SendOperatorCommandAsync(
                Request(OperatorCommandKind.Arm, target),
                TestContext.Current.CancellationToken);
            Assert.True(arm.Accepted, arm.Message);
            await WaitForAsync(
                () => connection.TryGetVehicle(vehicleId, out _, out _, out var telemetry, out _) &&
                      telemetry?.Armed == true,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            var disarm = await connection.SendOperatorCommandAsync(
                Request(OperatorCommandKind.Disarm, target),
                TestContext.Current.CancellationToken);
            Assert.True(disarm.Accepted, disarm.Message);
            await WaitForAsync(
                () => connection.TryGetVehicle(vehicleId, out _, out _, out var telemetry, out _) &&
                      telemetry?.Armed == false,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    [Trait("Category", "SITL")]
    public async Task SendsNeutralManualControlToPx4Unit()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ROBOT_COMMAND_PX4_SITL"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var definition = new ConnectionDefinition(
            "px4-sitl-manual-smoke",
            "PX4 SITL manual smoke",
            "udp-listen://0.0.0.0:14550",
            ConnectionMode.Mavlink,
            Mavlink: new MavlinkConnectionOptions());
        var transport = new UdpMavlinkTransport(new IPEndPoint(IPAddress.Any, 14550));
        await using var connection = new MavlinkConnection(
            definition,
            transport,
            new MavlinkSharpCodec(),
            [new Px4MavlinkAutopilotAdapter()],
            new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal),
            new ImmediateDispatcher(),
            new MavlinkConnectionRegistry(),
            NullLogger<MavlinkConnection>.Instance);

        var observation = await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);
        var vehicleId = observation.Vehicle!.VehicleId;
        var begin = await connection.BeginManualControlAsync(vehicleId, TestContext.Current.CancellationToken);
        Assert.True(begin.Accepted, begin.Message);
        var neutral = await connection.SendManualControlAsync(
            vehicleId,
            ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow),
            new ManualControlProfile(),
            TestContext.Current.CancellationToken);
        Assert.True(neutral.Accepted, neutral.Message);

        var release = await connection.EndManualControlAsync(vehicleId, TestContext.Current.CancellationToken);
        Assert.True(release.Accepted, release.Message);
    }

    private static OperatorCommandRequest Request(
        OperatorCommandKind command,
        OperatorCommandTarget target)
        => new(
            $"sitl-{command}-{Guid.NewGuid():N}",
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            command,
            target,
            "PX4 SITL smoke test",
            false,
            DateTimeOffset.UtcNow,
            Parameters: OperatorCommandParameters.None);

    private static async Task WaitForAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= expiresAt)
            {
                throw new TimeoutException("The expected PX4 telemetry state was not observed.");
            }
            await Task.Delay(100, cancellationToken);
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
