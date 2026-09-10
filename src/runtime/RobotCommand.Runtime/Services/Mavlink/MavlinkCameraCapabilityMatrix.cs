using RobotCommand.Core;
using RobotCommand.Models;

namespace RobotCommand.Services.Mavlink;

public enum MavlinkCameraCapabilityState
{
    Supported,
    Unsupported,
    Unknown
}

/// <summary>
/// Firmware-independent description of one backend camera capability. The
/// minimum versions are conservative verified baselines for mission emission;
/// they are deliberately kept here rather than in the portable mission model.
/// </summary>
public sealed record MavlinkCameraCapability(
    FlightMissionCameraActionKind Action,
    bool Supported,
    Version? MinimumFirmwareVersion,
    string Requirement,
    bool RequiresCamera = true,
    bool RequiresGimbal = false);

public sealed record MavlinkCameraCapabilityResult(
    FlightMissionCameraActionKind Action,
    MavlinkCameraCapabilityState State,
    string Message,
    string? FirmwareVersion,
    Version? ParsedFirmwareVersion,
    MavlinkCameraCapability Capability);

/// <summary>
/// Capability matrices for the mission camera actions supported by the
/// current PX4 and ArduPilot adapters. An unknown firmware version never
/// becomes an implicit approval.
/// </summary>
public static class MavlinkCameraCapabilityMatrix
{
    private static readonly Version Px4CameraProtocolBaseline = new(1, 13, 0);
    private static readonly Version ArduPilotCameraProtocolBaseline = new(4, 3, 0);

    public static IReadOnlyList<MavlinkCameraCapability> Px4 { get; } =
    [
        Supported(FlightMissionCameraActionKind.PhotoOnce, Px4CameraProtocolBaseline, "PX4 mission image capture"),
        Supported(FlightMissionCameraActionKind.PhotoByTime, Px4CameraProtocolBaseline, "PX4 mission image capture interval"),
        Unsupported(FlightMissionCameraActionKind.PhotoByDistance, "PX4's documented mission camera subset does not include distance triggering."),
        Supported(FlightMissionCameraActionKind.StopPhotos, Px4CameraProtocolBaseline, "PX4 mission image capture stop"),
        Supported(FlightMissionCameraActionKind.StartVideo, Px4CameraProtocolBaseline, "PX4 mission video capture start"),
        Supported(FlightMissionCameraActionKind.StopVideo, Px4CameraProtocolBaseline, "PX4 mission video capture stop"),
        Supported(FlightMissionCameraActionKind.CameraMode, Px4CameraProtocolBaseline, "PX4 mission camera mode"),
        Supported(FlightMissionCameraActionKind.CameraZoom, Px4CameraProtocolBaseline, "PX4 camera zoom"),
        Unsupported(FlightMissionCameraActionKind.RegionOfInterest, "ROI is not in PX4's documented mission camera subset."),
        Unsupported(FlightMissionCameraActionKind.Gimbal, "Gimbal positioning is not in PX4's documented mission camera subset.", requiresCamera: false, requiresGimbal: true)
    ];

    public static IReadOnlyList<MavlinkCameraCapability> ArduPilot { get; } =
    [
        Supported(FlightMissionCameraActionKind.PhotoOnce, ArduPilotCameraProtocolBaseline, "ArduPilot mission image capture"),
        Supported(FlightMissionCameraActionKind.PhotoByTime, ArduPilotCameraProtocolBaseline, "ArduPilot mission image capture interval"),
        Supported(FlightMissionCameraActionKind.PhotoByDistance, ArduPilotCameraProtocolBaseline, "ArduPilot camera distance trigger"),
        Supported(FlightMissionCameraActionKind.StopPhotos, ArduPilotCameraProtocolBaseline, "ArduPilot mission image capture stop"),
        Supported(FlightMissionCameraActionKind.StartVideo, ArduPilotCameraProtocolBaseline, "ArduPilot mission video capture start"),
        Supported(FlightMissionCameraActionKind.StopVideo, ArduPilotCameraProtocolBaseline, "ArduPilot mission video capture stop"),
        Supported(FlightMissionCameraActionKind.CameraMode, ArduPilotCameraProtocolBaseline, "ArduPilot mission camera mode"),
        Supported(FlightMissionCameraActionKind.CameraZoom, ArduPilotCameraProtocolBaseline, "ArduPilot camera zoom"),
        Supported(FlightMissionCameraActionKind.RegionOfInterest, ArduPilotCameraProtocolBaseline, "ArduPilot mission ROI"),
        Supported(FlightMissionCameraActionKind.Gimbal, ArduPilotCameraProtocolBaseline, "ArduPilot mission gimbal positioning", requiresCamera: false, requiresGimbal: true)
    ];

