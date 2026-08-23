using System.Globalization;
using RobotCommand.Models;

namespace RobotCommand.Services.Mavlink;

public sealed record MavlinkDiagnosticEvidence(
    string VehicleId,
    string ConnectionId,
    byte SystemId,
    byte ComponentId,
    AvailabilityState Availability,
    DateTimeOffset ObservedAt,
    DateTimeOffset LastHeartbeatAt,
    bool Supported,
    bool Armed,
    string LandedState,
    string Mode,
    string Version,
    uint? SensorsPresent,
    uint? SensorsEnabled,
    uint? SensorsHealthy,
    ushort? DropRateComm,
    ushort? CommunicationErrors,
    byte? GpsFixType,
    byte? GpsSatellites,
    double? GpsHorizontalAccuracyMetres,
    double? GpsVerticalAccuracyMetres,
    uint? EstimatorFlags,
    double? LatitudeDegrees,
    double? LongitudeDegrees,
    double? AltitudeMslMetres,
    ushort? BatteryVoltageMillivolts,
    short? BatteryCurrentCentiamps,
    sbyte? BatteryRemainingPercent,
    uint? BatteryFaults,
    float? VibrationX,
    float? VibrationY,
    float? VibrationZ,
    uint? Clipping0,
    uint? Clipping1,
    uint? Clipping2,
    double PacketLossPercent,
    double? SignalToNoiseDb,
    IReadOnlyList<VehicleDiagnosticMessage> RecentMessages,
    IReadOnlyDictionary<string, DateTimeOffset> ActiveTextBlockers);

public interface IVehicleDiagnosticsProvider
{
    string Backend { get; }

    VehicleDiagnosticsSnapshot Build(MavlinkDiagnosticEvidence evidence, DateTimeOffset now);
}

public sealed class Px4VehicleDiagnosticsProvider : IVehicleDiagnosticsProvider
{
    private const uint Gyro = 1u << 0;
    private const uint Accelerometer = 1u << 1;
    private const uint Magnetometer = 1u << 2;
    private const uint AbsolutePressure = 1u << 3;
    private const uint Gps = 1u << 5;

    private const uint EstimatorAttitude = 1u << 0;
    private const uint EstimatorHorizontalVelocity = 1u << 1;
    private const uint EstimatorVerticalVelocity = 1u << 2;
    private const uint EstimatorHorizontalAbsolute = 1u << 4;
    private const uint EstimatorVerticalAbsolute = 1u << 5;
    private const uint EstimatorGpsGlitch = 1u << 10;
    private const uint EstimatorAccelError = 1u << 11;

    private static readonly OperatorCommandKind[] ArmOperations =
        [OperatorCommandKind.Arm, OperatorCommandKind.Takeoff];
    private static readonly OperatorCommandKind[] NavigationOperations =
        [OperatorCommandKind.Takeoff, OperatorCommandKind.GoTo, OperatorCommandKind.ChangeAltitude, OperatorCommandKind.SetHeading];
    private static readonly OperatorCommandKind[] AltitudeOperations =
        [OperatorCommandKind.Takeoff, OperatorCommandKind.ChangeAltitude];

    public string Backend => "PX4";

