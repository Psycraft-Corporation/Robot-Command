using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class ArduPilotVehicleDiagnosticsProviderTests
{
    private readonly ArduPilotVehicleDiagnosticsProvider _provider = new();

    [Fact]
    public void HeartbeatWithoutEvidenceIsLimitedAndNotReady()
    {
        var snapshot = _provider.Build(Evidence(), DateTimeOffset.UtcNow);

        Assert.Equal("ArduPilot", snapshot.Backend);
        Assert.Equal(Models.VehicleDiagnosticStatus.Limited, snapshot.OverallStatus);
        Assert.NotEqual(Models.VehicleDiagnosticStatus.Ready, snapshot.ArmReadiness);
        Assert.Contains(snapshot.Checks, item => item.Code == "ARDUPILOT_GPS" && item.State == Models.VehicleDiagnosticCheckState.Unknown);
    }

    [Fact]
    public void LowBatteryIsWarningAndDoesNotBlockArm()
    {
        var sensors = (uint)((1 << 0) | (1 << 1) | (1 << 2) | (1 << 3));
        var snapshot = _provider.Build(Evidence(sensors, sensors, sensors, 3, 43.7, -79.4, 22000, 20), DateTimeOffset.UtcNow);

        Assert.Equal(Models.VehicleDiagnosticCheckState.Warning, snapshot.Checks.Single(item => item.Code == "ARDUPILOT_BATTERY").State);
        Assert.DoesNotContain(snapshot.Blockers, item => item.Code == "ARDUPILOT_BATTERY");
    }

    [Fact]
    public void PreArmTextIsAnActiveBlocker()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _provider.Build(Evidence(active: new Dictionary<string, DateTimeOffset>
        {
            ["PreArm: GPS not healthy"] = now
        }), now);

        Assert.Contains(snapshot.Blockers, item => item.Code.StartsWith("ARDUPILOT_STATUSTEXT_", StringComparison.Ordinal));
    }

    [Fact]
    public void StalePreArmTextIsRemovedAndRecoveryCommandsRemainAvailable()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _provider.Build(Evidence(active: new Dictionary<string, DateTimeOffset>
        {
            ["PreArm: GPS not healthy"] = now.AddSeconds(-11)
        }), now);

        Assert.DoesNotContain(snapshot.Blockers, item => item.Detail == "PreArm: GPS not healthy");
        var preflight = snapshot.Checks.SingleOrDefault(item => item.Code.Contains("STATUSTEXT", StringComparison.Ordinal));
        Assert.Null(preflight);
    }

    [Fact]
    public void PreArmBlockerAffectsUnsafeCommandsButNotRecoveryCommands()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _provider.Build(Evidence(active: new Dictionary<string, DateTimeOffset>
        {
            ["PreArm: GPS not healthy"] = now
        }), now);

        var blocker = Assert.Single(snapshot.Blockers, item => item.Detail == "PreArm: GPS not healthy");
        Assert.NotNull(blocker.AffectedOperations);
        Assert.Contains(Models.OperatorCommandKind.Arm, blocker.AffectedOperations!);
        Assert.Contains(Models.OperatorCommandKind.Takeoff, blocker.AffectedOperations!);
        Assert.Contains(Models.OperatorCommandKind.GoTo, blocker.AffectedOperations!);
        Assert.Contains(Models.OperatorCommandKind.ChangeAltitude, blocker.AffectedOperations!);
        Assert.Contains(Models.OperatorCommandKind.SetHeading, blocker.AffectedOperations!);
        Assert.DoesNotContain(Models.OperatorCommandKind.Hold, blocker.AffectedOperations!);
        Assert.DoesNotContain(Models.OperatorCommandKind.Recover, blocker.AffectedOperations!);
        Assert.DoesNotContain(Models.OperatorCommandKind.Land, blocker.AffectedOperations!);
        Assert.DoesNotContain(Models.OperatorCommandKind.Disarm, blocker.AffectedOperations!);
    }

    private static MavlinkDiagnosticEvidence Evidence(
        uint? present = null, uint? enabled = null, uint? healthy = null, byte? gps = null,
        double? lat = null, double? lon = null, ushort? voltage = null, sbyte? remaining = null,
        IReadOnlyDictionary<string, DateTimeOffset>? active = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new MavlinkDiagnosticEvidence(
            "vehicle", "connection", 1, 1, Models.AvailabilityState.Online, now, now, true, false,
            "Landed", "Loiter", "4.5", present, enabled, healthy,
            null, null, gps, gps is >= 3 ? (byte)12 : null, null, null,
            null, lat, lon, lat is null ? null : 500,
            voltage, null, remaining, null, null, null, null, null, null, null,
            0, null, [], active ?? new Dictionary<string, DateTimeOffset>());
    }
}