    public static IReadOnlyList<MavlinkCameraCapability> For(MavlinkAutopilotProfile profile)
        => profile == MavlinkAutopilotProfile.ArduPilot ? ArduPilot : Px4;

    public static MavlinkCameraCapabilityResult Evaluate(
        MavlinkAutopilotProfile profile,
        FlightMissionCameraActionKind action,
        string? firmwareVersion)
    {
        var capability = For(profile).FirstOrDefault(item => item.Action == action)
            ?? Unsupported(action, "No capability matrix entry exists for this camera action.");
        if (!capability.Supported)
            return new(action, MavlinkCameraCapabilityState.Unsupported, capability.Requirement, firmwareVersion, Parse(firmwareVersion), capability);

        if (!TryParse(firmwareVersion, out var parsed))
        {
            var minimum = capability.MinimumFirmwareVersion?.ToString(3) ?? "a supported firmware version";
            return new(action, MavlinkCameraCapabilityState.Unknown,
                $"{profile} firmware version was not reported; this action requires {minimum} or newer.", firmwareVersion, null, capability);
        }

        if (capability.MinimumFirmwareVersion is { } required && parsed < required)
            return new(action, MavlinkCameraCapabilityState.Unsupported,
                $"{profile} firmware {parsed} is older than the conservative minimum {required} for this action.", firmwareVersion, parsed, capability);

        return new(action, MavlinkCameraCapabilityState.Supported,
            $"{profile} firmware {parsed} meets the camera-action requirement.", firmwareVersion, parsed, capability);
    }

    public static IReadOnlyList<WorkflowFinding> ValidateMissionActions(
        MavlinkAutopilotProfile profile,
        FlightMissionDocument mission,
        UnitObservationSnapshot? target)
    {
        if (target is null) return [];

        var firmwareVersion = target.Diagnostics?.FirmwareVersion;
        var findings = new List<WorkflowFinding>();
        foreach (var action in ActionsFor(mission))
        {
            var result = Evaluate(profile, action.Kind, firmwareVersion);
            if (result.State == MavlinkCameraCapabilityState.Unsupported)
                findings.Add(new(
                    "MISSION_CAMERA_CAPABILITY_UNSUPPORTED",
                    WorkflowFindingSeverity.Blocking,
                    $"{result.Action} is not supported by {profile}: {result.Message}"));
            else if (result.State == MavlinkCameraCapabilityState.Unknown)
                findings.Add(new(
                    "MISSION_CAMERA_CAPABILITY_UNKNOWN",
                    WorkflowFindingSeverity.Blocking,
                    $"{result.Action} cannot be approved for {profile}: {result.Message}"));
        }

        return findings
            .DistinctBy(item => (item.Code, item.Message))
            .ToArray();
    }

    public static IReadOnlyList<FlightMissionCameraAction> ActionsFor(FlightMissionDocument mission)
        => (mission.CameraIntent?.Actions ?? [])
            .Concat(mission.Steps.SelectMany(step => (step.CameraIntent?.Actions ?? [])
                .Concat(step.Survey?.CameraIntent?.Actions ?? [])
                .Concat(step.Corridor?.CameraIntent?.Actions ?? [])
                .Concat(FlightMissionPreviewCaptureBuilder.RouteTriggerActions(
                    step.Kind,
                    step.CameraIntent ?? step.Survey?.CameraIntent ?? step.Corridor?.CameraIntent))))
            .ToArray();

    private static MavlinkCameraCapability Supported(
        FlightMissionCameraActionKind action,
        Version minimumFirmwareVersion,
        string requirement,
        bool requiresCamera = true,
        bool requiresGimbal = false)
        => new(action, true, minimumFirmwareVersion, requirement, requiresCamera, requiresGimbal);

    private static MavlinkCameraCapability Unsupported(
        FlightMissionCameraActionKind action,
        string requirement,
        bool requiresCamera = true,
        bool requiresGimbal = false)
        => new(action, false, null, requirement, requiresCamera, requiresGimbal);

    private static Version? Parse(string? value)
        => TryParse(value, out var parsed) ? parsed : null;

    private static bool TryParse(string? value, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('v')) text = text[1..];
        var numericParts = text.Split(['.', '-', '+', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Take(3)
            .ToArray();
        if (numericParts.Length < 2 || !numericParts.All(part => int.TryParse(part, out _)) ||
            !Version.TryParse(string.Join('.', numericParts), out var parsed) || parsed is null)
            return false;

        version = parsed;
        return true;
    }
}
