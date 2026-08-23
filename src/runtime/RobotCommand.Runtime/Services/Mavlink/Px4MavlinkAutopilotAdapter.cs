using RobotCommand.Models;

namespace RobotCommand.Services.Mavlink;

public sealed record MavlinkCommandEnvelope(
    ushort CommandId,
    float[] Parameters,
    string Description,
    MavlinkWireKind WireKind = MavlinkWireKind.CommandLong,
    byte Frame = 0,
    int X = 0,
    int Y = 0,
    float Z = 0,
    byte BaseMode = 0,
    uint CustomMode = 0);

public enum MavlinkWireKind
{
    CommandLong,
    CommandInt,
    SetMode
}

public interface IMavlinkAutopilotAdapter
{
    MavlinkAutopilotProfile Profile { get; }

    string DisplayName { get; }

    bool Supports(byte mavAutopilot, byte mavType);

    IReadOnlyList<string> CapabilityKeys { get; }

    string VehicleClass(byte mavType);

    string Domain(byte mavType);

    string DecodeMode(uint customMode);

    MavlinkCommandEnvelope BuildCommand(
        OperatorCommandRequest request,
        VehicleTelemetryRecord telemetry);

    bool SupportsManualControl(string mode);

    ManualControlModeClass ClassifyManualControlMode(string mode);

    bool IsHoldMode(uint customMode);

    bool SupportsMissionExecution { get; }

    uint MissionMode { get; }

    uint HoldMode { get; }

    string MissionModeName { get; }

    string HoldModeName { get; }

    MavlinkHoldPolicy HoldPolicy { get; }
}

public enum ManualControlModeClass
{
    Unsupported,
    GpsIndependent,
    PositionAssisted
}

/// <summary>
/// Backend-specific evidence required before Hold is reported complete.
/// PX4 uses mode confirmation; ArduCopter Brake also requires stable
/// position and altitude telemetry.
/// </summary>
public sealed record MavlinkHoldPolicy(
    bool RequiresStableTelemetry,
    TimeSpan StabilityWindow,
    double PositionToleranceMetres,
    double AltitudeToleranceMetres,
    double MaximumHorizontalSpeedMetresPerSecond,
    double MaximumVerticalSpeedMetresPerSecond,
    string StrategyName);

public sealed class Px4MavlinkAutopilotAdapter : IMavlinkAutopilotAdapter
{
    private static readonly IReadOnlyList<string> Capabilities =
    [
        "operator_control",
        "arm",
        "disarm",
        "hold",
        "takeoff",
        "go_to",
        "land",
        "return_home",
        "change_altitude",
        "set_heading"
    ];

    public MavlinkAutopilotProfile Profile => MavlinkAutopilotProfile.Px4;

    public string DisplayName => "PX4";

    public IReadOnlyList<string> CapabilityKeys => Capabilities;

    public bool SupportsManualControl(string mode)
        => mode is "Manual" or "Altitude" or "Position" or "Stabilized" or "Hold";

    public ManualControlModeClass ClassifyManualControlMode(string mode)
        => SupportsManualControl(mode)
            ? ManualControlModeClass.PositionAssisted
            : ManualControlModeClass.Unsupported;

    public bool IsHoldMode(uint customMode) => DecodeMode(customMode) == "Hold";

    public bool SupportsMissionExecution => true;

    public uint MissionMode => MavlinkValues.Px4AutoMissionCustomMode;

    public uint HoldMode => MavlinkValues.Px4AutoLoiterCustomMode;

    public string MissionModeName => "Mission";

    public string HoldModeName => "Hold";

    public MavlinkHoldPolicy HoldPolicy => new(
        RequiresStableTelemetry: false,
        StabilityWindow: TimeSpan.Zero,
        PositionToleranceMetres: 0,
        AltitudeToleranceMetres: 0,
        MaximumHorizontalSpeedMetresPerSecond: double.PositiveInfinity,
        MaximumVerticalSpeedMetresPerSecond: double.PositiveInfinity,
        StrategyName: "PX4 Hold mode confirmation");

