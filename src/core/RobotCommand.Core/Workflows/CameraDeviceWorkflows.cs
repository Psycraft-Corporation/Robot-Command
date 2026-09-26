namespace RobotCommand.Core;

/// <summary>One advertised video format supported by a camera device.</summary>
public sealed record CameraVideoFormatSnapshot(uint Width, uint Height, uint FramesPerSecond)
{
    public string Label => $"{Width}×{Height} @ {FramesPerSecond} fps";
}

/// <summary>Current simulated gimbal pose and motion configuration.</summary>
public sealed record CameraGimbalSnapshot(
    double PitchDegrees,
    double YawDegrees,
    double RollDegrees,
    double TargetPitchDegrees,
    double TargetYawDegrees,
    double TargetRollDegrees,
    double MinimumPitchDegrees,
    double MaximumPitchDegrees,
    double MaximumYawDegrees,
    double MaximumRollDegrees,
    double SlewRateDegreesPerSecond);

/// <summary>UI-neutral camera capabilities and current state.</summary>
public sealed record CameraDeviceStateSnapshot(
    FlightMissionCameraMode Mode,
    uint VideoWidth,
    uint VideoHeight,
    uint VideoFramesPerSecond,
    IReadOnlyList<CameraVideoFormatSnapshot> SupportedVideoFormats,
    double ZoomMagnification,
    double MinimumZoomMagnification,
    double MaximumZoomMagnification,
    bool IsRecording,
    ulong PhotoCount,
    bool SupportsPhoto,
    bool SupportsVideo,
    bool SupportsGimbal,
    CameraGimbalSnapshot Gimbal);

/// <summary>Fixed first-version camera and gimbal model used by every Ghost.</summary>
public static class GhostCameraDefaults
{
    public const uint DefaultWidth = 960;
    public const uint DefaultHeight = 540;
    public const uint FramesPerSecond = 30;
    public const double MinimumZoomMagnification = 1;
    public const double MaximumZoomMagnification = 10;
    public const double MinimumPitchDegrees = -90;
    public const double MaximumPitchDegrees = 30;
    public const double MaximumYawDegrees = 180;
    public const double MaximumRollDegrees = 45;
    public const double SlewRateDegreesPerSecond = 60;
    public const double SlewAccelerationDegreesPerSecondSquared = 240;

    public static IReadOnlyList<CameraVideoFormatSnapshot> SupportedVideoFormats { get; } =
    [
        new(640, 360, FramesPerSecond),
        new(DefaultWidth, DefaultHeight, FramesPerSecond),
        new(1280, 720, FramesPerSecond)
    ];
}
