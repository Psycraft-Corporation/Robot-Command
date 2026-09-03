using RobotCommand.Models;
using RobotCommand.Services.Operations;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperatorControlRulesTests
{
    [Fact]
    public void CameraAndGimbalCommandsDoNotRequireArmedOrNavigationState()
    {
        var findings = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["camera_photo", "camera_video", "gimbal"]),
            Telemetry(armed: false, landedState: "Landed"),
            Connection(),
            OperatorCommandKind.SetGimbal,
            new OperatorCommandParameters(GimbalPitchDegrees: -90, GimbalYawDegrees: 180));

        Assert.DoesNotContain(findings, item => item.Severity == OperatorPreflightSeverity.Blocking);
        Assert.Equal(OperatorCommandSafety.Routine, OperatorControlRules.SafetyFor(OperatorCommandKind.SetGimbal, null));
    }

    [Fact]
    public void Arm_IsAllowedWithWarning_WhenTelemetryIsStale()
    {
        var findings = OperatorControlRules.Evaluate(
            Vehicle(),
            Telemetry(armed: false, landedState: "Landed", isStale: true),
            Connection(),
            OperatorCommandKind.Arm);

        Assert.Contains(findings, item =>
            item.Code == "TELEMETRY_STALE" &&
            item.Severity == OperatorPreflightSeverity.Warning);
        Assert.DoesNotContain(findings, item => item.Severity == OperatorPreflightSeverity.Blocking);
    }

    [Fact]
    public void Disarm_RequiresConfirmation_WhileAirborne()
    {
        var findings = OperatorControlRules.Evaluate(
            Vehicle(),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.Disarm);

        Assert.Contains(findings, item => item.Code == "AIRBORNE_DISARM_CONFIRMATION_REQUIRED");
        Assert.Equal(
            OperatorCommandSafety.Critical,
            OperatorControlRules.SafetyFor(
                OperatorCommandKind.Disarm,
                Telemetry(armed: true, landedState: "InAir")));
    }

    [Fact]
    public void ConfirmedDisarm_IsAllowed_WhileAirborne()
    {
        var findings = OperatorControlRules.Evaluate(
            Vehicle(),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.Disarm,
            new OperatorCommandParameters(AirborneDisarmConfirmed: true));

        Assert.DoesNotContain(findings, item => item.Severity == OperatorPreflightSeverity.Blocking);
    }

    [Fact]
    public void Takeoff_RequiresArmedLandedAirVehicleAndAltitude()
    {
        var ready = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "Landed"),
            Connection(),
            OperatorCommandKind.Takeoff,
            new OperatorCommandParameters(5));
        var disarmed = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: false, landedState: "Landed"),
            Connection(),
            OperatorCommandKind.Takeoff,
            new OperatorCommandParameters(5));
        var missingAltitude = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "Landed"),
            Connection(),
            OperatorCommandKind.Takeoff,
            OperatorCommandParameters.None);

        Assert.DoesNotContain(ready, item => item.Severity == OperatorPreflightSeverity.Blocking);
        Assert.Contains(disarmed, item => item.Code == "TAKEOFF_REQUIRES_ARMED");
        Assert.Contains(missingAltitude, item => item.Code == "TAKEOFF_ALTITUDE_REQUIRED");
        Assert.Equal("Take off", OperatorControlRules.DisplayName(OperatorCommandKind.Takeoff));
        Assert.Equal(
            "TAKE OFF Dracula",
            OperatorControlRules.ConfirmationPhrase(
                OperatorCommandKind.Takeoff,
                "Dracula",
                requireTypedConfirmation: true));
    }

    [Fact]
    public void GoTo_RequiresValidTargetAndAirborneAirVehicle()
    {
        var ready = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.GoTo,
            OperatorCommandParameters.GlobalGoTo(43.65, -79.38, 120, 2));
        var landed = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "Landed"),
            Connection(),
            OperatorCommandKind.GoTo,
            OperatorCommandParameters.GlobalGoTo(43.65, -79.38, 120, 2));
        var invalid = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.GoTo,
            OperatorCommandParameters.GlobalGoTo(100, -79.38, 120, 0));

        Assert.DoesNotContain(ready, item => item.Severity == OperatorPreflightSeverity.Blocking);
        Assert.Contains(landed, item => item.Code == "GO_TO_REQUIRES_AIRBORNE");
        Assert.Contains(invalid, item => item.Code == "GO_TO_LATITUDE_INVALID");
        Assert.Contains(invalid, item => item.Code == "GO_TO_ACCEPTANCE_RADIUS_INVALID");
        Assert.Equal("Go to", OperatorControlRules.DisplayName(OperatorCommandKind.GoTo));
        Assert.Equal(
            "GO TO Dracula",
            OperatorControlRules.ConfirmationPhrase(
                OperatorCommandKind.GoTo,
                "Dracula",
                requireTypedConfirmation: true));
    }

    [Fact]
    public void Land_IsBlocked_ForNonAirVehicle()
    {
        var findings = OperatorControlRules.Evaluate(
            Vehicle(domain: "Ground", capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "Moving"),
            Connection(),
            OperatorCommandKind.Land);

        Assert.Contains(findings, item => item.Code == "LAND_NOT_APPLICABLE");
    }

    [Fact]
    public void Hold_CanPassLocalPreflight_ForCurrentArmedVehicle()
    {
        var findings = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.Hold);

        Assert.DoesNotContain(findings, item => item.Severity == OperatorPreflightSeverity.Blocking);
        Assert.Equal(OperatorCommandSafety.Routine, OperatorControlRules.SafetyFor(OperatorCommandKind.Hold, null));
    }

    [Fact]
    public void TypedConfirmation_IncludesCommandAndVehicleName()
    {
        Assert.Equal(
            "LAND Dracula",
            OperatorControlRules.ConfirmationPhrase(
                OperatorCommandKind.Land,
                "Dracula",
                requireTypedConfirmation: true));
        Assert.Equal(
            string.Empty,
            OperatorControlRules.ConfirmationPhrase(
                OperatorCommandKind.Hold,
                "Dracula",
                requireTypedConfirmation: true));
    }


    [Fact]
    public void Recover_IsPresentedAsReturnHome()
    {
        Assert.Equal("Return home", OperatorControlRules.DisplayName(OperatorCommandKind.Recover));
        Assert.Equal(
            "RETURN HOME Dracula",
            OperatorControlRules.ConfirmationPhrase(
                OperatorCommandKind.Recover,
                "Dracula",
                requireTypedConfirmation: true));
    }

    [Fact]
    public void ChangeAltitude_RequiresAirborneVehicleAndValidTarget()
    {
        var ready = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.ChangeAltitude,
            OperatorCommandParameters.ChangeAltitudeRelative(5));
        var landed = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "Landed"),
            Connection(),
            OperatorCommandKind.ChangeAltitude,
            OperatorCommandParameters.ChangeAltitudeAgl(20));
        var invalid = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.ChangeAltitude,
            OperatorCommandParameters.ChangeAltitudeRelative(0));

        Assert.DoesNotContain(ready, item => item.Severity == OperatorPreflightSeverity.Blocking);
        Assert.Contains(landed, item => item.Code == "CHANGE_ALTITUDE_REQUIRES_AIRBORNE");
        Assert.Contains(invalid, item => item.Code == "CHANGE_ALTITUDE_DELTA_INVALID");
        Assert.Equal(
            "CHANGE ALTITUDE Dracula",
            OperatorControlRules.ConfirmationPhrase(
                OperatorCommandKind.ChangeAltitude,
                "Dracula",
                requireTypedConfirmation: true));
    }

    [Fact]
    public void SetHeading_ValidatesAbsoluteAndRelativeTargets()
    {
        var absolute = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.SetHeading,
            OperatorCommandParameters.AbsoluteHeading(270));
        var relative = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.SetHeading,
            OperatorCommandParameters.RelativeYaw(-90));
        var invalid = OperatorControlRules.Evaluate(
            Vehicle(capabilities: ["operator_control"]),
            Telemetry(armed: true, landedState: "InAir"),
            Connection(),
            OperatorCommandKind.SetHeading,
            OperatorCommandParameters.AbsoluteHeading(360));

        Assert.DoesNotContain(absolute, item => item.Severity == OperatorPreflightSeverity.Blocking);
        Assert.DoesNotContain(relative, item => item.Severity == OperatorPreflightSeverity.Blocking);
        Assert.Contains(invalid, item => item.Code == "SET_HEADING_ABSOLUTE_INVALID");
        Assert.Equal("Set heading", OperatorControlRules.DisplayName(OperatorCommandKind.SetHeading));
    }

    private static VehicleRecord Vehicle(
        string domain = "Air",
        IReadOnlyList<string>? capabilities = null)
        => new(
            "vehicle-1",
            "Dracula",
            ["connection-1"],
            "logos-1",
            null,
            "Multicopter",
            domain,
            "default",
            AvailabilityState.Online,
            Readiness: "Ready",
            Lifecycle: "Idle",
            ArmState: "Disarmed",
            Health: "Healthy",
            CapabilityKeys: capabilities ?? ["operator_control"],
            LastSeen: DateTimeOffset.UtcNow);

    private static ConnectionRecord Connection()
        => new(
            "connection-1",
            "Local Logos",
            "http://localhost:50051",
            ConnectionMode.Direct,
            AvailabilityState.Online,
            true,
            "logos-1",
            "unit",
            DateTimeOffset.UtcNow);

    private static VehicleTelemetryRecord Telemetry(
        bool armed,
        string landedState,
        bool isStale = false)
        => new(
            "connection-1:vehicle-1",
            "vehicle-1",
            "connection-1",
            "logos-1",
            isStale ? AvailabilityState.Stale : AvailabilityState.Online,
            armed,
            landedState,
            "Multicopter",
            "Active",
            "Healthy",
            "Ready",
            43.0,
            -79.0,
            100,
            5,
            0,
            0,
            -5,
            0,
            0,
            0,
            0,
            isStale,
            "OK",
            string.Empty,
            DateTimeOffset.UtcNow);
}