    public bool Supports(byte mavAutopilot, byte mavType)
        => mavAutopilot == MavlinkValues.MavAutopilotPx4 && IsMulticopter(mavType);

    public string VehicleClass(byte mavType)
        => IsMulticopter(mavType) ? "Multicopter" : $"MAV_TYPE_{mavType}";

    public string Domain(byte mavType)
        => IsMulticopter(mavType) ? "Air" : "Unknown";

    public string DecodeMode(uint customMode)
    {
        var mainMode = (byte)((customMode >> 16) & 0xff);
        var subMode = (byte)((customMode >> 24) & 0xff);
        return (mainMode, subMode) switch
        {
            (1, _) => "Manual",
            (2, _) => "Altitude",
            (3, _) => "Position",
            (4, 2) => "Takeoff",
            (4, 3) => "Hold",
            (4, 4) => "Mission",
            (4, 5) => "Return",
            (4, 6) => "Land",
            (4, _) => "Auto",
            (5, _) => "Acro",
            (6, _) => "Offboard",
            (7, _) => "Stabilized",
            _ => $"PX4 mode 0x{customMode:X8}"
        };
    }

    public MavlinkCommandEnvelope BuildCommand(
        OperatorCommandRequest request,
        VehicleTelemetryRecord telemetry)
    {
        var parameters = request.Parameters ?? OperatorCommandParameters.None;
        return request.Command switch
        {
            OperatorCommandKind.Arm => Command(
                MavlinkCommandIds.ComponentArmDisarm,
                "Arm",
                1f, 0f, 0f, 0f, 0f, 0f, 0f),
            OperatorCommandKind.Disarm => Command(
                MavlinkCommandIds.ComponentArmDisarm,
                "Disarm",
                0f, 0f, 0f, 0f, 0f, 0f, 0f),
            // Match QGroundControl's PX4 pause path: DO_REPOSITION with the
            // change-mode flag asks PX4 to enter its position hold/loiter mode
            // without changing the current position or altitude target.
            OperatorCommandKind.Hold => Command(
                MavlinkCommandIds.DoReposition,
                "PX4 Hold",
                -1f, 1f, 0f, float.NaN, float.NaN, float.NaN, float.NaN),
            OperatorCommandKind.Takeoff => BuildTakeoff(parameters, telemetry),
            OperatorCommandKind.Land => Command(
                MavlinkCommandIds.NavLand,
                "Land",
                0f, 0f, 0f, float.NaN,
                Required(telemetry.LatitudeDegrees, "Land requires latitude telemetry."),
                Required(telemetry.LongitudeDegrees, "Land requires longitude telemetry."),
                0f),
            OperatorCommandKind.Recover => Command(
                MavlinkCommandIds.NavReturnToLaunch,
                "Return to launch",
                0f, 0f, 0f, 0f, 0f, 0f, 0f),
            OperatorCommandKind.GoTo => BuildReposition(parameters, telemetry, "Go To"),
            OperatorCommandKind.ChangeAltitude => BuildAltitude(parameters, telemetry),
            OperatorCommandKind.SetHeading => BuildHeading(parameters, telemetry),
            _ => throw new NotSupportedException($"PX4 MAVLink does not support {request.Command}.")
        };
    }

    private static MavlinkCommandEnvelope BuildTakeoff(
        OperatorCommandParameters parameters,
        VehicleTelemetryRecord telemetry)
    {
        var currentMsl = Required(telemetry.AltitudeMslMetres, "Takeoff requires MSL altitude telemetry.");
        var currentAgl = (float)(telemetry.AltitudeAglMetres ?? 0);
        var targetAgl = Required(parameters.TakeoffAltitudeAglMetres, "Takeoff requires a target AGL altitude.");
        var targetMsl = currentMsl - currentAgl + targetAgl;
        return Command(
            MavlinkCommandIds.NavTakeoff,
            "Takeoff",
            0f, 0f, 0f, float.NaN,
            Required(telemetry.LatitudeDegrees, "Takeoff requires latitude telemetry."),
            Required(telemetry.LongitudeDegrees, "Takeoff requires longitude telemetry."),
            targetMsl);
    }

