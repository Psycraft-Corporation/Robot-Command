namespace RobotCommand.Models;

public enum ManualControlSessionState
{
    Inactive,
    Acquiring,
    Active,
    Hold,
    InputStale,
    DeviceLost,
    Releasing,
    Blocked
}

public enum ManualControlBackend
{
    None,
    Ghost,
    Px4Mavlink,
    ArduPilotMavlink
}

public enum ManualInputDeviceKind
{
    XboxGamepad,
    Joystick
}

/// <summary>
/// Maps a generic HID joystick to Robot Command's assisted Mode 2 controls.
/// Axis indexes are zero-based and button indexes are zero-based.  The default
/// matches the common Windows raw-input layout exposed by a T.16000M:
/// X/Y/twist/slider on axes 0/1/2/3 and trigger on button 0.
/// </summary>
public sealed record ManualJoystickMapping(
    int ForwardAxis = 1,
    bool ForwardInverted = true,
    int RightAxis = 0,
    bool RightInverted = false,
    int VerticalAxis = 3,
    bool VerticalInverted = false,
    int YawAxis = 2,
    bool YawInverted = false,
    int DeadmanButton = 0,
    int ArmButton = 1,
    int TakeoffButton = 2,
    int ExecuteButton = 3,
    int CancelButton = 4,
    int ReleaseButton = 5)
{
    public ManualJoystickMapping Normalize() => this with
    {
        ForwardAxis = Math.Clamp(ForwardAxis, 0, 31),
        RightAxis = Math.Clamp(RightAxis, 0, 31),
        VerticalAxis = Math.Clamp(VerticalAxis, 0, 31),
        YawAxis = Math.Clamp(YawAxis, 0, 31),
        DeadmanButton = Math.Clamp(DeadmanButton, 0, 127),
        ArmButton = Math.Clamp(ArmButton, 0, 127),
        TakeoffButton = Math.Clamp(TakeoffButton, 0, 127),
        ExecuteButton = Math.Clamp(ExecuteButton, 0, 127),
        CancelButton = Math.Clamp(CancelButton, 0, 127),
        ReleaseButton = Math.Clamp(ReleaseButton, 0, 127)
    };
}

public sealed record ManualInputDevice(
    string Id,
    string Name,
    bool IsConnected,
    ManualInputDeviceKind Kind = ManualInputDeviceKind.XboxGamepad,
    int AxisCount = 0,
    int ButtonCount = 0);

public sealed record ManualInputReading(
    string DeviceId,
    double LeftX,
    double LeftY,
    double RightX,
    double RightY,
    double LeftTrigger,
    double RightTrigger,
    bool LeftBumper,
    bool RightBumper,
    bool A,
    bool B,
    bool X,
    bool Y,
    bool Menu,
    DateTimeOffset Timestamp,
    IReadOnlyList<double>? RawAxes = null,
    IReadOnlyList<bool>? RawButtons = null);

