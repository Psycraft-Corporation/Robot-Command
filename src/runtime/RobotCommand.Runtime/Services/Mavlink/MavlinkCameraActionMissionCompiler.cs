using RobotCommand.Core;
using RobotCommand.Models;

namespace RobotCommand.Services.Mavlink;

/// <summary>
/// Converts the portable camera-action contract to standard MAVLink mission
/// commands. This is deliberately separate from the navigation compiler so
/// backend capability checks and command parameter conventions stay together.
/// </summary>
internal static class MavlinkCameraActionMissionCompiler
{
    private const byte MissionCommandFrame = 2;
    private const byte GlobalRelativeAltInt = 6;
    private const int GimbalManagerRollLock = 4;
    private const int GimbalManagerPitchLock = 8;
    private const int GimbalManagerYawInVehicleFrame = GimbalManagerRollLock | GimbalManagerPitchLock | 32;
    private const int GimbalManagerYawInEarthFrame = GimbalManagerRollLock | GimbalManagerPitchLock | 64;
    private const float MountModeMavlinkTargeting = 2;

    public static void EnsureCanCompile(
        MavlinkAutopilotProfile profile,
        FlightMissionDocument mission,
        UnitObservationSnapshot? target)
    {
        var actions = MavlinkCameraCapabilityMatrix.ActionsFor(mission);
        if (actions.Count == 0) return;
        if (target is null)
            throw new InvalidOperationException("Camera mission actions require a connected target with a reported firmware version.");

        var findings = MavlinkCameraCapabilityMatrix.ValidateMissionActions(profile, mission, target)
            .Where(finding => finding.Severity == WorkflowFindingSeverity.Blocking)
            .ToArray();
        if (findings.Length > 0)
            throw new InvalidOperationException(string.Join(" ", findings.Select(finding => finding.Message)));

        foreach (var action in actions)
            if (!action.IsValid)
                throw new InvalidOperationException(string.Join(" ", action.ValidationErrors));
    }

    public static void AppendMissionActions(
        List<MavlinkMissionItem> items,
        IEnumerable<FlightMissionCameraAction> actions,
        MavlinkAutopilotProfile profile,
        ref int captureSequence)
    {
        foreach (var action in actions)
        {
            var item = Compile(action, profile, checked((ushort)items.Count), ref captureSequence);
            items.Add(item);
        }
    }