    private static MavlinkCommandEnvelope BuildReposition(
        OperatorCommandParameters parameters,
        VehicleTelemetryRecord telemetry,
        string description)
    {
        if (parameters.GoToTargetKind != OperatorGoToTargetKind.GlobalWgs84)
        {
            throw new NotSupportedException("PX4 MAVLink v1 supports global WGS84 Go To targets only.");
        }

        return Command(
            MavlinkCommandIds.DoReposition,
            description,
            float.NaN,
            1f,
            float.NaN,
            parameters.GoToYawDegrees is { } yaw ? (float)yaw : float.NaN,
            Required(parameters.GoToLatitudeDegrees, "Go To requires latitude."),
            Required(parameters.GoToLongitudeDegrees, "Go To requires longitude."),
            Required(parameters.GoToAltitudeAmslMetres, "Go To requires AMSL altitude."));
    }

    private static MavlinkCommandEnvelope BuildAltitude(
        OperatorCommandParameters parameters,
        VehicleTelemetryRecord telemetry)
    {
        var currentMsl = Required(telemetry.AltitudeMslMetres, "Change altitude requires MSL altitude telemetry.");
        var currentAgl = Required(telemetry.AltitudeAglMetres, "Change altitude requires AGL altitude telemetry.");
        var targetMsl = parameters.AltitudeTargetKind switch
        {
            OperatorAltitudeTargetKind.AltitudeAmsl =>
                Required(parameters.AltitudeAmslMetres, "An AMSL altitude is required."),
            OperatorAltitudeTargetKind.AltitudeAgl =>
                currentMsl - currentAgl +
                Required(parameters.AltitudeAglMetres, "An AGL altitude is required."),
            OperatorAltitudeTargetKind.RelativeDelta =>
                currentMsl +
                Required(parameters.AltitudeRelativeDeltaMetres, "A relative altitude delta is required."),
            _ => throw new InvalidOperationException("An altitude target kind is required.")
        };

        return Command(
            MavlinkCommandIds.DoReposition,
            "Change altitude",
            float.NaN, 1f, float.NaN, float.NaN,
            Required(telemetry.LatitudeDegrees, "Change altitude requires latitude telemetry."),
            Required(telemetry.LongitudeDegrees, "Change altitude requires longitude telemetry."),
            targetMsl);
    }

    private static MavlinkCommandEnvelope BuildHeading(
        OperatorCommandParameters parameters,
        VehicleTelemetryRecord telemetry)
    {
        var currentHeading = Required(telemetry.HeadingDegrees, "Set heading requires heading telemetry.");
        var targetHeading = parameters.HeadingTargetKind switch
        {
            OperatorHeadingTargetKind.AbsoluteHeading =>
                Required(parameters.HeadingDegrees, "An absolute heading is required."),
            OperatorHeadingTargetKind.RelativeYaw =>
                NormalizeHeading(currentHeading +
                    Required(parameters.RelativeYawDegrees, "A relative yaw is required.")),
            _ => throw new InvalidOperationException("A heading target kind is required.")
        };

        return Command(
            MavlinkCommandIds.DoReposition,
            "Set heading",
            float.NaN, 1f, float.NaN, targetHeading,
            Required(telemetry.LatitudeDegrees, "Set heading requires latitude telemetry."),
            Required(telemetry.LongitudeDegrees, "Set heading requires longitude telemetry."),
            Required(telemetry.AltitudeMslMetres, "Set heading requires MSL altitude telemetry."));
    }

    private static MavlinkCommandEnvelope Command(
        ushort command,
        string description,
        params float[] parameters)
        => new(command, parameters, description);

