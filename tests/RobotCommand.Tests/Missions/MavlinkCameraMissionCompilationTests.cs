using System.Xml.Linq;
using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Missions;

public sealed class MavlinkCameraMissionCompilationTests
{
    [Fact]
    public void Px4CompilesSupportedCameraActionsInMissionOrder()
    {
        var actions = new[]
        {
            FlightMissionCameraAction.PhotoOnce("front", 2),
            FlightMissionCameraAction.PhotoByTime(3, "front", 2),
            FlightMissionCameraAction.StopPhotos("front", 2),
            FlightMissionCameraAction.StartVideo("front", 2),
            FlightMissionCameraAction.StopVideo("front", 2),
            FlightMissionCameraAction.SetCameraMode(FlightMissionCameraMode.Video, "front", 2)
        };
        var mission = MissionWith(new FlightMissionCameraIntent(Actions: actions));

        var items = new Px4FlightMissionCompiler().Compile(mission, Target("PX4", "1.13.0"));
        var cameraItems = items.Where(item => item.Command != MavlinkCommandIds.NavTakeoff &&
                                              item.Command != MavlinkCommandIds.DoChangeSpeed &&
                                              item.Command != 16 &&
                                              item.Command != MavlinkCommandIds.NavReturnToLaunch).ToArray();

        Assert.Equal(
            new ushort[]
            {
                MavlinkCommandIds.ImageStartCapture,
                MavlinkCommandIds.ImageStartCapture,
                MavlinkCommandIds.ImageStopCapture,
                MavlinkCommandIds.VideoStartCapture,
                MavlinkCommandIds.VideoStopCapture,
                MavlinkCommandIds.SetCameraMode
            },
            cameraItems.Select(item => item.Command));
        Assert.Equal(2, cameraItems[0].Param1);
        Assert.Equal(1, cameraItems[0].Param3);
        Assert.Equal(0, cameraItems[0].Param4);
        Assert.Equal(3, cameraItems[1].Param2);
        Assert.Equal(2, cameraItems[3].Param3);
        Assert.Equal(2, cameraItems[4].Param2);
        Assert.Equal(1, cameraItems[5].Param2);
        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, items.Select(item => item.Sequence));
    }

    [Fact]
    public void ArduPilotCompilesDistanceRoiAndGimbalActionsWithCommandParameters()
    {
        var actions = new[]
        {
            FlightMissionCameraAction.PhotoByDistance(12, cameraId: 3),
            FlightMissionCameraAction.SetRegionOfInterest(new FlightMissionCoordinate(43.701, -79.401), cameraId: 3),
            FlightMissionCameraAction.SetGimbal(-45, 20, frame: FlightMissionGimbalFrame.Earth, cameraId: 4),
            FlightMissionCameraAction.SetGimbal(-30, 10, 5, cameraId: 4)
        };
        var mission = MissionWith(new FlightMissionCameraIntent(Actions: actions));

        var items = new ArduPilotFlightMissionCompiler().Compile(mission, Target("ArduPilot", "4.3.0"));
        var cameraItems = items.Where(item => item.Command is MavlinkCommandIds.DoSetCameraTriggerDistance or
            MavlinkCommandIds.DoSetRoiLocation or MavlinkCommandIds.DoGimbalManagerPitchYaw or MavlinkCommandIds.DoMountControl).ToArray();

        Assert.Equal(new ushort[]
        {
            MavlinkCommandIds.DoSetCameraTriggerDistance,
            MavlinkCommandIds.DoSetRoiLocation,
            MavlinkCommandIds.DoGimbalManagerPitchYaw,
            MavlinkCommandIds.DoMountControl
        }, cameraItems.Select(item => item.Command));
        Assert.Equal(12, cameraItems[0].Param1);
        Assert.Equal(3, cameraItems[0].Param4);
        Assert.Equal(437010000, cameraItems[1].LatitudeE7);
        Assert.Equal(-794010000, cameraItems[1].LongitudeE7);
        Assert.Equal(4, cameraItems[2].RawZ);
        Assert.Equal(4 | 8 | 64, cameraItems[2].RawX);
        Assert.Equal(-30, cameraItems[3].Param1);
        Assert.Equal(5, cameraItems[3].Param2);
        Assert.Equal(10, cameraItems[3].Param3);
        Assert.Equal(2, cameraItems[3].AltitudeMetres);
    }

    [Fact]
    public void CompileRejectsCameraActionsWithoutAProvenFirmwareCapability()
    {
        var mission = MissionWith(new FlightMissionCameraIntent(
            Actions: [FlightMissionCameraAction.PhotoByDistance(10)]));

        var px4 = Assert.Throws<InvalidOperationException>(() =>
            new Px4FlightMissionCompiler().Compile(mission, Target("PX4", "1.15.0")));
        var unknown = Assert.Throws<InvalidOperationException>(() =>
            new ArduPilotFlightMissionCompiler().Compile(mission, Target("ArduPilot", "Not reported")));

        Assert.Contains("PhotoByDistance", px4.Message);
        Assert.Contains("cannot be approved", unknown.Message);
    }

    [Fact]
    public void ArduPilotGeneratesSurveyAndCorridorTriggerCommands()
    {
        var survey = new FlightMissionStep(
            "survey",
            FlightMissionStepKind.SurveyZone,
            Coordinates:
            [
                new FlightMissionCoordinate(43.7000, -79.4010),
                new FlightMissionCoordinate(43.7010, -79.4010),
                new FlightMissionCoordinate(43.7010, -79.4000),
                new FlightMissionCoordinate(43.7000, -79.4000)
            ],
            Survey: new FlightMissionSurveyOptions(
                LineSpacingMetres: 25,
                CameraIntent: new FlightMissionCameraIntent("Photo", TriggerDistanceMetres: 20, AutomaticPhotoCaptureEnabled: true)));
        var mission = MissionWith(null) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                survey,
                new FlightMissionStep(
                    "corridor",
                    FlightMissionStepKind.CorridorScan,
                    Coordinates:
                    [
                        new FlightMissionCoordinate(43.7020, -79.4010),
                        new FlightMissionCoordinate(43.7030, -79.4010)
                    ],
                    Corridor: new FlightMissionCorridorOptions(
                        CorridorWidthMetres: 20,
                        LineSpacingMetres: 20,
                        CameraIntent: new FlightMissionCameraIntent("Photo", TriggerIntervalSeconds: 3, AutomaticPhotoCaptureEnabled: true)))
            ]
        };

        var items = new ArduPilotFlightMissionCompiler().Compile(mission, Target("ArduPilot", "4.3.0"));

        Assert.Equal(
            new ushort[]
            {
                MavlinkCommandIds.DoSetCameraTriggerDistance,
                MavlinkCommandIds.ImageStopCapture,
                MavlinkCommandIds.ImageStartCapture,
                MavlinkCommandIds.ImageStopCapture
            },
            items.Where(item => item.Command is MavlinkCommandIds.DoSetCameraTriggerDistance or MavlinkCommandIds.ImageStartCapture or MavlinkCommandIds.ImageStopCapture)
                .Select(item => item.Command));
        Assert.True(items.ToList().FindIndex(item => item.Command == MavlinkCommandIds.DoSetCameraTriggerDistance) <
                    items.ToList().FindIndex(item => item.Command == 16));
    }

    [Fact]
    public void PreviewReportsCaptureMarkersAndExpectedCounts()
    {
        var mission = MissionWith(null) with
        {
            CameraIntent = new FlightMissionCameraIntent(Actions: [FlightMissionCameraAction.StartVideo()]),
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("route", FlightMissionStepKind.WaypointSequence,
                    Coordinates: [new FlightMissionCoordinate(43.7000, -79.4000), new FlightMissionCoordinate(43.7010, -79.4000)],
                    CameraIntent: new FlightMissionCameraIntent(Actions: [FlightMissionCameraAction.PhotoByDistance(50)]))
            ]
        };

        var preview = new ArduPilotFlightMissionCompiler().Preview(mission);

        Assert.NotNull(preview.CaptureStatistics);
        Assert.True(preview.CaptureStatistics!.ExpectedPhotoCount > 0);
        Assert.True(preview.CaptureStatistics.ExpectedVideoDurationSeconds > 0);
        Assert.NotEmpty(preview.CaptureStatistics.Markers);
    }

    [Fact]
    public void DecompilePreservesImportedCameraActionsAtMissionAndStepScope()
    {
        var mission = MissionWith(null) with
        {
            CameraIntent = new FlightMissionCameraIntent(
                Actions: [FlightMissionCameraAction.StartVideo(cameraId: 2)]),
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("route", FlightMissionStepKind.WaypointSequence,
                    Coordinates: [new FlightMissionCoordinate(43.7000, -79.4000), new FlightMissionCoordinate(43.7010, -79.4000)],
                    CameraIntent: new FlightMissionCameraIntent(
                        Actions:
                        [
                            FlightMissionCameraAction.PhotoByDistance(25, cameraId: 2),
                            FlightMissionCameraAction.SetRegionOfInterest(new FlightMissionCoordinate(43.702, -79.4), cameraId: 2),
                            FlightMissionCameraAction.SetGimbal(-45, 10, frame: FlightMissionGimbalFrame.Earth, cameraId: 2)
                        ])),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ]
        };

        var items = new ArduPilotFlightMissionCompiler().Compile(mission, Target("ArduPilot", "4.3.0"));
        var imported = new ArduPilotFlightMissionCompiler().Decompile("Imported", items, DateTimeOffset.UtcNow);

        Assert.Equal(FlightMissionCameraActionKind.StartVideo, Assert.Single(imported.CameraIntent!.Actions!).Kind);
        var importedRoute = imported.Steps.Last(step => step.Kind == FlightMissionStepKind.PointOfInterest);
        Assert.Equal(
            new[]
            {
                FlightMissionCameraActionKind.PhotoByDistance,
                FlightMissionCameraActionKind.RegionOfInterest,
                FlightMissionCameraActionKind.Gimbal
            },
            importedRoute.CameraIntent!.Actions!.Select(action => action.Kind));
        Assert.Equal(25, importedRoute.CameraIntent.Actions[0].DistanceMetres);
        Assert.Equal((byte)2, importedRoute.CameraIntent.Actions[0].CameraId);
        Assert.Equal(43.702, importedRoute.CameraIntent.Actions[1].RegionOfInterest!.LatitudeDegrees, 6);
        Assert.Equal(FlightMissionGimbalFrame.Earth, importedRoute.CameraIntent.Actions[2].GimbalFrame);
    }

    [Theory]
    [InlineData("PX4")]
    [InlineData("ArduPilot")]
    public void DecompilePreservesBasicCameraActionsForBothBackends(string backend)
    {
        var mission = MissionWith(null) with
        {
            CameraIntent = new FlightMissionCameraIntent(
                Actions: [FlightMissionCameraAction.PhotoOnce(cameraId: 2), FlightMissionCameraAction.StartVideo(cameraId: 2)])
        };
        IFlightMissionCompiler compiler = backend == "PX4"
            ? new Px4FlightMissionCompiler()
            : new ArduPilotFlightMissionCompiler();

        var imported = compiler.Decompile("Imported", compiler.Compile(mission, Target(backend, backend == "PX4" ? "1.15.0" : "4.3.0")), DateTimeOffset.UtcNow);

        Assert.Equal(
            new[] { FlightMissionCameraActionKind.PhotoOnce, FlightMissionCameraActionKind.StartVideo },
            imported.CameraIntent!.Actions!.Select(action => action.Kind));
    }

    [Fact]
    public void CameraDefinitionParserPreservesQgcParametersAndOptions()
    {
        var document = XDocument.Parse("""
            <camera name="SurveyCam">
              <parameter name="mode" humanName="Capture mode" type="enum" default="photo" units="">
                <values><value code="photo" name="Photo" /><value code="video" name="Video" /></values>
              </parameter>
              <parameter name="interval" label="Interval" type="float" min="0.5" max="60" increment="0.5" units="s" />
            </camera>
            """);

        var settings = MavlinkCameraDefinitionLoader.Parse(document);

        Assert.Equal(2, settings.Count);
        Assert.Equal("Capture mode", settings[0].Label);
        Assert.Equal(["photo", "video"], settings[0].Values.Select(item => item.Value));
        Assert.Equal(0.5, settings[1].Minimum);
        Assert.Equal("s", settings[1].Units);
    }

    private static FlightMissionDocument MissionWith(FlightMissionCameraIntent? cameraIntent)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            FlightMissionDocument.CurrentSchemaVersion,
            "camera-compile",
            "Camera compile",
            25,
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("camera", FlightMissionStepKind.CameraCaptureIntent, CameraIntent: cameraIntent),
                new FlightMissionStep("poi", FlightMissionStepKind.PointOfInterest,
                    Coordinates: [new FlightMissionCoordinate(43.7, -79.4)]),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ],
            now,
            now);
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