    /// <summary>
    /// Compiles one portable camera action for immediate MAVLink dispatch. The
    /// parameter ordering is shared with mission compilation so standalone
    /// controls and uploaded missions cannot drift apart.
    /// </summary>
    public static MavlinkCommandEnvelope CompileStandalone(
        FlightMissionCameraAction action,
        MavlinkAutopilotProfile profile)
    {
        if (!action.IsValid)
            throw new InvalidOperationException(string.Join(" ", action.ValidationErrors));

        var cameraId = action.CameraId ?? 0;
        return action.Kind switch
        {
            FlightMissionCameraActionKind.PhotoOnce => Command(
                MavlinkCommandIds.ImageStartCapture,
                "Capture photo",
                cameraId, 0, 1, 0, 0, 0, 0),
            FlightMissionCameraActionKind.PhotoByTime => Command(
                MavlinkCommandIds.ImageStartCapture,
                "Start timed photos",
                cameraId, ToFloat(action.IntervalSeconds), 0, 0, 0, 0, 0),
            FlightMissionCameraActionKind.PhotoByDistance => Command(
                MavlinkCommandIds.DoSetCameraTriggerDistance,
                "Start distance photos",
                ToFloat(action.DistanceMetres), 0, 0, cameraId, 0, 0, 0),
            FlightMissionCameraActionKind.StopPhotos => Command(
                MavlinkCommandIds.ImageStopCapture,
                "Stop photos",
                cameraId, 0, 0, 0, 0, 0, 0),
            FlightMissionCameraActionKind.StartVideo => Command(
                MavlinkCommandIds.VideoStartCapture,
                "Start video",
                0, 0, cameraId, 0, 0, 0, 0),
            FlightMissionCameraActionKind.StopVideo => Command(
                MavlinkCommandIds.VideoStopCapture,
                "Stop video",
                0, cameraId, 0, 0, 0, 0, 0),
            FlightMissionCameraActionKind.CameraMode => Command(
                MavlinkCommandIds.SetCameraMode,
                "Set camera mode",
                cameraId, action.CameraMode == FlightMissionCameraMode.Video ? 1 : 0, 0, 0, 0, 0, 0),
            FlightMissionCameraActionKind.CameraZoom => Command(
                MavlinkCommandIds.SetCameraZoom,
                "Set camera zoom",
                2, ToFloat(action.GimbalZoomPercent), 0, 0, 0, 0, 0),
            FlightMissionCameraActionKind.RegionOfInterest => new(
                MavlinkCommandIds.DoSetRoiLocation,
                [cameraId, 0, 0, 0, 0, 0, 0],
                "Set camera region of interest",
                MavlinkWireKind.CommandInt,
                GlobalRelativeAltInt,
                ToE7(action.RegionOfInterest!.LatitudeDegrees),
                ToE7(action.RegionOfInterest.LongitudeDegrees),
                0),
            FlightMissionCameraActionKind.Gimbal when profile == MavlinkAutopilotProfile.ArduPilot && action.GimbalRollDegrees is not null
                => Command(
                    MavlinkCommandIds.DoMountControl,
                    "Set gimbal attitude",
                    ToFloat(action.GimbalPitchDegrees),
                    ToFloat(action.GimbalRollDegrees),
                    ToFloat(action.GimbalYawDegrees),
                    MountModeMavlinkTargeting, 0, 0, 0),
            FlightMissionCameraActionKind.Gimbal => Command(
                MavlinkCommandIds.DoGimbalManagerPitchYaw,
                "Set gimbal attitude",
                ToFloat(action.GimbalPitchDegrees),
                ToFloat(action.GimbalYawDegrees),
                float.NaN,
                float.NaN,
                action.GimbalFrame == FlightMissionGimbalFrame.Earth
                    ? GimbalManagerYawInEarthFrame
                    : GimbalManagerYawInVehicleFrame,
                0,
                cameraId),
            _ => throw new InvalidOperationException($"Camera action {action.Kind} is not supported.")
        };
    }

    private static MavlinkCommandEnvelope Command(
        ushort commandId,
        string description,
        float param1 = 0,
        float param2 = 0,
        float param3 = 0,
        float param4 = 0,
        float param5 = 0,
        float param6 = 0,
        float param7 = 0)
        => new(commandId, [param1, param2, param3, param4, param5, param6, param7], description);

