using RobotCommand.Core;
using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests.Missions;

public sealed class FlightMissionCameraActionTests
{
    [Fact]
    public void FactoriesRepresentEveryPortableCameraAction()
    {
        var roi = new FlightMissionCoordinate(43.7, -79.4);
        var actions = new[]
        {
            FlightMissionCameraAction.PhotoOnce(),
            FlightMissionCameraAction.PhotoByTime(2.5),
            FlightMissionCameraAction.PhotoByDistance(15),
            FlightMissionCameraAction.StopPhotos(),
            FlightMissionCameraAction.StartVideo(),
            FlightMissionCameraAction.StopVideo(),
            FlightMissionCameraAction.SetCameraMode(FlightMissionCameraMode.Video),
            FlightMissionCameraAction.SetRegionOfInterest(roi),
            FlightMissionCameraAction.SetGimbal(-30, 90, 0, FlightMissionGimbalFrame.Earth),
            FlightMissionCameraAction.SetZoom(75)
        };

        Assert.Equal(
            [
                FlightMissionCameraActionKind.PhotoOnce,
                FlightMissionCameraActionKind.PhotoByTime,
                FlightMissionCameraActionKind.PhotoByDistance,
                FlightMissionCameraActionKind.StopPhotos,
                FlightMissionCameraActionKind.StartVideo,
                FlightMissionCameraActionKind.StopVideo,
                FlightMissionCameraActionKind.CameraMode,
                FlightMissionCameraActionKind.RegionOfInterest,
                FlightMissionCameraActionKind.Gimbal,
                FlightMissionCameraActionKind.CameraZoom
            ],
            actions.Select(action => action.Kind));
        Assert.All(actions, action => Assert.True(action.IsValid));
        Assert.Equal(roi, actions[7].RegionOfInterest);
        Assert.Equal(FlightMissionGimbalFrame.Earth, actions[8].GimbalFrame);
        Assert.Equal(75, actions[9].GimbalZoomPercent);
    }

    [Fact]
    public void ActionValidationRequiresActionSpecificValues()
    {
        Assert.Contains(FlightMissionCameraAction.PhotoByTime(0).ValidationErrors, error => error.Contains("interval", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(FlightMissionCameraAction.PhotoByDistance(double.NaN).ValidationErrors, error => error.Contains("distance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(new FlightMissionCameraAction(FlightMissionCameraActionKind.CameraMode).ValidationErrors, error => error.Contains("mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(new FlightMissionCameraAction(FlightMissionCameraActionKind.RegionOfInterest).ValidationErrors, error => error.Contains("coordinate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(FlightMissionCameraAction.SetGimbal().ValidationErrors, error => error.Contains("angle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(FlightMissionCameraAction.SetGimbal(-91).ValidationErrors, error => error.Contains("pitch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(FlightMissionCameraAction.SetZoom(101).ValidationErrors, error => error.Contains("zoom", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AutomaticPhotoCaptureIsOptInAndDoesNotSuppressExplicitActions()
    {
        var disabled = new FlightMissionCameraIntent(
            TriggerDistanceMetres: 10,
            Actions: [FlightMissionCameraAction.StartVideo()],
            AutomaticPhotoCaptureEnabled: false);
        var enabled = disabled with { AutomaticPhotoCaptureEnabled = true };

        Assert.Empty(FlightMissionPreviewCaptureBuilder.RouteTriggerStartActions(FlightMissionStepKind.CorridorScan, disabled));

        var generated = Assert.Single(FlightMissionPreviewCaptureBuilder.RouteTriggerStartActions(
            FlightMissionStepKind.CorridorScan, enabled));
        Assert.Equal(FlightMissionCameraActionKind.PhotoByDistance, generated.Kind);
        Assert.Equal(10, generated.DistanceMetres);
    }

    [Fact]
    public async Task CameraActionsRoundTripAndInvalidActionsAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robot-command-camera-actions-{Guid.NewGuid():N}");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var mission = new FlightMissionDocument(
                FlightMissionDocument.CurrentSchemaVersion,
                "camera-actions",
                "Camera actions",
                25,
                [new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff)],
                now,
                now,
                CameraIntent: new FlightMissionCameraIntent(
                    Mode: "Portable",
                    Actions:
                    [
                        FlightMissionCameraAction.PhotoOnce("front-camera"),
                        FlightMissionCameraAction.PhotoByDistance(10, "front-camera"),
                        FlightMissionCameraAction.SetRegionOfInterest(new FlightMissionCoordinate(43.7, -79.4), "front-camera")
                    ]));
            using var store = new FlightMissionLibraryStore(root);

            await store.SaveAsync(mission, cancellationToken: TestContext.Current.CancellationToken);
            using var reloadedStore = new FlightMissionLibraryStore(root);
            var actions = Assert.Single(reloadedStore.Missions).CameraIntent!.Actions!;

            Assert.Equal(3, actions.Count);
            Assert.Equal(FlightMissionCameraActionKind.PhotoByDistance, actions[1].Kind);
            Assert.Equal(10, actions[1].DistanceMetres);
            Assert.Equal("front-camera", actions[2].CameraName);

            var invalid = mission with
            {
                MissionId = "invalid-camera-actions",
                CameraIntent = new FlightMissionCameraIntent(Actions: [FlightMissionCameraAction.PhotoByTime(0)])
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(invalid, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MissionStartAndStepActionsRemainIndependentAcrossPersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robot-command-camera-scopes-{Guid.NewGuid():N}");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var mission = new FlightMissionDocument(
                FlightMissionDocument.CurrentSchemaVersion,
                "camera-scopes",
                "Camera scopes",
                25,
                [
                    new FlightMissionStep(
                        "takeoff",
                        FlightMissionStepKind.Takeoff,
                        CameraIntent: new FlightMissionCameraIntent(Actions: [FlightMissionCameraAction.StartVideo("step-camera")])),
                    new FlightMissionStep(
                        "loiter",
                        FlightMissionStepKind.TimedLoiter,
                        Coordinates: [new FlightMissionCoordinate(43.7, -79.4)],
                        LoiterDurationSeconds: 30,
                        CameraIntent: new FlightMissionCameraIntent(Actions: [FlightMissionCameraAction.PhotoByTime(2, "loiter-camera")]))
                ],
                now,
                now,
                CameraIntent: new FlightMissionCameraIntent(Actions: [FlightMissionCameraAction.SetCameraMode(FlightMissionCameraMode.Photo, "mission-camera")]));
            using var store = new FlightMissionLibraryStore(root);

            await store.SaveAsync(mission, cancellationToken: TestContext.Current.CancellationToken);
            using var reloadedStore = new FlightMissionLibraryStore(root);
            var loaded = Assert.Single(reloadedStore.Missions);

            var missionAction = Assert.Single(loaded.CameraIntent!.Actions!);
            Assert.Equal(FlightMissionCameraActionKind.CameraMode, missionAction.Kind);
            Assert.Equal("mission-camera", missionAction.CameraName);

            var takeoffAction = Assert.Single(loaded.Steps[0].CameraIntent!.Actions!);
            Assert.Equal(FlightMissionCameraActionKind.StartVideo, takeoffAction.Kind);
            Assert.Equal("step-camera", takeoffAction.CameraName);

            var loiter = loaded.Steps[1];
            var loiterAction = Assert.Single(loiter.CameraIntent!.Actions!);
            Assert.Equal(FlightMissionCameraActionKind.PhotoByTime, loiterAction.Kind);
            Assert.Equal(30, loiter.LoiterDurationSeconds);
            Assert.Equal("loiter-camera", loiterAction.CameraName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
