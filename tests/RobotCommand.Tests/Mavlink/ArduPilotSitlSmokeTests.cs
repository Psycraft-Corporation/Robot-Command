using System.Net;
using MavLinkSharp.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class ArduPilotSitlSmokeTests
{
    [Fact]
    [Trait("Category", "SITL")]
    public async Task ReceivesArduPilotHeartbeatAndCurrentDiagnostics()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ROBOT_COMMAND_ARDUPILOT_SITL"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var definition = new ConnectionDefinition(
            "ardupilot-sitl-smoke",
            "ArduPilot SITL smoke",
            "udp-listen://0.0.0.0:14550?bootstrap=127.0.0.1:14551",
            ConnectionMode.Mavlink,
            Mavlink: new MavlinkConnectionOptions(Autopilot: MavlinkAutopilotProfile.ArduPilot));
        await using var connection = new MavlinkConnection(
            definition,
            new UdpMavlinkTransport(new IPEndPoint(IPAddress.Any, 14550)),
            new MavlinkSharpCodec(DialectType.Ardupilotmega),
            [new ArduPilotMavlinkAutopilotAdapter()],
            new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal),
            new ImmediateDispatcher(),
            new MavlinkConnectionRegistry(),
            NullLogger<MavlinkConnection>.Instance,
            bootstrapRoute: new MavlinkTransportRoute(
                "127.0.0.1:14551",
                new IPEndPoint(IPAddress.Loopback, 14551)));

        var observation = await connection.ConnectAsync(
            ConnectionCredentials.Empty,
            false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(observation.Vehicle);
        Assert.Contains("ardupilot", observation.Vehicle!.ProfileKey, StringComparison.OrdinalIgnoreCase);
        await WaitForAsync(
            () => connection.LiveSnapshot.Telemetry.FirstOrDefault()?.IsStale == false &&
                  connection.LiveSnapshot.VehicleDiagnostics.Count > 0,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        var vehicle = Assert.Single(connection.LiveSnapshot.Telemetry);
        Assert.False(vehicle.IsStale);
        Assert.NotEmpty(connection.LiveSnapshot.VehicleDiagnostics);
    }

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
                throw new TimeoutException("The expected ArduPilot SITL telemetry state was not observed.");
            }
            await Task.Delay(100, cancellationToken);
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