    /// <summary>
    /// Reconstructs the portable camera action represented by a downloaded
    /// MAVLink mission item. Keeping this beside the encoder prevents PX4 and
    /// ArduPilot imports from drifting apart as command parameters evolve.
    /// </summary>
    public static bool TryDecompile(MavlinkMissionItem item, out FlightMissionCameraAction? action)
    {
        action = item.Command switch
        {
            MavlinkCommandIds.ImageStartCapture when item.Param3 > 0
                => FlightMissionCameraAction.PhotoOnce(
                    cameraId: CameraId(item.Param1),
                    cameraName: null),
            MavlinkCommandIds.ImageStartCapture when item.Param2 > 0 && float.IsFinite(item.Param2)
                => FlightMissionCameraAction.PhotoByTime(item.Param2, cameraId: CameraId(item.Param1)),
            MavlinkCommandIds.ImageStopCapture
                => FlightMissionCameraAction.StopPhotos(cameraId: CameraId(item.Param1)),
            MavlinkCommandIds.DoSetCameraTriggerDistance when item.Param1 > 0 && float.IsFinite(item.Param1)
                => FlightMissionCameraAction.PhotoByDistance(item.Param1, cameraId: CameraId(item.Param4)),
            MavlinkCommandIds.VideoStartCapture
                => FlightMissionCameraAction.StartVideo(cameraId: CameraId(item.Param3)),
            MavlinkCommandIds.VideoStopCapture
                => FlightMissionCameraAction.StopVideo(cameraId: CameraId(item.Param2)),
            MavlinkCommandIds.SetCameraMode when item.Param2 is 0 or 1
                => FlightMissionCameraAction.SetCameraMode(
                    item.Param2 == 1 ? FlightMissionCameraMode.Video : FlightMissionCameraMode.Photo,
                    cameraId: CameraId(item.Param1)),
            MavlinkCommandIds.SetCameraZoom when item.Param1 == 2 && float.IsFinite(item.Param2)
                => FlightMissionCameraAction.SetZoom(item.Param2),
            MavlinkCommandIds.DoSetRoiLocation
                => FlightMissionCameraAction.SetRegionOfInterest(
                    new FlightMissionCoordinate(item.LatitudeE7 / 10_000_000d, item.LongitudeE7 / 10_000_000d),
                    cameraId: CameraId(item.Param1)),
            MavlinkCommandIds.DoSetRoi when item.Param1 is 3 or 4
                => FlightMissionCameraAction.SetRegionOfInterest(
                    new FlightMissionCoordinate(item.LatitudeE7 / 10_000_000d, item.LongitudeE7 / 10_000_000d)),
            MavlinkCommandIds.DoGimbalManagerPitchYaw
                => FlightMissionCameraAction.SetGimbal(
                    NullableFinite(item.Param1), NullableFinite(item.Param2),
                    frame: item.RawX == GimbalManagerYawInEarthFrame
                        ? FlightMissionGimbalFrame.Earth
                        : FlightMissionGimbalFrame.Vehicle,
                    cameraId: CameraId(item.RawZ ?? item.AltitudeMetres)),
            MavlinkCommandIds.DoMountControl
                => FlightMissionCameraAction.SetGimbal(
                    NullableFinite(item.Param1), NullableFinite(item.Param3), NullableFinite(item.Param2),
                    cameraId: null),
            _ => null
        };
        return action is not null;
    }

    private static byte? CameraId(float value)
        => float.IsFinite(value) && value >= 0 && value <= byte.MaxValue ? (byte)Math.Round(value) : null;

    private static byte? CameraId(int value)
        => value is >= 0 and <= byte.MaxValue ? (byte)value : null;

    private static double? NullableFinite(float value)
        => float.IsFinite(value) ? value : null;