    public VehicleDiagnosticsSnapshot Build(MavlinkDiagnosticEvidence evidence, DateTimeOffset now)
    {
        var checks = new List<VehicleDiagnosticCheck>();
        var telemetryStatus = evidence.Availability switch
        {
            AvailabilityState.Offline or AvailabilityState.Faulted => VehicleDiagnosticStatus.Offline,
            AvailabilityState.Stale => VehicleDiagnosticStatus.Stale,
            AvailabilityState.Online or AvailabilityState.Degraded => VehicleDiagnosticStatus.Ready,
            _ => VehicleDiagnosticStatus.Unknown
        };
        checks.Add(Check(
            "PX4_HEARTBEAT", "Connection", "PX4 heartbeat",
            telemetryStatus == VehicleDiagnosticStatus.Ready ? VehicleDiagnosticCheckState.Passed :
            telemetryStatus == VehicleDiagnosticStatus.Stale ? VehicleDiagnosticCheckState.Warning : VehicleDiagnosticCheckState.Failed,
            telemetryStatus == VehicleDiagnosticStatus.Ready
                ? $"Heartbeat received {(now - evidence.LastHeartbeatAt).TotalSeconds:0.0}s ago."
                : $"Heartbeat is {telemetryStatus.ToString().ToLowerInvariant()}.",
            ArmOperations.Concat(NavigationOperations).Distinct().ToArray()));

        if (!evidence.Supported)
        {
            checks.Add(Check("PX4_UNSUPPORTED", "Identity", "PX4 support", VehicleDiagnosticCheckState.Failed,
                "This MAVLink system is not a supported PX4 multicopter.", ArmOperations.Concat(NavigationOperations).Distinct().ToArray()));
        }

        AddSensorCheck(checks, evidence, Gyro, "PX4_GYRO", "Core sensors", "Gyroscope", true, ArmOperations);
        AddSensorCheck(checks, evidence, Accelerometer, "PX4_ACCEL", "Core sensors", "Accelerometer", true, ArmOperations);
        AddSensorCheck(checks, evidence, Magnetometer, "PX4_MAG", "Core sensors", "Magnetometer", false, null);
        AddSensorCheck(checks, evidence, AbsolutePressure, "PX4_BARO", "Core sensors", "Barometer", true, AltitudeOperations);
        AddSensorCheck(checks, evidence, Gps, "PX4_GPS_SENSOR", "Navigation", "GPS sensor", false, NavigationOperations);

        // PX4 does not consistently publish the SYS_STATUS controller, motor-output, or
        // pre-arm bits. Their absence is not evidence of a failed controller, disconnected
        // motor, or failed pre-arm check. Estimator/GPS data covers navigation evidence;
        // current PX4 STATUSTEXT preflight failures cover arming evidence.

        AddGpsCheck(checks, evidence);
        AddEstimatorChecks(checks, evidence);
        AddBatteryCheck(checks, evidence);
        AddVibrationCheck(checks, evidence);
        AddLinkCheck(checks, evidence);

        foreach (var blocker in evidence.ActiveTextBlockers.Where(item => now - item.Value <= TimeSpan.FromSeconds(10)))
        {
            checks.Add(Check(
                $"PX4_STATUSTEXT_{StableCode(blocker.Key)}", "Preflight", "PX4 preflight report",
                VehicleDiagnosticCheckState.Failed, blocker.Key, ArmOperations.Concat(NavigationOperations).Distinct().ToArray()));
        }

        var arm = Assess(checks, [OperatorCommandKind.Arm]);
        var navigation = Assess(checks, NavigationOperations);
        var overall = telemetryStatus switch
        {
            VehicleDiagnosticStatus.Offline => VehicleDiagnosticStatus.Offline,
            VehicleDiagnosticStatus.Stale => VehicleDiagnosticStatus.Stale,
            _ when arm.Status == VehicleDiagnosticStatus.Blocked => VehicleDiagnosticStatus.Blocked,
            _ when navigation.Status == VehicleDiagnosticStatus.Blocked => VehicleDiagnosticStatus.Limited,
            _ when arm.Status == VehicleDiagnosticStatus.Limited || navigation.Status == VehicleDiagnosticStatus.Limited => VehicleDiagnosticStatus.Limited,
            _ => VehicleDiagnosticStatus.Ready
        };
        var summary = overall switch
        {
            VehicleDiagnosticStatus.Ready => "PX4 reports the required flight and navigation evidence healthy.",
            VehicleDiagnosticStatus.Blocked => checks.First(item => item.State == VehicleDiagnosticCheckState.Failed).Detail,
            VehicleDiagnosticStatus.Limited => "PX4 is connected, but some readiness evidence is missing or degraded.",
            VehicleDiagnosticStatus.Stale => "PX4 telemetry is stale.",
            VehicleDiagnosticStatus.Offline => "PX4 is offline.",
            _ => "PX4 readiness has not been established."
        };

        return new VehicleDiagnosticsSnapshot(
            $"{evidence.VehicleId}:{evidence.ConnectionId}", evidence.VehicleId, evidence.ConnectionId, Backend,
            overall, summary,
            arm.Status, arm.Detail,
            navigation.Status, navigation.Detail,
            telemetryStatus,
            telemetryStatus == VehicleDiagnosticStatus.Ready ? "MAVLink heartbeat and telemetry are current." : summary,
            checks, evidence.RecentMessages.TakeLast(100).Reverse().ToArray(), evidence.ObservedAt,
            evidence.SystemId, evidence.ComponentId, evidence.Version, evidence.Mode, evidence.Armed, evidence.LandedState,
            evidence.BatteryRemainingPercent is >= 0 and <= 100 ? evidence.BatteryRemainingPercent : null,
            evidence.BatteryVoltageMillivolts is { } voltage ? voltage / 1000d : null);
    }

