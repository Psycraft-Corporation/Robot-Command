using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Missions;

public sealed class FlightMissionLibraryTests
{
    [Fact]
    public async Task Store_RoundTripsMissionAndPreservesFrozenGeometrySnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robot-command-flight-mission-{Guid.NewGuid():N}");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var mission = ValidMission(now);
            var store = new FlightMissionLibraryStore(root);

            var saved = await store.SaveAsync(mission, cancellationToken: TestContext.Current.CancellationToken);
            var reloaded = new FlightMissionLibraryStore(root);

            var loaded = Assert.Single(reloaded.Missions);
            Assert.Equal(FlightMissionDocument.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(saved.ContentSha256, loaded.ContentSha256);
            Assert.Equal("geometry-poi", Assert.Single(loaded.Steps.Where(step => step.Kind == FlightMissionStepKind.PointOfInterest)).SourceGeometryId);
            Assert.Equal(43.7001, Assert.Single(loaded.Steps.Where(step => step.Kind == FlightMissionStepKind.PointOfInterest)).FrozenCoordinates.Single().LatitudeDegrees, 4);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Compiler_UsesRelativeHomeMissionItemsAndPreservesStepOrder()
    {
        var compiler = new Px4FlightMissionCompiler();
        var items = compiler.Compile(ValidMission(DateTimeOffset.UtcNow), TargetAt(43.7, -79.4));

        Assert.Collection(items,
            takeoff =>
            {
                Assert.Equal(MavlinkCommandIds.NavTakeoff, takeoff.Command);
                Assert.Equal((byte)6, takeoff.Frame);
                Assert.Equal(25, takeoff.AltitudeMetres);
            },
            speed =>
            {
                Assert.Equal(MavlinkCommandIds.DoChangeSpeed, speed.Command);
                Assert.Equal(5, speed.Param2);
            },
            waypoint =>
            {
                Assert.Equal((ushort)16, waypoint.Command);
                Assert.Equal(437001000, waypoint.LatitudeE7);
                Assert.Equal(-794001000, waypoint.LongitudeE7);
                Assert.Equal(25, waypoint.AltitudeMetres);
            },
            rtl =>
            {
                Assert.Equal(MavlinkCommandIds.NavReturnToLaunch, rtl.Command);
                Assert.Equal((byte)2, rtl.Frame);
            });

        Assert.Equal(new ushort[] { 0, 1, 2, 3 }, items.Select(item => item.Sequence));
    }

    [Fact]
    public void ArduPilotCompiler_UsesSharedSupportedMissionItemsAndAllowsSharedFenceAssociation()
    {
        var now = DateTimeOffset.UtcNow;
        var mission = ValidMission(now) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("poi", FlightMissionStepKind.PointOfInterest,
                    Coordinates: [new FlightMissionCoordinate(43.7001, -79.4001)]),
                new FlightMissionStep("loiter", FlightMissionStepKind.TimedLoiter,
                    Coordinates: [new FlightMissionCoordinate(43.7002, -79.4002)], LoiterDurationSeconds: 10),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch),
                new FlightMissionStep("land", FlightMissionStepKind.Land)
            ],
            TargetAssignment = new FlightMissionTargetAssignment("ardupilot-1", null, "PX4-fence")
        };
        var target = TargetAt(43.7, -79.4) with { ProfileKey = "ardupilot", Name = "ArduPilot" };
        var compiler = new ArduPilotFlightMissionCompiler();

        Assert.True(compiler.SupportsTarget(target));
        var findings = compiler.Validate(mission, target);
        Assert.DoesNotContain(findings, item => item.Code == "MISSION_FENCE_BACKEND_UNSUPPORTED");
        Assert.DoesNotContain(compiler.Compile(mission, target), item => item.Command == MavlinkCommandIds.MissionStart);

        var wire = compiler.Compile(mission with { TargetAssignment = null }, target);
        Assert.Contains(wire, item => item.Command == MavlinkCommandIds.NavTakeoff && item.Frame == 6);
        Assert.Contains(wire, item => item.Command == MavlinkCommandIds.NavLoiterTime && item.Param1 == 10);
        Assert.Equal([MavlinkCommandIds.NavReturnToLaunch, MavlinkCommandIds.NavLand], wire.TakeLast(2).Select(item => item.Command));
        Assert.All(wire, item => Assert.False(float.IsNaN(item.Param4)));
    }

    [Fact]
    public void ArduPilotCompiler_DecompileRejectsUnsupportedDownloadedCommand()
    {
        var compiler = new ArduPilotFlightMissionCompiler();
        var exception = Assert.Throws<NotSupportedException>(() => compiler.Decompile(
            "Downloaded",
            [new MavlinkMissionItem(4, 177, 2, 0, 0, 0)],
            DateTimeOffset.UtcNow));

        Assert.Contains("177", exception.Message);
        Assert.Contains("sequence 4", exception.Message);
    }

    [Fact]
    public void Compiler_UsesTheTargetLocationForTakeoff()
    {
        var compiler = new Px4FlightMissionCompiler();
        var target = TargetAt(47.3977422, 8.5455941);

        var takeoff = Assert.Single(compiler.Compile(ValidMission(DateTimeOffset.UtcNow), target).Take(1));

        Assert.Equal(473977422, takeoff.LatitudeE7);
        Assert.Equal(85455941, takeoff.LongitudeE7);
        Assert.True(float.IsNaN(takeoff.Param4));
    }

    [Fact]
    public void Compiler_PreviewWithoutTarget_DoesNotAttemptWireCompilation()
    {
        var compiler = new Px4FlightMissionCompiler();

        var preview = compiler.Preview(ValidMission(DateTimeOffset.UtcNow), target: null);

        Assert.Equal(0, preview.MissionItemCount);
        Assert.NotNull(preview.Route);
    }

    private static UnitObservationSnapshot TargetAt(double latitude, double longitude)
        => new(
            "px4-1", "PX4", ["connection-1"], null, null, "Multicopter", "Air", "px4",
            ManagedConnectionState.Online, "Ready", "Online", "Disarmed", "Unknown", [], DateTimeOffset.UtcNow,
            false, "px4-1", "px4-1", "px4-1",
            new UnitTelemetryObservation(ManagedConnectionState.Online, false, "Landed", "Hold", latitude, longitude, 488, 0, 0, 0, 0, 90, false, DateTimeOffset.UtcNow),
            null, [], new UnitActionObservation(null, null));

    [Fact]
    public void Validate_AllowsNavigationMissionWithConfiguredEndBehavior()
    {
        var compiler = new Px4FlightMissionCompiler();
        var now = DateTimeOffset.UtcNow;
        var incomplete = ValidMission(now) with { Steps = ValidMission(now).Steps.Take(2).ToArray() };

        FlightMissionLibraryStore.Validate(incomplete);
        var findings = compiler.Validate(incomplete, null);

        Assert.DoesNotContain(findings, finding => finding.Code == "MISSION_TERMINAL_STEP_REQUIRED");
    }

    [Fact]
    public void Compiler_AppendsRtlForConfiguredMissionEndAction()
    {
        var mission = ValidMission(DateTimeOffset.UtcNow) with
        {
            EndAction = FlightMissionEndAction.ReturnToLaunch,
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("poi", FlightMissionStepKind.PointOfInterest, Coordinates: [new FlightMissionCoordinate(43.7001, -79.4001)])
            ]
        };

        var compiler = new Px4FlightMissionCompiler();
        var findings = compiler.Validate(mission, TargetAt(43.7, -79.4));
        var items = compiler.Compile(mission, TargetAt(43.7, -79.4));

        Assert.DoesNotContain(findings, finding => finding.Severity == WorkflowFindingSeverity.Blocking);
        Assert.Equal(MavlinkCommandIds.NavReturnToLaunch, items[^1].Command);
    }

    [Fact]
    public void Validate_AcceptsTakeoffInsertedBeforeExistingRouteSteps()
    {
        var now = DateTimeOffset.UtcNow;
        var mission = ValidMission(now) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("route", FlightMissionStepKind.WaypointSequence, "geometry-route", "Route", "hash",
                    [new FlightMissionCoordinate(43.7, -79.4), new FlightMissionCoordinate(43.71, -79.41)]),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ]
        };

        FlightMissionLibraryStore.Validate(mission);
    }

    [Fact]
    public void Validate_AcceptsRtlImmediatelyFollowedByLand()
    {
        var now = DateTimeOffset.UtcNow;
        var mission = ValidMission(now) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("poi", FlightMissionStepKind.PointOfInterest, Coordinates: [new FlightMissionCoordinate(43.7, -79.4)]),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch),
                new FlightMissionStep("land", FlightMissionStepKind.Land)
            ]
        };

        FlightMissionLibraryStore.Validate(mission);
        var compiled = new Px4FlightMissionCompiler().Compile(mission, TargetAt(43.7, -79.4));

        Assert.Equal([MavlinkCommandIds.NavReturnToLaunch, MavlinkCommandIds.NavLand], compiled.TakeLast(2).Select(item => item.Command));
        var land = compiled[^1];
        Assert.Equal((byte)6, land.Frame);
        Assert.Equal(437000000, land.LatitudeE7);
        Assert.Equal(-794000000, land.LongitudeE7);
        Assert.Equal(0, land.AltitudeMetres);
    }

    [Fact]
    public void Compiler_LandsAtTheLastNavigationPointWithoutRtl()
    {
        var mission = ValidMission(DateTimeOffset.UtcNow) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("poi", FlightMissionStepKind.PointOfInterest, Coordinates: [new FlightMissionCoordinate(43.7001, -79.4001)]),
                new FlightMissionStep("land", FlightMissionStepKind.Land)
            ]
        };

        var land = new Px4FlightMissionCompiler().Compile(mission, TargetAt(43.7, -79.4))[^1];

        Assert.Equal(MavlinkCommandIds.NavLand, land.Command);
        Assert.Equal((byte)6, land.Frame);
        Assert.Equal(437001000, land.LatitudeE7);
        Assert.Equal(-794001000, land.LongitudeE7);
        Assert.Equal(0, land.AltitudeMetres);
    }

    [Fact]
    public async Task Store_MigratesV1MissionToV2WithSafeDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robot-command-flight-mission-{Guid.NewGuid():N}");
        try
        {
            var store = new FlightMissionLibraryStore(root);
            var legacy = ValidMission(DateTimeOffset.UtcNow) with { SchemaVersion = FlightMissionDocument.LegacySchemaVersion, CruiseSpeedMetresPerSecond = 0 };

            var saved = await store.SaveAsync(legacy, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(FlightMissionDocument.CurrentSchemaVersion, saved.SchemaVersion);
            Assert.Equal(5, saved.CruiseSpeedMetresPerSecond);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Compiler_ExpandsSurveyInsertsSpeedAndKeepsCameraMetadataOffWire()
    {
        var now = DateTimeOffset.UtcNow;
        var survey = new FlightMissionStep("survey", FlightMissionStepKind.SurveyZone, "zone", "Zone", "hash",
        [
            new FlightMissionCoordinate(43.7000, -79.4010),
            new FlightMissionCoordinate(43.7010, -79.4010),
            new FlightMissionCoordinate(43.7010, -79.4000),
            new FlightMissionCoordinate(43.7000, -79.4000)
        ], CruiseSpeedMetresPerSecond: 7, Survey: new FlightMissionSurveyOptions(LineSpacingMetres: 25));
        var mission = ValidMission(now) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                survey,
                new FlightMissionStep("camera", FlightMissionStepKind.CameraCaptureIntent, CameraIntent: new FlightMissionCameraIntent("Photo")),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ]
        };

        var items = new Px4FlightMissionCompiler().Compile(mission, TargetAt(43.7, -79.4));

        Assert.Contains(items, item => item.Command == MavlinkCommandIds.DoChangeSpeed && item.Param2 == 7);
        Assert.True(items.Count(item => item.Command == 16) >= 2);
        Assert.DoesNotContain(items, item => item.Command != MavlinkCommandIds.DoChangeSpeed && item.Command != MavlinkCommandIds.NavTakeoff && item.Command != 16 && item.Command != MavlinkCommandIds.NavReturnToLaunch);
    }

    [Fact]
    public void Compiler_CompilesTimedLoiterAtPointWithDuration()
    {
        var now = DateTimeOffset.UtcNow;
        var mission = ValidMission(now) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("loiter", FlightMissionStepKind.TimedLoiter, "poi", "Point", "hash", [new FlightMissionCoordinate(43.7001, -79.4001)], LoiterDurationSeconds: 45),
                new FlightMissionStep("land", FlightMissionStepKind.Land)
            ]
        };

        var loiter = Assert.Single(new Px4FlightMissionCompiler().Compile(mission, TargetAt(43.7, -79.4)).Where(item => item.Command == MavlinkCommandIds.NavLoiterTime));

        Assert.Equal(45, loiter.Param1);
        Assert.Equal(437001000, loiter.LatitudeE7);
        Assert.Equal(-794001000, loiter.LongitudeE7);
    }

    [Fact]
    public void Compiler_ReportsCameraIntentAsMetadataOnly()
    {
        var mission = ValidMission(DateTimeOffset.UtcNow) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("camera", FlightMissionStepKind.CameraCaptureIntent, CameraIntent: new FlightMissionCameraIntent("Photo", TriggerDistanceMetres: 5)),
                new FlightMissionStep("poi", FlightMissionStepKind.PointOfInterest, Coordinates: [new FlightMissionCoordinate(43.7001, -79.4001)]),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ]
        };

        var findings = new Px4FlightMissionCompiler().Validate(mission, TargetAt(43.7, -79.4));

        Assert.Contains(findings, finding => finding.Code == "MISSION_CAMERA_INTENT_NOT_TRANSMITTED" && finding.Severity == WorkflowFindingSeverity.Warning);
    }

    [Fact]
    public void Compiler_Decompile_PreservesSpeedAndTimedLoiter()
    {
        var now = DateTimeOffset.UtcNow;
        var source = ValidMission(now) with
        {
            CruiseSpeedMetresPerSecond = 4,
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("point", FlightMissionStepKind.PointOfInterest, Coordinates: [new FlightMissionCoordinate(43.7000, -79.4000)]),
                new FlightMissionStep("loiter", FlightMissionStepKind.TimedLoiter, Coordinates: [new FlightMissionCoordinate(43.7001, -79.4001)], CruiseSpeedMetresPerSecond: 6, LoiterDurationSeconds: 25),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ]
        };
        var compiler = new Px4FlightMissionCompiler();

        var downloaded = compiler.Decompile("Downloaded", compiler.Compile(source, TargetAt(43.7, -79.4)), now);

        Assert.Equal(4, downloaded.CruiseSpeedMetresPerSecond);
        var loiter = Assert.Single(downloaded.Steps.Where(step => step.Kind == FlightMissionStepKind.TimedLoiter));
        Assert.Equal(25, loiter.LoiterDurationSeconds);
        Assert.Equal(6, loiter.CruiseSpeedMetresPerSecond);
    }

    [Fact]
    public void SurveyPreview_IsDeterministicAndReportsDerivedCoverage()
    {
        var survey = new FlightMissionStep("survey", FlightMissionStepKind.SurveyZone, Coordinates:
        [
            new FlightMissionCoordinate(43.7000, -79.4010),
            new FlightMissionCoordinate(43.7010, -79.4010),
            new FlightMissionCoordinate(43.7010, -79.4000),
            new FlightMissionCoordinate(43.7000, -79.4000)
        ], Survey: new FlightMissionSurveyOptions(25, 30, 5));
        var mission = ValidMission(DateTimeOffset.UtcNow) with
        {
            Steps = [new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff), survey, new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)]
        };
        var compiler = new Px4FlightMissionCompiler();

        var first = compiler.Preview(mission, TargetAt(43.7, -79.4));
        var second = compiler.Preview(mission, TargetAt(43.7, -79.4));

        Assert.Equal(first.Route, second.Route);
        Assert.True(first.SurveyLineCount > 0);
        Assert.True(first.SurveyAreaSquareMetres > 0);
    }

    [Fact]
    public void CorridorRoute_GeneratesDeterministicAlternatingOffsetPasses()
    {
        var corridor = new FlightMissionStep("corridor", FlightMissionStepKind.CorridorScan, "route", "Road", "hash",
        [
            new FlightMissionCoordinate(43.7000, -79.4010),
            new FlightMissionCoordinate(43.7010, -79.4010)
        ], Corridor: new FlightMissionCorridorOptions(
            CorridorWidthMetres: 40,
            LineSpacingMetres: 20,
            TurnaroundDistanceMetres: 5,
            ReverseDirection: false,
            EntrySide: FlightMissionCorridorEntrySide.Left,
            FrontLapPercent: 75,
            SideLapPercent: 70));

        var first = Px4FlightMissionCompiler.CorridorRoute(corridor);
        var second = Px4FlightMissionCompiler.CorridorRoute(corridor);

        Assert.Equal(first, second);
        Assert.Equal(6, first.Count);
        Assert.True(first[1].LatitudeDegrees > first[0].LatitudeDegrees);
        Assert.True(first[3].LatitudeDegrees < first[2].LatitudeDegrees);
        Assert.NotEqual(first[0].LongitudeDegrees, first[2].LongitudeDegrees);
    }

    [Fact]
    public void Compiler_ExpandsCorridorIntoPx4WaypointsAndRetainsCameraIntentOffWire()
    {
        var corridor = new FlightMissionStep("corridor", FlightMissionStepKind.CorridorScan, Coordinates:
        [
            new FlightMissionCoordinate(43.7000, -79.4010),
            new FlightMissionCoordinate(43.7010, -79.4010)
        ], Corridor: new FlightMissionCorridorOptions(40, 20, 5, CameraIntent: new FlightMissionCameraIntent("Photo", TriggerDistanceMetres: 10)));
        var mission = ValidMission(DateTimeOffset.UtcNow) with
        {
            Steps =
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                corridor,
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ]
        };

        var compiler = new Px4FlightMissionCompiler();
        var items = compiler.Compile(mission, TargetAt(43.7, -79.4));
        var findings = compiler.Validate(mission, TargetAt(43.7, -79.4));

        Assert.Equal(Px4FlightMissionCompiler.CorridorRoute(corridor).Count, items.Count(item => item.Command == 16));
        Assert.Contains(findings, item => item.Code == "MISSION_CAMERA_INTENT_NOT_TRANSMITTED");
    }

    [Fact]
    public async Task FenceStore_RejectsSelfIntersectingPolygon()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robot-command-fence-{Guid.NewGuid():N}");
        try
        {
            var store = new Px4FenceLibraryStore(root);
            var invalid = new Px4FenceDocument(Px4FenceDocument.CurrentSchemaVersion, "fence-cross", "Cross", Px4FenceKind.Inclusion,
            [
                new FlightMissionCoordinate(43.70, -79.40),
                new FlightMissionCoordinate(43.71, -79.39),
                new FlightMissionCoordinate(43.70, -79.39),
                new FlightMissionCoordinate(43.71, -79.40)
            ], null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(invalid, token: TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static FlightMissionDocument ValidMission(DateTimeOffset now)
        => new(
            FlightMissionDocument.CurrentSchemaVersion,
            "mission-alpha",
            "Alpha",
            25,
            [
                new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff),
                new FlightMissionStep("poi", FlightMissionStepKind.PointOfInterest, "geometry-poi", "Survey point", "source-hash", [new FlightMissionCoordinate(43.7001, -79.4001)]),
                new FlightMissionStep("rtl", FlightMissionStepKind.ReturnToLaunch)
            ],
            now,
            now);
}