    private static MavlinkMissionItem Compile(
        FlightMissionCameraAction action,
        MavlinkAutopilotProfile profile,
        ushort sequence,
        ref int captureSequence)
    {
        var cameraId = action.CameraId ?? 0;
        var command = action.Kind switch
        {
            FlightMissionCameraActionKind.PhotoOnce => new MavlinkMissionItem(
                sequence, MavlinkCommandIds.ImageStartCapture, MissionCommandFrame, 0, 0, 0,
                Param1: cameraId, Param3: 1, Param4: captureSequence++),
            FlightMissionCameraActionKind.PhotoByTime => new MavlinkMissionItem(
                sequence, MavlinkCommandIds.ImageStartCapture, MissionCommandFrame, 0, 0, 0,
                Param1: cameraId, Param2: ToWireFloat(action.IntervalSeconds!.Value), Param3: 0, Param4: 0),
            FlightMissionCameraActionKind.PhotoByDistance => new MavlinkMissionItem(
                sequence, MavlinkCommandIds.DoSetCameraTriggerDistance, MissionCommandFrame, 0, 0, 0,
                Param1: ToWireFloat(action.DistanceMetres!.Value), Param4: cameraId),
            FlightMissionCameraActionKind.StopPhotos => new MavlinkMissionItem(
                sequence, MavlinkCommandIds.ImageStopCapture, MissionCommandFrame, 0, 0, 0,
                Param1: cameraId),
            FlightMissionCameraActionKind.StartVideo => new MavlinkMissionItem(
                sequence, MavlinkCommandIds.VideoStartCapture, MissionCommandFrame, 0, 0, 0,
                Param3: cameraId),
            FlightMissionCameraActionKind.StopVideo => new MavlinkMissionItem(
                sequence, MavlinkCommandIds.VideoStopCapture, MissionCommandFrame, 0, 0, 0,
                Param2: cameraId),
            FlightMissionCameraActionKind.CameraMode => new MavlinkMissionItem(
                sequence, MavlinkCommandIds.SetCameraMode, MissionCommandFrame, 0, 0, 0,
                Param1: cameraId, Param2: action.CameraMode == FlightMissionCameraMode.Video ? 1 : 0),
            FlightMissionCameraActionKind.CameraZoom => new MavlinkMissionItem(
                sequence, MavlinkCommandIds.SetCameraZoom, MissionCommandFrame, 0, 0, 0,
                Param1: 2, Param2: ToWireFloat(action.GimbalZoomPercent!.Value)),
            FlightMissionCameraActionKind.RegionOfInterest => CompileRegionOfInterest(action, sequence, cameraId),
            FlightMissionCameraActionKind.Gimbal => CompileGimbal(action, profile, sequence, cameraId),
            _ => throw new InvalidOperationException($"Camera action {action.Kind} is not supported by the mission compiler.")
        };
        return command;
    }

    private static MavlinkMissionItem CompileRegionOfInterest(
        FlightMissionCameraAction action,
        ushort sequence,
        byte cameraId)
    {
        var roi = action.RegionOfInterest!;
        return new(
            sequence,
            MavlinkCommandIds.DoSetRoiLocation,
            GlobalRelativeAltInt,
            ToE7(roi.LatitudeDegrees),
            ToE7(roi.LongitudeDegrees),
            0,
            Param1: cameraId);
    }

    private static MavlinkMissionItem CompileGimbal(
        FlightMissionCameraAction action,
        MavlinkAutopilotProfile profile,
        ushort sequence,
        byte cameraId)
    {
        // MAV_CMD_DO_GIMBAL_MANAGER_PITCHYAW has no roll parameter. ArduPilot
        // retains MAV_CMD_DO_MOUNT_CONTROL for missions that explicitly ask
        // for roll, so use that legacy command only for this lossless case.
        if (profile == MavlinkAutopilotProfile.ArduPilot && action.GimbalRollDegrees is not null)
            return new(
                sequence,
                MavlinkCommandIds.DoMountControl,
                MissionCommandFrame,
                0,
                0,
                MountModeMavlinkTargeting,
                Param1: ToFloat(action.GimbalPitchDegrees),
                Param2: ToFloat(action.GimbalRollDegrees),
                Param3: ToFloat(action.GimbalYawDegrees),
                Param4: 0);

        var yawFrame = action.GimbalFrame == FlightMissionGimbalFrame.Earth
            ? GimbalManagerYawInEarthFrame
            : GimbalManagerYawInVehicleFrame;
        return new(
            sequence,
            MavlinkCommandIds.DoGimbalManagerPitchYaw,
            MissionCommandFrame,
            0,
            0,
            0,
            Param1: ToFloat(action.GimbalPitchDegrees),
            Param2: ToFloat(action.GimbalYawDegrees),
            Param3: float.NaN,
            Param4: float.NaN,
            RawX: yawFrame,
            RawY: 0,
            RawZ: cameraId);
    }

    private static float ToFloat(double? value)
        => value is { } angle ? ToWireFloat(angle) : float.NaN;

    private static float ToWireFloat(double value)
    {
        var converted = (float)value;
        return float.IsFinite(converted)
            ? converted
            : throw new InvalidOperationException("Camera action parameter exceeds the MAVLink float range.");
    }

    private static int ToE7(double value)
        => checked((int)Math.Round(value * 10_000_000d));
}
