using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Missions;

public sealed class MavlinkCameraCapabilityMatrixTests
{
    [Fact]
    public void Px4AndArduPilotMatricesCoverEveryCameraAction()
    {
        var actions = Enum.GetValues<FlightMissionCameraActionKind>();

        Assert.Equal(actions.Order(), MavlinkCameraCapabilityMatrix.Px4.Select(item => item.Action).Order());
        Assert.Equal(actions.Order(), MavlinkCameraCapabilityMatrix.ArduPilot.Select(item => item.Action).Order());
    }

    [Fact]
    public void Px4MatrixDistinguishesSupportedUnsupportedAndVersionUnknown()
    {
        var timePhoto = MavlinkCameraCapabilityMatrix.Evaluate(
            MavlinkAutopilotProfile.Px4,
            FlightMissionCameraActionKind.PhotoByTime,
            "1.13.0");
        var oldFirmware = MavlinkCameraCapabilityMatrix.Evaluate(
            MavlinkAutopilotProfile.Px4,
            FlightMissionCameraActionKind.PhotoByTime,
            "1.12.0");
        var unknownFirmware = MavlinkCameraCapabilityMatrix.Evaluate(
            MavlinkAutopilotProfile.Px4,
            FlightMissionCameraActionKind.PhotoByTime,
            "Not reported");
        var distancePhoto = MavlinkCameraCapabilityMatrix.Evaluate(
            MavlinkAutopilotProfile.Px4,
            FlightMissionCameraActionKind.PhotoByDistance,
            "1.15.0");

        Assert.Equal(MavlinkCameraCapabilityState.Supported, timePhoto.State);
        Assert.Equal(MavlinkCameraCapabilityState.Unsupported, oldFirmware.State);
        Assert.Equal(MavlinkCameraCapabilityState.Unknown, unknownFirmware.State);
        Assert.Equal(MavlinkCameraCapabilityState.Unsupported, distancePhoto.State);
    }

    [Fact]
    public void ArduPilotMatrixRequiresTheConservativeFirmwareBaseline()
    {
        var current = MavlinkCameraCapabilityMatrix.Evaluate(
            MavlinkAutopilotProfile.ArduPilot,
            FlightMissionCameraActionKind.Gimbal,
            "4.3.0");
        var oldFirmware = MavlinkCameraCapabilityMatrix.Evaluate(
            MavlinkAutopilotProfile.ArduPilot,
            FlightMissionCameraActionKind.StartVideo,
            "4.2.9");

        Assert.Equal(MavlinkCameraCapabilityState.Supported, current.State);
        Assert.True(current.Capability.RequiresGimbal);
        Assert.Equal(MavlinkCameraCapabilityState.Unsupported, oldFirmware.State);
    }

    [Fact]
    public void CompilerReportsUnsupportedAndUnverifiableCameraActionsForAConnectedTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var mission = new FlightMissionDocument(
            FlightMissionDocument.CurrentSchemaVersion,
            "camera-matrix",
            "Camera matrix",
            25,
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("camera", FlightMissionStepKind.CameraCaptureIntent,
                    CameraIntent: new FlightMissionCameraIntent(Actions: [FlightMissionCameraAction.PhotoByDistance(10)])),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ],
            now,
            now);

        var unsupported = new Px4FlightMissionCompiler().Validate(mission, Target("PX4", "1.15.0"));
        var unverifiable = new ArduPilotFlightMissionCompiler().Validate(mission, Target("ArduPilot", "Not reported"));

        Assert.Contains(unsupported, finding => finding.Code == "MISSION_CAMERA_CAPABILITY_UNSUPPORTED" && finding.Severity == WorkflowFindingSeverity.Blocking);
        Assert.Contains(unverifiable, finding => finding.Code == "MISSION_CAMERA_CAPABILITY_UNKNOWN" && finding.Severity == WorkflowFindingSeverity.Blocking);
    }

    private static UnitObservationSnapshot Target(string backend, string firmwareVersion)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = backend.Equals("ArduPilot", StringComparison.OrdinalIgnoreCase) ? "ardupilot" : "px4";
        return new UnitObservationSnapshot(
            $"{profile}-1", backend, ["connection-1"], null, null, "Multicopter", "Air", profile,
            ManagedConnectionState.Online, "Ready", "Online", "Disarmed", "Unknown", [], now, false,
            $"{profile}-1", $"{profile}-1", $"{profile}-1",
            new UnitTelemetryObservation(ManagedConnectionState.Online, false, "Landed", "Hold", 43.7, -79.4, 488, 0, 0, 0, 0, 90, false, now),
            new UnitDiagnosticsObservation("Ready", "Ready", "Ready", "", "Ready", "", "Ready", "", [], [], now, firmwareVersion),
            [], new UnitActionObservation(null, null));
    }
}
