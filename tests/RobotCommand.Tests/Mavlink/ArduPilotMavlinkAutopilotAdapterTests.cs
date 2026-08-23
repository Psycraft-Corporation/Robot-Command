using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class ArduPilotMavlinkAutopilotAdapterTests
{
    private readonly ArduPilotMavlinkAutopilotAdapter _adapter = new();

    [Fact]
    public void SupportsOnlyArduPilotMulticopters()
    {
        Assert.True(_adapter.Supports(MavlinkValues.MavAutopilotArduPilot, 2));
        Assert.False(_adapter.Supports(MavlinkValues.MavAutopilotArduPilot, 1));
        Assert.False(_adapter.Supports(MavlinkValues.MavAutopilotPx4, 2));
    }

    [Theory]
    [InlineData(0, "Stabilize")]
    [InlineData(2, "AltHold")]
    [InlineData(4, "Guided")]
    [InlineData(5, "Loiter")]
    [InlineData(17, "Brake")]
    [InlineData(6, "RTL")]
    [InlineData(9, "Land")]
    public void DecodesArduCopterModes(uint mode, string expected)
        => Assert.Equal(expected, _adapter.DecodeMode(mode));

    [Fact]
    public void MovementCommandsUseCommandIntGuidedPosition()
    {
        var command = _adapter.BuildCommand(
            Request(OperatorCommandKind.GoTo, OperatorCommandParameters.GlobalGoTo(43.7, -79.4, 530, 2)),
            Telemetry());

        Assert.Equal(MavlinkWireKind.CommandInt, command.WireKind);
        Assert.Equal(MavlinkCommandIds.DoReposition, command.CommandId);
        Assert.Equal(437000000, command.X);
        Assert.Equal(-794000000, command.Y);
        Assert.Equal(530f, command.Z);
        Assert.Equal(1f, command.Parameters[1]);
        Assert.Equal(0, command.Frame);
    }

    [Fact]
    public void TakeoffUsesArduPilotRelativeHomeAltitude()
    {
        var command = _adapter.BuildCommand(
            Request(OperatorCommandKind.Takeoff, new OperatorCommandParameters(TakeoffAltitudeAglMetres: 15)),
            Telemetry());

        Assert.Equal(MavlinkCommandIds.NavTakeoff, command.CommandId);
        Assert.Equal(15f, command.Parameters[6]);
    }

    [Fact]
    public void HeadingUsesNativeConditionYawRatherThanRepositionYaw()
    {
        var command = _adapter.BuildCommand(
            Request(OperatorCommandKind.SetHeading, OperatorCommandParameters.AbsoluteHeading(45)),
            Telemetry());

        Assert.Equal(MavlinkCommandIds.ConditionYaw, command.CommandId);
        Assert.Equal(MavlinkWireKind.CommandLong, command.WireKind);
        Assert.Equal(45f, command.Parameters[0]);
        Assert.Equal(30f, command.Parameters[1]);
        Assert.Equal(-1f, command.Parameters[2]);
        Assert.Equal(1f, command.Parameters[3]);
    }

    [Fact]
    public void HoldIsArduCopterBrake()
    {
        Assert.True(_adapter.IsHoldMode(MavlinkValues.ArduPilotBrakeCustomMode));
        Assert.False(_adapter.IsHoldMode(MavlinkValues.ArduPilotLoiterCustomMode));
        var hold = _adapter.BuildCommand(
            Request(OperatorCommandKind.Hold, OperatorCommandParameters.None), Telemetry()).Description;
        Assert.Equal("ArduPilot Hold", hold);
        Assert.Equal(MavlinkWireKind.SetMode, _adapter.BuildCommand(
            Request(OperatorCommandKind.Hold, OperatorCommandParameters.None), Telemetry()).WireKind);
    }

    [Fact]
    public void HoldRequiresStablePositionAndAltitudeTelemetry()
    {
        Assert.True(_adapter.HoldPolicy.RequiresStableTelemetry);
        Assert.Equal(TimeSpan.FromSeconds(1), _adapter.HoldPolicy.StabilityWindow);
        Assert.Equal("ArduCopter Brake with stable position and altitude telemetry", _adapter.HoldPolicy.StrategyName);
    }

    [Theory]
    [InlineData(OperatorCommandKind.Arm, MavlinkCommandIds.ComponentArmDisarm, MavlinkWireKind.CommandLong)]
    [InlineData(OperatorCommandKind.Disarm, MavlinkCommandIds.ComponentArmDisarm, MavlinkWireKind.CommandLong)]
    [InlineData(OperatorCommandKind.Takeoff, MavlinkCommandIds.NavTakeoff, MavlinkWireKind.CommandLong)]
    [InlineData(OperatorCommandKind.Land, MavlinkCommandIds.NavLand, MavlinkWireKind.CommandLong)]
    [InlineData(OperatorCommandKind.Recover, MavlinkCommandIds.NavReturnToLaunch, MavlinkWireKind.CommandLong)]
    public void BasicOperationsUseExpectedMavlinkCommands(OperatorCommandKind operation, ushort commandId, MavlinkWireKind wireKind)
    {
        var parameters = operation == OperatorCommandKind.Takeoff
            ? new OperatorCommandParameters(TakeoffAltitudeAglMetres: 15)
            : OperatorCommandParameters.None;
        var result = _adapter.BuildCommand(Request(operation, parameters), Telemetry());

        Assert.Equal(commandId, result.CommandId);
        Assert.Equal(wireKind, result.WireKind);
    }

    [Fact]
    public void ManualControlRequiresArduCopterPilotModes()
    {
        Assert.False(_adapter.SupportsManualControl("Guided"));
        Assert.True(_adapter.SupportsManualControl("Loiter"));
        Assert.True(_adapter.SupportsManualControl("AltHold"));
        Assert.False(_adapter.SupportsManualControl("RTL"));
    }

    [Theory]
    [InlineData("Stabilize", ManualControlModeClass.GpsIndependent)]
    [InlineData("Acro", ManualControlModeClass.GpsIndependent)]
    [InlineData("AltHold", ManualControlModeClass.GpsIndependent)]
    [InlineData("Sport", ManualControlModeClass.GpsIndependent)]
    [InlineData("Loiter", ManualControlModeClass.PositionAssisted)]
    [InlineData("PosHold", ManualControlModeClass.PositionAssisted)]
    [InlineData("Guided", ManualControlModeClass.Unsupported)]
    [InlineData("Guided_NoGPS", ManualControlModeClass.Unsupported)]
    [InlineData("RTL", ManualControlModeClass.Unsupported)]
    public void ClassifiesNativePilotModes(string mode, ManualControlModeClass expected)
        => Assert.Equal(expected, _adapter.ClassifyManualControlMode(mode));

    private static OperatorCommandRequest Request(OperatorCommandKind command, OperatorCommandParameters parameters)
        => new("command", "correlation", "idempotency", command,
            new OperatorCommandTarget("mavlink", "vehicle", "runtime", DateTimeOffset.UtcNow),
            "test", false, DateTimeOffset.UtcNow, Parameters: parameters);

    private static VehicleTelemetryRecord Telemetry()
        => new("telemetry", "vehicle", "mavlink", "runtime", AvailabilityState.Online, false,
            "Landed", "Loiter", "MAVLink 2", "Healthy", "Ready", 43.73, -79.42, 500, 10,
            null, null, null, 0, 0, 0, 90, false, "MAVLINK_OK", "Ready", DateTimeOffset.UtcNow);
}
