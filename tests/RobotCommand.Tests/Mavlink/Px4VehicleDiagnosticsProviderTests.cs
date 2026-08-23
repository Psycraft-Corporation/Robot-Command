using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Operations;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class Px4VehicleDiagnosticsProviderTests
{
    private readonly Px4VehicleDiagnosticsProvider _provider = new();

    [Fact]
    public void HeartbeatAlone_IsNotReportedReadyOrHealthy()
    {
        var snapshot = _provider.Build(Evidence(), DateTimeOffset.UtcNow);

        Assert.Equal(VehicleDiagnosticStatus.Limited, snapshot.OverallStatus);
        Assert.NotEqual(VehicleDiagnosticStatus.Ready, snapshot.ArmReadiness);
        Assert.NotEqual(VehicleDiagnosticStatus.Ready, snapshot.NavigationReadiness);
        Assert.Contains(snapshot.Checks, item => item.Code == "PX4_GPS_FIX" && item.State == VehicleDiagnosticCheckState.Unknown);
    }

    [Fact]
    public void EnabledButUnhealthyCoreSensor_BlocksArming()
    {
        const uint required = (1u << 0) | (1u << 1) | (1u << 3) | (1u << 11) | (1u << 13) |
                              (1u << 14) | (1u << 15) | (1u << 25) | (1u << 28);
        var snapshot = _provider.Build(Evidence(
            sensorsPresent: required,
            sensorsEnabled: required,
            sensorsHealthy: required & ~(1u << 0)), DateTimeOffset.UtcNow);

        Assert.Equal(VehicleDiagnosticStatus.Blocked, snapshot.ArmReadiness);
        Assert.Contains(snapshot.Blockers, item => item.Code == "PX4_GYRO");
    }

    [Fact]
    public void MissingGps_BlocksNavigationWithoutCallingArmReadinessBlocked()
    {
        const uint required = (1u << 0) | (1u << 1) | (1u << 3) | (1u << 11) | (1u << 13) |
                              (1u << 14) | (1u << 15) | (1u << 25) | (1u << 28);
        var snapshot = _provider.Build(Evidence(
            sensorsPresent: required,
            sensorsEnabled: required,
            sensorsHealthy: required,
            gpsFixType: 1,
            estimatorFlags: 7), DateTimeOffset.UtcNow);

        Assert.NotEqual(VehicleDiagnosticStatus.Blocked, snapshot.ArmReadiness);
        Assert.Equal(VehicleDiagnosticStatus.Blocked, snapshot.NavigationReadiness);
        Assert.Equal(VehicleDiagnosticStatus.Limited, snapshot.OverallStatus);
    }

    [Fact]
    public void ActivePreflightStatusText_BlocksArmAndUnsafeNavigationButNotRecoveryCommands()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _provider.Build(Evidence(activeBlockers: new Dictionary<string, DateTimeOffset>
        {
            ["Preflight Fail: GPS missing"] = now
        }), now);
        var vehicle = Vehicle();
        var telemetry = Telemetry();
        var connection = Connection();

        Assert.Contains(OperatorControlRules.Evaluate(vehicle, telemetry, connection, OperatorCommandKind.Arm,
            diagnostics: snapshot), item => item.Code.StartsWith("PX4_STATUSTEXT_", StringComparison.Ordinal));
        Assert.DoesNotContain(OperatorControlRules.Evaluate(vehicle, telemetry, connection, OperatorCommandKind.Hold,
            diagnostics: snapshot), item => item.Code.StartsWith("PX4_STATUSTEXT_", StringComparison.Ordinal));
        Assert.DoesNotContain(OperatorControlRules.Evaluate(vehicle, telemetry, connection, OperatorCommandKind.Recover,
            diagnostics: snapshot), item => item.Code.StartsWith("PX4_STATUSTEXT_", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthyEvidence_ProducesReadyAssessment()
    {
        const uint sensors = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 5) |
                             (1u << 25);
        const uint estimator = 1u | 2u | 4u | 16u | 32u;
        var snapshot = _provider.Build(Evidence(
            sensors, sensors, sensors, 3, estimator, 43.7, -79.4,
            batteryVoltage: 16000, batteryRemaining: 80), DateTimeOffset.UtcNow);

        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.OverallStatus);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.ArmReadiness);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.NavigationReadiness);
    }

    [Fact]
    public void LowBattery_IsVisibleWarningButDoesNotBlockVehicleOperations()
    {
        const uint sensors = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 5) |
                             (1u << 25);
        const uint estimator = 1u | 2u | 4u | 16u | 32u;
        var snapshot = _provider.Build(Evidence(
            sensors, sensors, sensors, 3, estimator, 43.7, -79.4,
            batteryVoltage: 22070, batteryRemaining: 20), DateTimeOffset.UtcNow);

        Assert.Equal(VehicleDiagnosticCheckState.Warning,
            snapshot.Checks.Single(item => item.Code == "PX4_BATTERY").State);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.ArmReadiness);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.NavigationReadiness);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.OverallStatus);

        var armFindings = OperatorControlRules.Evaluate(
            Vehicle(), Telemetry(), Connection(), OperatorCommandKind.Arm, diagnostics: snapshot);
        Assert.DoesNotContain(armFindings, item => item.Code == "PX4_BATTERY");
    }

    [Fact]
    public void VibrationAndLinkWarnings_AreVisibleWithoutLimitingReadiness()
    {
        const uint sensors = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 5) |
                             (1u << 25);
        const uint estimator = 1u | 2u | 4u | 16u | 32u;
        var evidence = Evidence(
            sensors, sensors, sensors, 3, estimator, 43.7, -79.4,
            batteryVoltage: 16000, batteryRemaining: 80) with
        {
            VibrationX = 0,
            Clipping0 = 504,
            PacketLossPercent = 10.5
        };

        var snapshot = _provider.Build(evidence, DateTimeOffset.UtcNow);

        Assert.Equal(VehicleDiagnosticCheckState.Warning,
            snapshot.Checks.Single(item => item.Code == "PX4_VIBRATION").State);
        Assert.Equal(VehicleDiagnosticCheckState.Warning,
            snapshot.Checks.Single(item => item.Code == "MAVLINK_LINK").State);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.OverallStatus);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.ArmReadiness);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.NavigationReadiness);
    }

    [Fact]
    public void UnpublishedControllerAndPrearmBits_DoNotCreateMisleadingRequiredFailures()
    {
        const uint sensors = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 5) | (1u << 25);
        const uint estimator = 1u | 2u | 4u | 16u | 32u;

        var snapshot = _provider.Build(Evidence(
            sensors, sensors, sensors, 3, estimator, 43.7, -79.4,
            batteryVoltage: 16000, batteryRemaining: 80), DateTimeOffset.UtcNow);

        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.ArmReadiness);
        Assert.Equal(VehicleDiagnosticStatus.Ready, snapshot.NavigationReadiness);
        Assert.DoesNotContain(snapshot.Checks, item => item.Code is
            "PX4_ATTITUDE_CONTROL" or "PX4_ALTITUDE_CONTROL" or "PX4_MOTOR_OUTPUTS" or "PX4_PREARM");
    }

    [Fact]
    public void RecentDiagnosticHistory_IsBoundedToOneHundredMessages()
    {
        var now = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, 120)
            .Select(index => new VehicleDiagnosticMessage(
                $"message-{index}",
                now.AddSeconds(index),
                VehicleDiagnosticSeverity.Warning,
                $"warning-{index}",
                "PX4"))
            .ToArray();
        var snapshot = _provider.Build(Evidence(messages: messages), now.AddMinutes(3));

        Assert.Equal(100, snapshot.RecentMessages.Count);
        Assert.Equal("warning-119", snapshot.RecentMessages[0].Text);
        Assert.Equal("warning-20", snapshot.RecentMessages[^1].Text);
    }

    private static MavlinkDiagnosticEvidence Evidence(
        uint? sensorsPresent = null,
        uint? sensorsEnabled = null,
        uint? sensorsHealthy = null,
        byte? gpsFixType = null,
        uint? estimatorFlags = null,
        double? latitude = null,
        double? longitude = null,
        ushort? batteryVoltage = null,
        sbyte? batteryRemaining = null,
        IReadOnlyDictionary<string, DateTimeOffset>? activeBlockers = null,
        IReadOnlyList<VehicleDiagnosticMessage>? messages = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new MavlinkDiagnosticEvidence(
            "vehicle", "connection", 1, 1, AvailabilityState.Online, now, now, true, false,
            "Landed", "Position", "1.0.0", sensorsPresent, sensorsEnabled, sensorsHealthy,
            null, null, gpsFixType, gpsFixType is >= 3 ? (byte)10 : null, null, null,
            estimatorFlags, latitude, longitude, latitude is null ? null : 100,
            batteryVoltage, null, batteryRemaining, null, null, null, null, null, null, null,
            0, null, messages ?? [], activeBlockers ?? new Dictionary<string, DateTimeOffset>());
    }

    private static VehicleRecord Vehicle() => new(
        "vehicle", "PX4", ["connection"], null, null, "Multicopter", "Air", "px4",
        AvailabilityState.Online, CapabilityKeys: ["arm", "hold", "return_home"]);

    private static ConnectionRecord Connection() => new(
        "connection", "PX4", "serial://COM3", ConnectionMode.Mavlink, AvailabilityState.Online, false);

    private static VehicleTelemetryRecord Telemetry() => new(
        "telemetry", "vehicle", "connection", null, AvailabilityState.Online, false, "Landed", "Position",
        "MAVLink 2", "Limited", "Limited", 43.7, -79.4, 100, 0, null, null, null, 0, 0, 0, 90,
        false, "MAVLINK_LIMITED", "Diagnostics incomplete", DateTimeOffset.UtcNow);
}