    private static float Required(double? value, string message)
        => value is { } actual && double.IsFinite(actual)
            ? (float)actual
            : throw new InvalidOperationException(message);

    private static float NormalizeHeading(double heading)
        => (float)((heading % 360 + 360) % 360);

    private static bool IsMulticopter(byte mavType)
        => mavType is 2 or 3 or 4 or 13 or 14 or 15 or 29;
}

public sealed class ArduPilotMavlinkAutopilotAdapter : IMavlinkAutopilotAdapter
{
    private static readonly IReadOnlyList<string> Capabilities =
    [
        "operator_control",
        "arm",
        "disarm",
        "hold",
        "takeoff",
        "go_to",
        "land",
        "return_home",
        "change_altitude",
        "set_heading"
    ];

    public MavlinkAutopilotProfile Profile => MavlinkAutopilotProfile.ArduPilot;

    public string DisplayName => "ArduPilot";

    public IReadOnlyList<string> CapabilityKeys => Capabilities;

    public bool Supports(byte mavAutopilot, byte mavType)
        => mavAutopilot == MavlinkValues.MavAutopilotArduPilot && IsMulticopter(mavType);

    public string VehicleClass(byte mavType)
        => IsMulticopter(mavType) ? "Multicopter" : $"MAV_TYPE_{mavType}";

    public string Domain(byte mavType)
        => IsMulticopter(mavType) ? "Air" : "Unknown";

    public bool SupportsManualControl(string mode)
        => ClassifyManualControlMode(mode) != ManualControlModeClass.Unsupported;

    public ManualControlModeClass ClassifyManualControlMode(string mode)
        => mode switch
        {
            "Stabilize" or "Acro" or "AltHold" or "Sport" => ManualControlModeClass.GpsIndependent,
            "Loiter" or "PosHold" => ManualControlModeClass.PositionAssisted,
            // Guided is an automated navigation mode, not a manual stick mode.
            _ => ManualControlModeClass.Unsupported
        };

    public bool IsHoldMode(uint customMode) => customMode == MavlinkValues.ArduPilotBrakeCustomMode;

    public bool SupportsMissionExecution => true;

    public uint MissionMode => MavlinkValues.ArduPilotAutoCustomMode;

    public uint HoldMode => MavlinkValues.ArduPilotBrakeCustomMode;

    public string MissionModeName => "AUTO";

    public string HoldModeName => "Hold";

    public MavlinkHoldPolicy HoldPolicy => new(
        RequiresStableTelemetry: true,
        StabilityWindow: TimeSpan.FromSeconds(1),
        PositionToleranceMetres: 3,
        AltitudeToleranceMetres: 0.75,
        MaximumHorizontalSpeedMetresPerSecond: 0.75,
        MaximumVerticalSpeedMetresPerSecond: 0.5,
        StrategyName: "ArduCopter Brake with stable position and altitude telemetry");

    public string DecodeMode(uint customMode)
        => customMode switch
        {
            0 => "Stabilize",
            1 => "Acro",
            2 => "AltHold",
            3 => "Auto",
            4 => "Guided",
            5 => "Loiter",
            6 => "RTL",
            7 => "Circle",
            9 => "Land",
            11 => "Drift",
            13 => "Sport",
            15 => "AutoTune",
            16 => "PosHold",
            17 => "Brake",
            18 => "Throw",
            20 => "Guided_NoGPS",
            21 => "SmartRTL",
            22 => "FlowHold",
            23 => "Follow",
            24 => "ZigZag",
            _ => $"ArduPilot mode {customMode}"
        };

