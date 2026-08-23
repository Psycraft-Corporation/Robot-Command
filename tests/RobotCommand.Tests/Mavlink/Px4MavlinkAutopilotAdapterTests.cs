using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class Px4MavlinkAutopilotAdapterTests
{
    private readonly Px4MavlinkAutopilotAdapter _adapter = new();

    [Fact]
    public void SupportsPx4MulticoptersOnly()
    {
        Assert.True(_adapter.Supports(MavlinkValues.MavAutopilotPx4, 2));
        Assert.False(_adapter.Supports(MavlinkValues.MavAutopilotPx4, 1));
        Assert.False(_adapter.Supports(3, 2));
    }

    [Fact]
    public void Takeoff_ConvertsAglToAmsl()
    {
        var command = _adapter.BuildCommand(
            Request(
                OperatorCommandKind.Takeoff,
                new OperatorCommandParameters(TakeoffAltitudeAglMetres: 20)),
            Telemetry(altitudeMsl: 510, altitudeAgl: 10));

        Assert.Equal(MavlinkCommandIds.NavTakeoff, command.CommandId);
        Assert.Equal(520f, command.Parameters[6]);
    }

    [Fact]
    public void GoTo_MapsToDoRepositionWithChangeMode()
    {
        var command = _adapter.BuildCommand(
            Request(
                OperatorCommandKind.GoTo,
                OperatorCommandParameters.GlobalGoTo(43.7, -79.4, 530, 2)),
            Telemetry());

        Assert.Equal(MavlinkCommandIds.DoReposition, command.CommandId);
        Assert.Equal(1f, command.Parameters[1]);
        Assert.Equal(43.7f, command.Parameters[4], 3);
        Assert.Equal(-79.4f, command.Parameters[5], 3);
        Assert.Equal(530f, command.Parameters[6]);
    }

    [Fact]
    public void Hold_UsesPx4PauseRepositionPath()
    {
        var command = _adapter.BuildCommand(
            Request(OperatorCommandKind.Hold, OperatorCommandParameters.None),
            Telemetry());

        Assert.Equal(MavlinkCommandIds.DoReposition, command.CommandId);
        Assert.Equal(-1f, command.Parameters[0]);
        Assert.Equal(1f, command.Parameters[1]);
    }

    [Fact]
    public void RelativeAltitude_ResolvesToAmslTarget()
    {
        var command = _adapter.BuildCommand(
            Request(
                OperatorCommandKind.ChangeAltitude,
                new OperatorCommandParameters(
                    AltitudeTargetKind: OperatorAltitudeTargetKind.RelativeDelta,
                    AltitudeRelativeDeltaMetres: 15)),
            Telemetry(altitudeMsl: 500));

        Assert.Equal(MavlinkCommandIds.DoReposition, command.CommandId);
        Assert.Equal(515f, command.Parameters[6]);
    }

    [Fact]
    public void RelativeHeading_ResolvesAndNormalizesAbsoluteYaw()
    {
        var command = _adapter.BuildCommand(
            Request(
                OperatorCommandKind.SetHeading,
                new OperatorCommandParameters(
                    HeadingTargetKind: OperatorHeadingTargetKind.RelativeYaw,
                    RelativeYawDegrees: -120)),
            Telemetry());

        Assert.Equal(MavlinkCommandIds.DoReposition, command.CommandId);
        Assert.Equal(330f, command.Parameters[3]);
    }

    private static OperatorCommandRequest Request(
        OperatorCommandKind command,
        OperatorCommandParameters parameters)
        => new(
            "command",
            "correlation",
            "idempotency",
            command,
            new OperatorCommandTarget("mavlink", "vehicle", "runtime", DateTimeOffset.UtcNow),
            "test",
            false,
            DateTimeOffset.UtcNow,
            Parameters: parameters);

    private static VehicleTelemetryRecord Telemetry(
        double altitudeMsl = 500,
        double altitudeAgl = 10)
        => new(
            "telemetry",
            "vehicle",
            "mavlink",
            "runtime",
            AvailabilityState.Online,
            true,
            "Flying",
            "Hold",
            "MAVLink 2",
            "Healthy",
            "Ready",
            43.73,
            -79.42,
            altitudeMsl,
            altitudeAgl,
            null,
            null,
            null,
            0,
            0,
            0,
            90,
            false,
            "MAVLINK_OK",
            "Ready",
            DateTimeOffset.UtcNow);
}