public sealed record ManualControlProfile(
    double DeadZone = 0.12,
    double Expo = 0.35,
    double MaximumHorizontalSpeedMetresPerSecond = 8,
    double MaximumVerticalSpeedMetresPerSecond = 3,
    double MaximumYawRateDegreesPerSecond = 90,
    double HorizontalAccelerationMetresPerSecondSquared = 4,
    double VerticalAccelerationMetresPerSecondSquared = 3,
    double YawAccelerationDegreesPerSecondSquared = 180,
    double TakeoffAltitudeAglMetres = 5,
    IReadOnlyDictionary<string, ManualJoystickMapping>? JoystickMappings = null)
{
    public ManualControlProfile Normalize() => this with
    {
        DeadZone = Math.Clamp(DeadZone, 0, 0.5),
        Expo = Math.Clamp(Expo, 0, 1),
        MaximumHorizontalSpeedMetresPerSecond = Math.Clamp(MaximumHorizontalSpeedMetresPerSecond, 0.1, 30),
        MaximumVerticalSpeedMetresPerSecond = Math.Clamp(MaximumVerticalSpeedMetresPerSecond, 0.1, 15),
        MaximumYawRateDegreesPerSecond = Math.Clamp(MaximumYawRateDegreesPerSecond, 1, 360),
        HorizontalAccelerationMetresPerSecondSquared = Math.Clamp(HorizontalAccelerationMetresPerSecondSquared, 0.1, 30),
        VerticalAccelerationMetresPerSecondSquared = Math.Clamp(VerticalAccelerationMetresPerSecondSquared, 0.1, 20),
        YawAccelerationDegreesPerSecondSquared = Math.Clamp(YawAccelerationDegreesPerSecondSquared, 1, 720),
        TakeoffAltitudeAglMetres = Math.Clamp(TakeoffAltitudeAglMetres, 0.5, 120),
        JoystickMappings = (JoystickMappings ?? new Dictionary<string, ManualJoystickMapping>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(item => item.Key, item => (item.Value ?? new ManualJoystickMapping()).Normalize(), StringComparer.Ordinal)
    };

    public ManualJoystickMapping MappingFor(string? deviceId)
        => deviceId is not null && JoystickMappings is not null && JoystickMappings.TryGetValue(deviceId, out var mapping)
            ? mapping.Normalize()
            : new ManualJoystickMapping();

    public ManualControlProfile WithJoystickMapping(string deviceId, ManualJoystickMapping mapping)
    {
        var mappings = (JoystickMappings ?? new Dictionary<string, ManualJoystickMapping>())
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        mappings[deviceId] = mapping.Normalize();
        return (this with { JoystickMappings = mappings }).Normalize();
    }
}

public sealed record ManualControlProfileRecord(
    string Id,
    string Name,
    ManualControlProfile Settings);

public sealed record ManualControlProfileLibrary(
    string SchemaVersion,
    string ActiveProfileId,
    IReadOnlyList<ManualControlProfileRecord> Profiles)
{
    public const string CurrentSchemaVersion = "robotcommand.manual-control-profiles.v1";

    public static ManualControlProfileLibrary Default { get; } = new(
        CurrentSchemaVersion,
        "default",
        [new ManualControlProfileRecord("default", "Default", new ManualControlProfile())]);
}

public sealed record ManualControlSetpoint(
    double BodyForwardMetresPerSecond,
    double BodyRightMetresPerSecond,
    double VerticalMetresPerSecond,
    double YawRateDegreesPerSecond,
    bool DeadmanPressed,
    DateTimeOffset Timestamp)
{
    public static ManualControlSetpoint Neutral(DateTimeOffset now) => new(0, 0, 0, 0, false, now);
}

public sealed record ManualControlSessionSnapshot(
    ManualControlSessionState State,
    ManualControlBackend Backend,
    string? TargetVehicleId,
    string? TargetName,
    string? DeviceId,
    string? DeviceName,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastInputAt,
    bool DeadmanPressed,
    bool InputNeutral,
    string Status,
    string? PendingButtonAction,
    DateTimeOffset? PendingButtonExpiresAt,
    double InputRateHertz,
    string? AutopilotMode = null,
    string? ModeClass = null,
    string? AdmissionStatus = null,
    double? TransportInputRateHertz = null,
    DateTimeOffset? LastInputSentAt = null,
    bool? InputEchoAvailable = null,
    string? SafeReleaseMode = null,
    bool? SafeReleaseConfirmed = null,
    string? InterruptionReason = null)
{
    public static ManualControlSessionSnapshot Empty { get; } = new(
        ManualControlSessionState.Inactive, ManualControlBackend.None, null, null, null, null, null, null,
        false, true, "No manual-control session.", null, null, 0);

    public bool IsActive => State is ManualControlSessionState.Active or ManualControlSessionState.Hold or ManualControlSessionState.InputStale;
}