    public MavlinkCommandEnvelope BuildCommand(
        OperatorCommandRequest request,
        VehicleTelemetryRecord telemetry)
    {
        var parameters = request.Parameters ?? OperatorCommandParameters.None;
        return request.Command switch
        {
            OperatorCommandKind.Arm => Command(MavlinkCommandIds.ComponentArmDisarm, "Arm", 1f, 0f, 0f, 0f, 0f, 0f, 0f),
            OperatorCommandKind.Disarm => Command(MavlinkCommandIds.ComponentArmDisarm, "Disarm", 0f, 0f, 0f, 0f, 0f, 0f, 0f),
            OperatorCommandKind.Hold => new MavlinkCommandEnvelope(
                MavlinkCommandIds.DoSetMode,
                [],
                "ArduPilot Hold",
                MavlinkWireKind.SetMode,
                BaseMode: MavlinkValues.MavModeFlagCustomModeEnabled,
                CustomMode: MavlinkValues.ArduPilotBrakeCustomMode),
            OperatorCommandKind.Takeoff => BuildTakeoff(parameters, telemetry),
            OperatorCommandKind.Land => Command(MavlinkCommandIds.NavLand, "Land", 0f, 0f, 0f, float.NaN,
                Required(telemetry.LatitudeDegrees, "Land requires latitude telemetry."),
                Required(telemetry.LongitudeDegrees, "Land requires longitude telemetry."), 0f),
            OperatorCommandKind.Recover => Command(MavlinkCommandIds.NavReturnToLaunch, "Return to launch", 0f, 0f, 0f, 0f, 0f, 0f, 0f),
            OperatorCommandKind.GoTo => BuildReposition(parameters, telemetry, "Go To"),
            OperatorCommandKind.ChangeAltitude => BuildAltitude(parameters, telemetry),
            OperatorCommandKind.SetHeading => BuildHeading(parameters, telemetry),
            _ => throw new NotSupportedException($"ArduPilot MAVLink does not support {request.Command}.")
        };
    }

    private static MavlinkCommandEnvelope BuildTakeoff(OperatorCommandParameters parameters, VehicleTelemetryRecord telemetry)
    {
        var targetAgl = Required(parameters.TakeoffAltitudeAglMetres, "Takeoff requires a target AGL altitude.");
        return Command(MavlinkCommandIds.NavTakeoff, "Takeoff", 0f, 0f, 0f, float.NaN,
            Required(telemetry.LatitudeDegrees, "Takeoff requires latitude telemetry."),
            Required(telemetry.LongitudeDegrees, "Takeoff requires longitude telemetry."),
            // ArduCopter interprets NAV_TAKEOFF param7 as altitude above home.
            // Do not convert the requested AGL target through the current MSL
            // telemetry; that would command an unintended large climb.
            (float)targetAgl);
    }

    private static MavlinkCommandEnvelope BuildReposition(OperatorCommandParameters parameters, VehicleTelemetryRecord telemetry, string description)
    {
        if (parameters.GoToTargetKind != OperatorGoToTargetKind.GlobalWgs84)
            throw new NotSupportedException("ArduPilot MAVLink supports global WGS84 targets only.");
        var yaw = parameters.GoToYawDegrees is { } value ? NormalizeHeading(value) * Math.PI / 180 : double.NaN;
        return GuidedReposition(parameters.GoToLatitudeDegrees, parameters.GoToLongitudeDegrees,
            parameters.GoToAltitudeAmslMetres, double.IsFinite(yaw) ? (float)yaw : float.NaN, description);
    }

    private static MavlinkCommandEnvelope BuildAltitude(OperatorCommandParameters parameters, VehicleTelemetryRecord telemetry)
    {
        var currentMsl = Required(telemetry.AltitudeMslMetres, "Change altitude requires MSL altitude telemetry.");
        var currentAgl = Required(telemetry.AltitudeAglMetres, "Change altitude requires AGL altitude telemetry.");
        var target = parameters.AltitudeTargetKind switch
        {
            OperatorAltitudeTargetKind.AltitudeAmsl => Required(parameters.AltitudeAmslMetres, "An AMSL altitude is required."),
            OperatorAltitudeTargetKind.AltitudeAgl => currentMsl - currentAgl + Required(parameters.AltitudeAglMetres, "An AGL altitude is required."),
            OperatorAltitudeTargetKind.RelativeDelta => currentMsl + Required(parameters.AltitudeRelativeDeltaMetres, "A relative altitude delta is required."),
            _ => throw new InvalidOperationException("An altitude target kind is required.")
        };
        return GuidedPosition(telemetry.LatitudeDegrees, telemetry.LongitudeDegrees, target, float.NaN, "Change altitude");
    }