    private static void AddSensorCheck(List<VehicleDiagnosticCheck> checks, MavlinkDiagnosticEvidence evidence, uint mask,
        string code, string category, string name, bool required, IReadOnlyList<OperatorCommandKind>? affected)
    {
        if (evidence.SensorsPresent is null || evidence.SensorsEnabled is null || evidence.SensorsHealthy is null)
        {
            checks.Add(Check(code, category, name, VehicleDiagnosticCheckState.Unknown, "SYS_STATUS has not reported this sensor.", affected));
            return;
        }
        var present = (evidence.SensorsPresent.Value & mask) != 0;
        var enabled = (evidence.SensorsEnabled.Value & mask) != 0;
        var healthy = (evidence.SensorsHealthy.Value & mask) != 0;
        if (!present)
        {
            checks.Add(Check(code, category, name,
                required ? VehicleDiagnosticCheckState.Unknown : VehicleDiagnosticCheckState.NotApplicable,
                required ? "PX4 did not report this required capability as present." : "Not reported as installed.", affected));
        }
        else if (enabled && !healthy)
        {
            checks.Add(Check(code, category, name, VehicleDiagnosticCheckState.Failed, "Present and enabled, but PX4 reports it unhealthy.", affected));
        }
        else if (!enabled)
        {
            checks.Add(Check(code, category, name, required ? VehicleDiagnosticCheckState.Warning : VehicleDiagnosticCheckState.NotApplicable,
                "Present but not enabled.", affected));
        }
        else
        {
            checks.Add(Check(code, category, name, VehicleDiagnosticCheckState.Passed, "Present, enabled, and healthy.", affected));
        }
    }

    private static void AddGpsCheck(List<VehicleDiagnosticCheck> checks, MavlinkDiagnosticEvidence evidence)
    {
        if (evidence.GpsFixType is null)
        {
            checks.Add(Check("PX4_GPS_FIX", "Navigation", "GPS fix", VehicleDiagnosticCheckState.Unknown,
                "GPS_RAW_INT has not been received.", NavigationOperations));
            return;
        }
        var fix = evidence.GpsFixType.Value;
        checks.Add(Check("PX4_GPS_FIX", "Navigation", "GPS fix",
            fix >= 3 ? VehicleDiagnosticCheckState.Passed : VehicleDiagnosticCheckState.Failed,
            fix >= 3
                ? $"3D fix with {evidence.GpsSatellites?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} satellites."
                : $"GPS fix type {fix}; a 3D fix is required for global navigation.", NavigationOperations));
    }

    private static void AddEstimatorChecks(List<VehicleDiagnosticCheck> checks, MavlinkDiagnosticEvidence evidence)
    {
        if (evidence.EstimatorFlags is null)
        {
            checks.Add(Check("PX4_ESTIMATOR", "Estimator", "Estimator validity", VehicleDiagnosticCheckState.Unknown,
                "ESTIMATOR_STATUS has not been received.", NavigationOperations));
        }
        else
        {
            var flags = evidence.EstimatorFlags.Value;
            var basic = (flags & (EstimatorAttitude | EstimatorHorizontalVelocity | EstimatorVerticalVelocity)) ==
                        (EstimatorAttitude | EstimatorHorizontalVelocity | EstimatorVerticalVelocity);
            var global = (flags & (EstimatorHorizontalAbsolute | EstimatorVerticalAbsolute)) ==
                         (EstimatorHorizontalAbsolute | EstimatorVerticalAbsolute);
            var fault = (flags & (EstimatorGpsGlitch | EstimatorAccelError)) != 0;
            checks.Add(Check("PX4_ESTIMATOR", "Estimator", "Estimator validity",
                basic && global && !fault ? VehicleDiagnosticCheckState.Passed : VehicleDiagnosticCheckState.Failed,
                fault ? "PX4 reports an estimator GPS glitch or acceleration error."
                    : !basic ? "Attitude or velocity estimate is invalid."
                    : !global ? "Absolute horizontal or vertical position estimate is invalid."
                    : "Estimator reports valid attitude, velocity, and global position.", NavigationOperations));
        }

        var globalPosition = evidence.LatitudeDegrees is double lat && evidence.LongitudeDegrees is double lon &&
                             double.IsFinite(lat) && double.IsFinite(lon) &&
                             Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 &&
                             !(Math.Abs(lat) < 0.000001 && Math.Abs(lon) < 0.000001);
        checks.Add(Check("PX4_GLOBAL_POSITION", "Navigation", "Global position",
            globalPosition ? VehicleDiagnosticCheckState.Passed : VehicleDiagnosticCheckState.Failed,
            globalPosition ? $"Global position {evidence.LatitudeDegrees:0.000000}, {evidence.LongitudeDegrees:0.000000}."
                : "No valid global position has been reported.", NavigationOperations));
    }

