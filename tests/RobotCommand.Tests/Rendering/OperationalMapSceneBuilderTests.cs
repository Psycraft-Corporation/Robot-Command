using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Operations;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperationalMapSceneBuilderTests
{
    private readonly OperationalMapSceneBuilder _builder = new();

    [Fact]
    public void Build_UsesGlobalCoordinatesAndMarksSelectedVehicle()
    {
        VehicleRecord[] vehicles =
        [
            Vehicle("alpha", "conn-a"),
            Vehicle("bravo", "conn-b")
        ];
        VehicleTelemetryRecord[] telemetry =
        [
            Telemetry("alpha", "conn-a", latitude: 43.65, longitude: -79.38, heading: 90),
            Telemetry("bravo", "conn-b", latitude: 43.66, longitude: -79.37, heading: 180)
        ];
        GeometryOverlayRecord[] geometry =
        [
            Geometry(
                "zone-a",
                "conn-a",
                MapFrameKind.GlobalWgs84,
                [
                    new OperationalPoint(-79.39, 43.64),
                    new OperationalPoint(-79.37, 43.64),
                    new OperationalPoint(-79.37, 43.66)
                ]),
            Geometry(
                "zone-b",
                "conn-b",
                MapFrameKind.GlobalWgs84,
                [
                    new OperationalPoint(-79.36, 43.65),
                    new OperationalPoint(-79.35, 43.65),
                    new OperationalPoint(-79.35, 43.67)
                ])
        ];

        var scene = _builder.Build(
            vehicles,
            telemetry,
            geometry,
            "alpha",
            MapViewportMode.FollowSelected,
            geometryVisible: true);

        Assert.Equal(MapFrameKind.GlobalWgs84, scene.Frame);
        Assert.Equal(2, scene.Vehicles.Count);
        Assert.True(scene.Vehicles.Single(item => item.VehicleId == "alpha").Selected);
        Assert.Equal(-79.38, scene.Vehicles.Single(item => item.VehicleId == "alpha").X, 6);
        Assert.Equal(2, scene.Geometries.Count);
        Assert.Contains(scene.Geometries, item => item.GeometryId == "zone-a");
        Assert.Contains(scene.Geometries, item => item.GeometryId == "zone-b");
    }

    [Fact]
    public void Build_ScopesLocalFramesToSelectedVehicleConnection()
    {
        VehicleRecord[] vehicles =
        [
            Vehicle("alpha", "conn-a"),
            Vehicle("bravo", "conn-b")
        ];
        VehicleTelemetryRecord[] telemetry =
        [
            Telemetry("alpha", "conn-a", north: 10, east: 20),
            Telemetry("bravo", "conn-b", north: 5000, east: 9000)
        ];
        GeometryOverlayRecord[] geometry =
        [
            Geometry(
                "route-a",
                "conn-a",
                MapFrameKind.LocalNed,
                [new OperationalPoint(30, 40, -5)]),
            Geometry(
                "route-b",
                "conn-b",
                MapFrameKind.LocalNed,
                [new OperationalPoint(8000, 9000, -5)])
        ];

        var scene = _builder.Build(
            vehicles,
            telemetry,
            geometry,
            "alpha",
            MapViewportMode.FitAll,
            geometryVisible: true);

        var vehicle = Assert.Single(scene.Vehicles);
        Assert.Equal("alpha", vehicle.VehicleId);
        Assert.Equal(20, vehicle.X);
        Assert.Equal(10, vehicle.Y);

        var overlay = Assert.Single(scene.Geometries);
        Assert.Equal("route-a", overlay.GeometryId);
        Assert.Equal(40, overlay.Points[0].X);
        Assert.Equal(30, overlay.Points[0].Y);
        Assert.Equal(5, overlay.Points[0].Z);
    }

    [Fact]
    public void Build_CanHideGeometryWithoutRemovingVehicles()
    {
        var scene = _builder.Build(
            [Vehicle("alpha", "conn-a")],
            [Telemetry("alpha", "conn-a", latitude: 43.65, longitude: -79.38)],
            [Geometry("zone-a", "conn-a", MapFrameKind.GlobalWgs84, [new OperationalPoint(-79.38, 43.65)])],
            "alpha",
            MapViewportMode.FitAll,
            geometryVisible: false);

        Assert.Single(scene.Vehicles);
        Assert.Empty(scene.Geometries);
        Assert.True(scene.HasData);
        Assert.False(scene.GeometryVisible);
    }

    [Fact]
    public void Build_CanShowMissionPreviewWhenNormalGeometryIsHidden()
    {
        var scene = _builder.Build(
            [Vehicle("alpha", "conn-a")],
            [Telemetry("alpha", "conn-a", latitude: 43.65, longitude: -79.38)],
            [
                Geometry("normal", "conn-a", MapFrameKind.GlobalWgs84, [new OperationalPoint(-79.38, 43.65)]),
                Geometry("mission", "conn-a", MapFrameKind.GlobalWgs84, [new OperationalPoint(-79.37, 43.66)], kind: "FlightMissionPreviewPoint")
            ],
            "alpha",
            MapViewportMode.FitAll,
            geometryVisible: false,
            missionPreviewVisible: true);

        var preview = Assert.Single(scene.Geometries);
        Assert.Equal("mission", preview.GeometryId);
        Assert.False(scene.GeometryVisible);
    }

    [Fact]
    public void Build_CanHideMissionPreviewWithoutHidingNormalGeometry()
    {
        var scene = _builder.Build(
            [Vehicle("alpha", "conn-a")],
            [Telemetry("alpha", "conn-a", latitude: 43.65, longitude: -79.38)],
            [
                Geometry("normal", "conn-a", MapFrameKind.GlobalWgs84, [new OperationalPoint(-79.38, 43.65)]),
                Geometry("mission", "conn-a", MapFrameKind.GlobalWgs84, [new OperationalPoint(-79.37, 43.66)], kind: "FlightMissionPreviewPoint")
            ],
            "alpha",
            MapViewportMode.FitAll,
            geometryVisible: true,
            missionPreviewVisible: false);

        var normal = Assert.Single(scene.Geometries);
        Assert.Equal("normal", normal.GeometryId);
    }

    [Fact]
    public void MissionPreview_UsesOneConnectedRouteAndUnlabelledStepMarkers()
    {
        var mission = new FlightMissionSnapshot(
            "mission",
            "Survey mission",
            20,
            [
                new FlightMissionStep(
                    "first",
                    FlightMissionStepKind.TimedLoiter,
                    SourceGeometryName: "PoI 1",
                    Coordinates: [new FlightMissionCoordinate(43.65, -79.38)]),
                new FlightMissionStep(
                    "second",
                    FlightMissionStepKind.TimedLoiter,
                    SourceGeometryName: "PoI 2",
                    Coordinates: [new FlightMissionCoordinate(43.66, -79.37)])
            ],
            "Valid",
            [],
            DateTimeOffset.UtcNow,
            "hash");

        var overlays = OperationalMapViewModel.BuildMissionPreviewOverlays(mission);
        var route = Assert.Single(overlays.Where(item => item.Kind == "FlightMissionPreviewRoute"));

        Assert.Equal(2, route.Points.Count);
        Assert.All(overlays, item => Assert.True(string.IsNullOrWhiteSpace(item.Name)));
        Assert.Equal(2, overlays.Count(item => item.Kind == "FlightMissionPreviewPoint"));
    }

    [Fact]
    public void Build_CanHidePolicyWithoutHidingOperationalGeometry()
    {
        var scene = _builder.Build(
            [Vehicle("alpha", "conn-a")],
            [Telemetry("alpha", "conn-a", latitude: 43.65, longitude: -79.38)],
            [
                Geometry(
                    "route-a",
                    "conn-a",
                    MapFrameKind.GlobalWgs84,
                    [new OperationalPoint(-79.38, 43.65), new OperationalPoint(-79.37, 43.66)]),
                Geometry(
                    "exclusion-a",
                    "conn-a",
                    MapFrameKind.GlobalWgs84,
                    [
                        new OperationalPoint(-79.39, 43.64),
                        new OperationalPoint(-79.37, 43.64),
                        new OperationalPoint(-79.37, 43.66)
                    ],
                    policyKind: "safety",
                    policyConstraint: "exclusion")
            ],
            "alpha",
            MapViewportMode.FitAll,
            geometryVisible: true,
            policyVisible: false);

        var geometry = Assert.Single(scene.Geometries);
        Assert.Equal("route-a", geometry.GeometryId);
        Assert.False(geometry.IsPolicy);
        Assert.False(scene.PolicyVisible);
    }

    [Fact]
    public void Build_HighlightsGeometryReferencedBySelectedPlan()
    {
        var scene = _builder.Build(
            [Vehicle("alpha", "conn-a")],
            [Telemetry("alpha", "conn-a", latitude: 43.65, longitude: -79.38)],
            [Geometry(
                "mission-area",
                "conn-a",
                MapFrameKind.GlobalWgs84,
                [
                    new OperationalPoint(-79.39, 43.64),
                    new OperationalPoint(-79.37, 43.64),
                    new OperationalPoint(-79.37, 43.66)
                ])],
            "alpha",
            MapViewportMode.FitAll,
            geometryVisible: true,
            highlightedGeometryIds: new HashSet<string>(["mission-area"], StringComparer.Ordinal));

        Assert.True(Assert.Single(scene.Geometries).Highlighted);
    }

    private static VehicleRecord Vehicle(string id, string connectionId)
        => new(
            id,
            id,
            [connectionId],
            $"logos-{id}",
            null,
            "Multicopter",
            "Air",
            "test",
            AvailabilityState.Online);

    private static VehicleTelemetryRecord Telemetry(
        string vehicleId,
        string connectionId,
        double? latitude = null,
        double? longitude = null,
        double? north = null,
        double? east = null,
        double? heading = null)
        => new(
            $"{connectionId}:{vehicleId}",
            vehicleId,
            connectionId,
            $"logos-{vehicleId}",
            AvailabilityState.Online,
            false,
            "OnGround",
            "Multicopter",
            "Ready",
            "Healthy",
            "Ready",
            latitude,
            longitude,
            null,
            null,
            north,
            east,
            null,
            null,
            null,
            null,
            heading,
            false,
            "OK",
            string.Empty,
            DateTimeOffset.UtcNow);

    private static GeometryOverlayRecord Geometry(
        string id,
        string connectionId,
        MapFrameKind frame,
        IReadOnlyList<OperationalPoint> points,
        string policyKind = "none",
        string policyConstraint = "none",
        string kind = "WaypointSequence")
        => new(
            $"{connectionId}:{id}",
            id,
            connectionId,
            null,
            id,
            kind,
            frame,
            false,
            points,
            [],
            policyKind,
            policyConstraint,
            DateTimeOffset.UtcNow);
}