    private static MavlinkCommandEnvelope BuildHeading(OperatorCommandParameters parameters, VehicleTelemetryRecord telemetry)
    {
        var current = Required(telemetry.HeadingDegrees, "Set heading requires heading telemetry.");
        var target = parameters.HeadingTargetKind switch
        {
            OperatorHeadingTargetKind.AbsoluteHeading => Required(parameters.HeadingDegrees, "An absolute heading is required."),
            OperatorHeadingTargetKind.RelativeYaw => NormalizeHeading(current + Required(parameters.RelativeYawDegrees, "A relative yaw is required.")),
            _ => throw new InvalidOperationException("A heading target kind is required.")
        };
        // ArduCopter does not interpret the yaw field of DO_REPOSITION as a
        // reliable heading command. QGroundControl uses CONDITION_YAW so the
        // vehicle performs the rotation in its native Guided controller while
        // preserving the requested absolute/relative frontend semantics.
        var delta = ((target - current + 540) % 360) - 180;
        return Command(
            MavlinkCommandIds.ConditionYaw,
            "Set heading",
            Math.Abs((float)delta),
            30f,
            delta >= 0 ? 1f : -1f,
            1f,
            0f,
            0f,
            0f);
    }

    private static MavlinkCommandEnvelope GuidedPosition(double? latitude, double? longitude, double? altitude, float yaw, string description)
    {
        var lat = Required(latitude, $"{description} requires latitude telemetry.");
        var lon = Required(longitude, $"{description} requires longitude telemetry.");
        var alt = Required(altitude, $"{description} requires MSL altitude.");
        return new MavlinkCommandEnvelope(
            MavlinkCommandIds.DoReposition,
            [-1f, 1f, 0f, yaw], description,
            MavlinkWireKind.CommandInt,
            5,
            checked((int)Math.Round(lat * 10_000_000)),
            checked((int)Math.Round(lon * 10_000_000)),
            (float)alt);
    }

    private static MavlinkCommandEnvelope GuidedReposition(double? latitude, double? longitude, double? altitude, float yaw, string description)
    {
        var lat = Required(latitude, $"{description} requires a latitude target.");
        var lon = Required(longitude, $"{description} requires a longitude target.");
        var alt = Required(altitude, $"{description} requires an AMSL altitude target.");
        // DO_REPOSITION follows QGroundControl's ArduCopter path: GLOBAL
        // frame, change-mode flag, and an AMSL altitude. Using the relative
        // frame while supplying MSL is silently rejected or produces a bad
        // target on some ArduPilot versions.
        return new MavlinkCommandEnvelope(
            MavlinkCommandIds.DoReposition,
            [-1f, 1f, 0f, yaw], description,
            MavlinkWireKind.CommandInt,
            0,
            checked((int)Math.Round(lat * 10_000_000)),
            checked((int)Math.Round(lon * 10_000_000)),
            (float)alt);
    }

    private static MavlinkCommandEnvelope Command(ushort command, string description, params float[] parameters)
        => new(command, parameters, description);

    private static float Required(double? value, string message)
        => value is { } actual && double.IsFinite(actual) ? (float)actual : throw new InvalidOperationException(message);

    private static float NormalizeHeading(double heading)
        => (float)((heading % 360 + 360) % 360);

    private static bool IsMulticopter(byte mavType)
        => mavType is 2 or 3 or 4 or 13 or 14 or 15 or 29;
}