    private static void AddBatteryCheck(List<VehicleDiagnosticCheck> checks, MavlinkDiagnosticEvidence evidence)
    {
        var voltage = evidence.BatteryVoltageMillivolts;
        var remaining = evidence.BatteryRemainingPercent;
        if (voltage is null && remaining is null)
        {
            checks.Add(Check("PX4_BATTERY", "Power", "Battery", VehicleDiagnosticCheckState.Unknown,
                "Battery status has not been reported."));
            return;
        }
        var faulted = evidence.BatteryFaults is > 0;
        var low = remaining is >= 0 and <= 20;
        checks.Add(Check("PX4_BATTERY", "Power", "Battery",
            faulted ? VehicleDiagnosticCheckState.Failed : low ? VehicleDiagnosticCheckState.Warning : VehicleDiagnosticCheckState.Passed,
            faulted ? $"PX4 reports battery fault mask 0x{evidence.BatteryFaults:X}."
                : $"{(voltage is null ? "Voltage not reported" : $"{voltage.Value / 1000d:0.00} V")}; {(remaining is null or < 0 ? "remaining not reported" : $"{remaining}% remaining")}.",
            faulted ? ArmOperations : null));
    }

    private static void AddVibrationCheck(List<VehicleDiagnosticCheck> checks, MavlinkDiagnosticEvidence evidence)
    {
        if (evidence.VibrationX is null && evidence.VibrationY is null && evidence.VibrationZ is null)
        {
            checks.Add(Check("PX4_VIBRATION", "Core sensors", "Vibration", VehicleDiagnosticCheckState.Unknown,
                "VIBRATION has not been received."));
            return;
        }
        var clipped = (evidence.Clipping0 ?? 0) + (evidence.Clipping1 ?? 0) + (evidence.Clipping2 ?? 0);
        var high = new[] { evidence.VibrationX ?? 0, evidence.VibrationY ?? 0, evidence.VibrationZ ?? 0 }.Max() > 60;
        checks.Add(Check("PX4_VIBRATION", "Core sensors", "Vibration",
            clipped > 0 || high ? VehicleDiagnosticCheckState.Warning : VehicleDiagnosticCheckState.Passed,
            clipped > 0 ? $"Accelerometer clipping count is {clipped}." : high ? "High vibration reported." : "No high vibration or clipping reported."));
    }

    private static void AddLinkCheck(List<VehicleDiagnosticCheck> checks, MavlinkDiagnosticEvidence evidence)
    {
        var severe = evidence.PacketLossPercent >= 30;
        var degraded = evidence.PacketLossPercent >= 10 || evidence.SignalToNoiseDb is < 6;
        checks.Add(Check("MAVLINK_LINK", "Radio link", "MAVLink link",
            severe ? VehicleDiagnosticCheckState.Failed : degraded ? VehicleDiagnosticCheckState.Warning : VehicleDiagnosticCheckState.Passed,
            $"Packet loss {evidence.PacketLossPercent:0.0}%; SNR {(evidence.SignalToNoiseDb is double snr ? $"{snr:0.0} dB" : "not reported")}.",
            severe ? ArmOperations.Concat(NavigationOperations).Distinct().ToArray() : null));
    }

    private static (VehicleDiagnosticStatus Status, string Detail) Assess(
        IReadOnlyList<VehicleDiagnosticCheck> checks, IReadOnlyList<OperatorCommandKind> operations)
    {
        var relevant = checks.Where(check => check.AffectedOperations?.Any(operations.Contains) == true).ToArray();
        var failure = relevant.FirstOrDefault(item => item.State == VehicleDiagnosticCheckState.Failed);
        if (failure is not null) return (VehicleDiagnosticStatus.Blocked, failure.Detail);
        var uncertain = relevant.FirstOrDefault(item => item.State is VehicleDiagnosticCheckState.Warning or VehicleDiagnosticCheckState.Unknown);
        if (uncertain is not null) return (VehicleDiagnosticStatus.Limited, uncertain.Detail);
        return (VehicleDiagnosticStatus.Ready, "Required checks passed.");
    }

    private static VehicleDiagnosticCheck Check(string code, string category, string name,
        VehicleDiagnosticCheckState state, string detail, IReadOnlyList<OperatorCommandKind>? affected = null)
        => new(code, category, name, state, detail, affected);

    private static string StableCode(string value)
        => new string(value.ToUpperInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray()).Trim('_');

}

/// <summary>
/// ArduPilot Copter diagnostics projected into the same normalized model as PX4.
/// ArduPilot does not expose the PX4 controller/pre-arm sensor bits reliably, so
/// this provider only treats evidence that ArduPilot actually reports as authoritative.
/// </summary>
public sealed class ArduPilotVehicleDiagnosticsProvider : IVehicleDiagnosticsProvider
{
    private static readonly OperatorCommandKind[] ArmOperations = [OperatorCommandKind.Arm, OperatorCommandKind.Takeoff];
    private static readonly OperatorCommandKind[] NavigationOperations =
        [OperatorCommandKind.Takeoff, OperatorCommandKind.GoTo, OperatorCommandKind.ChangeAltitude, OperatorCommandKind.SetHeading];

    public string Backend => "ArduPilot";

    public VehicleDiagnosticsSnapshot Build(MavlinkDiagnosticEvidence evidence, DateTimeOffset now)
    {
        var checks = new List<VehicleDiagnosticCheck>();
        var telemetry = evidence.Availability switch
        {
            AvailabilityState.Offline or AvailabilityState.Faulted => VehicleDiagnosticStatus.Offline,
            AvailabilityState.Stale => VehicleDiagnosticStatus.Stale,
            AvailabilityState.Online or AvailabilityState.Degraded => VehicleDiagnosticStatus.Ready,
            _ => VehicleDiagnosticStatus.Unknown
        };
        checks.Add(Check("ARDUPILOT_HEARTBEAT", "Connection", "ArduPilot heartbeat",
            telemetry == VehicleDiagnosticStatus.Ready ? VehicleDiagnosticCheckState.Passed :
            telemetry == VehicleDiagnosticStatus.Stale ? VehicleDiagnosticCheckState.Warning : VehicleDiagnosticCheckState.Failed,
            telemetry == VehicleDiagnosticStatus.Ready
                ? $"Heartbeat received {(now - evidence.LastHeartbeatAt).TotalSeconds:0.0}s ago."
                : $"Heartbeat is {telemetry.ToString().ToLowerInvariant()}.",
            ArmOperations.Concat(NavigationOperations).Distinct().ToArray()));

        if (!evidence.Supported)
        {
            checks.Add(Check("ARDUPILOT_UNSUPPORTED", "Identity", "ArduPilot support", VehicleDiagnosticCheckState.Failed,
                "This MAVLink system is not a supported ArduPilot multicopter.", ArmOperations.Concat(NavigationOperations).Distinct().ToArray()));
        }

        AddSensor(checks, evidence, 1u << 0, "ARDUPILOT_GYRO", "Gyroscope", true, ArmOperations);
        AddSensor(checks, evidence, 1u << 1, "ARDUPILOT_ACCEL", "Accelerometer", true, ArmOperations);
        AddSensor(checks, evidence, 1u << 2, "ARDUPILOT_MAG", "Magnetometer", false, null);
        AddSensor(checks, evidence, 1u << 3, "ARDUPILOT_BARO", "Barometer", true, ArmOperations);

        var gps = evidence.GpsFixType is null
            ? VehicleDiagnosticCheckState.Unknown
            : evidence.GpsFixType >= 3 ? VehicleDiagnosticCheckState.Passed : VehicleDiagnosticCheckState.Warning;
        checks.Add(Check("ARDUPILOT_GPS", "Navigation", "GPS fix", gps,
            evidence.GpsFixType is null ? "GPS_RAW_INT has not been received." :
            evidence.GpsFixType >= 3 ? $"3D fix with {evidence.GpsSatellites?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} satellites." :
            $"GPS fix type {evidence.GpsFixType}; global navigation may be unavailable.", NavigationOperations));

        var lat = evidence.LatitudeDegrees ?? double.NaN;
        var lon = evidence.LongitudeDegrees ?? double.NaN;
        var global = double.IsFinite(lat) && double.IsFinite(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 &&
                     !(Math.Abs(lat) < 0.000001 && Math.Abs(lon) < 0.000001);
        checks.Add(Check("ARDUPILOT_GLOBAL_POSITION", "Navigation", "Global position",
            global ? VehicleDiagnosticCheckState.Passed : VehicleDiagnosticCheckState.Warning,
            global ? $"Global position {lat:0.000000}, {lon:0.000000}." : "No valid global position has been reported.", NavigationOperations));

        var batteryFault = evidence.BatteryFaults is > 0;
        var batteryLow = evidence.BatteryRemainingPercent is >= 0 and <= 20;
        checks.Add(Check("ARDUPILOT_BATTERY", "Power", "Battery",
            batteryFault ? VehicleDiagnosticCheckState.Failed : batteryLow ? VehicleDiagnosticCheckState.Warning :
            evidence.BatteryVoltageMillivolts is null && evidence.BatteryRemainingPercent is null ? VehicleDiagnosticCheckState.Unknown : VehicleDiagnosticCheckState.Passed,
            batteryFault ? $"ArduPilot reports battery fault mask 0x{evidence.BatteryFaults:X}." :
            batteryLow ? $"{evidence.BatteryVoltageMillivolts / 1000d:0.00} V; {evidence.BatteryRemainingPercent}% remaining." :
            evidence.BatteryVoltageMillivolts is null && evidence.BatteryRemainingPercent is null ? "Battery status has not been reported." : "Battery telemetry is within the reported limits."));

        var linkState = evidence.PacketLossPercent >= 30 ? VehicleDiagnosticCheckState.Failed :
            evidence.PacketLossPercent >= 10 || evidence.SignalToNoiseDb is < 6 ? VehicleDiagnosticCheckState.Warning : VehicleDiagnosticCheckState.Passed;
        checks.Add(Check("ARDUPILOT_LINK", "Radio link", "MAVLink link", linkState,
            $"Packet loss {evidence.PacketLossPercent:0.0}%; SNR {(evidence.SignalToNoiseDb is double snr ? $"{snr:0.0} dB" : "not reported")}.",
            linkState == VehicleDiagnosticCheckState.Failed ? ArmOperations.Concat(NavigationOperations).Distinct().ToArray() : null));

        foreach (var blocker in evidence.ActiveTextBlockers.Where(item =>
                     now - item.Value <= TimeSpan.FromSeconds(10) ||
                     IsPersistentArduPilotBlocker(item.Key)))
        {
            checks.Add(Check($"ARDUPILOT_STATUSTEXT_{StableCode(blocker.Key)}", "Preflight", "ArduPilot preflight report",
                VehicleDiagnosticCheckState.Failed, blocker.Key, ArmOperations.Concat(NavigationOperations).Distinct().ToArray()));
        }

        var arm = Assess(checks, [OperatorCommandKind.Arm]);
        var navigation = Assess(checks, NavigationOperations);
        var overall = telemetry switch
        {
            VehicleDiagnosticStatus.Offline => VehicleDiagnosticStatus.Offline,
            VehicleDiagnosticStatus.Stale => VehicleDiagnosticStatus.Stale,
            _ when arm.Status == VehicleDiagnosticStatus.Blocked => VehicleDiagnosticStatus.Blocked,
            _ when navigation.Status == VehicleDiagnosticStatus.Blocked => VehicleDiagnosticStatus.Limited,
            _ when arm.Status == VehicleDiagnosticStatus.Limited || navigation.Status == VehicleDiagnosticStatus.Limited => VehicleDiagnosticStatus.Limited,
            _ => VehicleDiagnosticStatus.Ready
        };
        var summary = overall switch
        {
            VehicleDiagnosticStatus.Ready => "ArduPilot reports the available flight and navigation evidence healthy.",
            VehicleDiagnosticStatus.Blocked => checks.First(item => item.State == VehicleDiagnosticCheckState.Failed).Detail,
            VehicleDiagnosticStatus.Limited => "ArduPilot is connected, but some readiness evidence is missing or degraded.",
            VehicleDiagnosticStatus.Stale => "ArduPilot telemetry is stale.",
            VehicleDiagnosticStatus.Offline => "ArduPilot is offline.",
            _ => "ArduPilot readiness has not been established."
        };
        return new VehicleDiagnosticsSnapshot(
            $"{evidence.VehicleId}:{evidence.ConnectionId}", evidence.VehicleId, evidence.ConnectionId, Backend,
            overall, summary, arm.Status, arm.Detail, navigation.Status, navigation.Detail, telemetry,
            telemetry == VehicleDiagnosticStatus.Ready ? "MAVLink heartbeat and telemetry are current." : summary,
            checks, evidence.RecentMessages.TakeLast(100).Reverse().ToArray(), evidence.ObservedAt,
            evidence.SystemId, evidence.ComponentId, evidence.Version, evidence.Mode, evidence.Armed, evidence.LandedState,
            evidence.BatteryRemainingPercent is >= 0 and <= 100 ? evidence.BatteryRemainingPercent : null,
            evidence.BatteryVoltageMillivolts is { } voltage ? voltage / 1000d : null);
    }

    private static void AddSensor(List<VehicleDiagnosticCheck> checks, MavlinkDiagnosticEvidence evidence, uint mask, string code, string name, bool required, IReadOnlyList<OperatorCommandKind>? affected)
    {
        if (evidence.SensorsPresent is null || evidence.SensorsEnabled is null || evidence.SensorsHealthy is null)
        {
            checks.Add(Check(code, "Core sensors", name, VehicleDiagnosticCheckState.Unknown, "SYS_STATUS has not reported this sensor.", affected));
            return;
        }
        var present = (evidence.SensorsPresent.Value & mask) != 0;
        var enabled = (evidence.SensorsEnabled.Value & mask) != 0;
        var healthy = (evidence.SensorsHealthy.Value & mask) != 0;
        checks.Add(Check(code, "Core sensors", name,
            !present ? required ? VehicleDiagnosticCheckState.Unknown : VehicleDiagnosticCheckState.NotApplicable :
            enabled && !healthy ? VehicleDiagnosticCheckState.Failed : !enabled ? VehicleDiagnosticCheckState.Warning : VehicleDiagnosticCheckState.Passed,
            !present ? required ? "ArduPilot has not reported this required sensor as present." : "Not reported as installed." :
            enabled && !healthy ? "Present and enabled, but ArduPilot reports it unhealthy." :
            !enabled ? "Present but not enabled." : "Present, enabled, and healthy.", affected));
    }

    private static (VehicleDiagnosticStatus Status, string Detail) Assess(IReadOnlyList<VehicleDiagnosticCheck> checks, IReadOnlyList<OperatorCommandKind> operations)
    {
        var relevant = checks.Where(check => check.AffectedOperations?.Any(operations.Contains) == true).ToArray();
        var failure = relevant.FirstOrDefault(item => item.State == VehicleDiagnosticCheckState.Failed);
        if (failure is not null) return (VehicleDiagnosticStatus.Blocked, failure.Detail);
        var uncertain = relevant.FirstOrDefault(item => item.State is VehicleDiagnosticCheckState.Warning or VehicleDiagnosticCheckState.Unknown);
        return uncertain is null ? (VehicleDiagnosticStatus.Ready, "Required checks passed.") : (VehicleDiagnosticStatus.Limited, uncertain.Detail);
    }

    private static VehicleDiagnosticCheck Check(string code, string category, string name, VehicleDiagnosticCheckState state, string detail, IReadOnlyList<OperatorCommandKind>? affected = null)
        => new(code, category, name, state, detail, affected);

    private static string StableCode(string value)
        => new string(value.ToUpperInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray()).Trim('_');

    private static bool IsPersistentArduPilotBlocker(string text)
        => text.StartsWith("PreArm:", StringComparison.OrdinalIgnoreCase) ||
           text.StartsWith("Arming denied:", StringComparison.OrdinalIgnoreCase) ||
           text.StartsWith("Preflight Fail:", StringComparison.OrdinalIgnoreCase) ||
           text.StartsWith("Arm:", StringComparison.OrdinalIgnoreCase);
}
