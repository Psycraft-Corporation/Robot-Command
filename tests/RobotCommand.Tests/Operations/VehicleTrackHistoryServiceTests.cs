using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class VehicleTrackHistoryServiceTests
{
    [Fact]
    public void Record_BuildsTrailFromOrderedGlobalTelemetry()
    {
        var service = CreateService();
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");

        service.Record([Telemetry(-79.3800, 43.6500, start)], start);
        service.Record([Telemetry(-79.3798, 43.6502, start.AddSeconds(5))], start.AddSeconds(5));

        var trail = Assert.Single(service.BuildTrails([Vehicle()], "vehicle"));
        Assert.Equal("vehicle", trail.VehicleId);
        Assert.True(trail.Selected);
        Assert.Equal(2, trail.Points.Count);
        Assert.Equal(-79.3800, trail.Points[0].X, 6);
        Assert.Equal(43.6502, trail.Points[1].Y, 6);
    }

    [Fact]
    public void Record_DoesNotBuildTrailFromLandedGpsDrift()
    {
        var service = CreateService(minimumDistanceMetres: 0);
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");

        service.Record([Telemetry(-79.3800, 43.6500, start, landedState: "Landed")], start);
        service.Record([Telemetry(-79.3790, 43.6510, start.AddSeconds(5), landedState: "Landed")], start.AddSeconds(5));

        Assert.Empty(service.BuildTrails([Vehicle()], null));
    }

    [Fact]
    public void Record_SuppressesTinyRapidUpdatesButKeepsHeartbeatSample()
    {
        var service = CreateService(minimumDistanceMetres: 10);
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");

        service.Record([Telemetry(-79.380000, 43.650000, start)], start);
        service.Record([Telemetry(-79.379995, 43.650005, start.AddSeconds(5))], start.AddSeconds(5));
        Assert.Empty(service.BuildTrails([Vehicle()], null));

        service.Record([Telemetry(-79.379995, 43.650005, start.AddSeconds(11))], start.AddSeconds(11));
        Assert.Equal(2, Assert.Single(service.BuildTrails([Vehicle()], null)).Points.Count);
    }

    [Fact]
    public void Record_PrunesSamplesOutsideMaximumAge()
    {
        var service = CreateService(maxAgeMinutes: 1);
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");

        service.Record([Telemetry(-79.3800, 43.6500, start)], start);
        service.Record([Telemetry(-79.3700, 43.6600, start.AddMinutes(2))], start.AddMinutes(2));

        Assert.Empty(service.BuildTrails([Vehicle()], null));
    }

    [Fact]
    public void Record_BoundsPointCountPerVehicle()
    {
        var service = CreateService(maximumPoints: 3, minimumDistanceMetres: 0);
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        for (var index = 0; index < 4; index++)
        {
            service.Record(
                [Telemetry(-79.38 + (index * 0.001), 43.65 + (index * 0.001), start.AddSeconds(index))],
                start.AddSeconds(index));
        }

        var trail = Assert.Single(service.BuildTrails([Vehicle()], null));
        Assert.Equal(3, trail.Points.Count);
        Assert.Equal(-79.379, trail.Points[0].X, 6);
    }

    [Fact]
    public void Clear_RemovesOnlyRequestedVehicle()
    {
        var service = CreateService(minimumDistanceMetres: 0);
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        service.Record(
        [
            Telemetry(-79.38, 43.65, start, "vehicle", "conn"),
            Telemetry(-79.40, 43.67, start, "other", "other-conn")
        ], start);
        service.Record(
        [
            Telemetry(-79.37, 43.66, start.AddSeconds(1), "vehicle", "conn"),
            Telemetry(-79.39, 43.68, start.AddSeconds(1), "other", "other-conn")
        ], start.AddSeconds(1));

        service.Clear("vehicle");

        var trails = service.BuildTrails(
        [
            Vehicle(),
            Vehicle("other", "other-conn")
        ], null);
        Assert.DoesNotContain(trails, item => item.VehicleId == "vehicle");
        Assert.Contains(trails, item => item.VehicleId == "other");
    }

    [Fact]
    public void BuildTrailsForSelectedVehicles_HighlightsEverySelectedTrail()
    {
        var service = CreateService(minimumDistanceMetres: 0);
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        service.Record(
        [
            Telemetry(-79.38, 43.65, start, "vehicle", "conn"),
            Telemetry(-79.40, 43.67, start, "other", "other-conn")
        ], start);
        service.Record(
        [
            Telemetry(-79.37, 43.66, start.AddSeconds(1), "vehicle", "conn"),
            Telemetry(-79.39, 43.68, start.AddSeconds(1), "other", "other-conn")
        ], start.AddSeconds(1));

        var trails = service.BuildTrailsForSelectedVehicles(
            [Vehicle(), Vehicle("other", "other-conn")],
            new HashSet<string>(["vehicle", "other"], StringComparer.Ordinal));

        Assert.Equal(2, trails.Count(item => item.Selected));
    }

    private static VehicleTrackHistoryService CreateService(
        int maxAgeMinutes = 15,
        int maximumPoints = 600,
        double minimumDistanceMetres = 2)
        => new(new AppConfiguration
        {
            MapTrailMaxAgeMinutes = maxAgeMinutes,
            MapTrailMaxPointsPerVehicle = maximumPoints,
            MapTrailMinimumDistanceMetres = minimumDistanceMetres
        });

    private static VehicleRecord Vehicle(string id = "vehicle", string connectionId = "conn")
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
        double longitude,
        double latitude,
        DateTimeOffset observedAt,
        string vehicleId = "vehicle",
        string connectionId = "conn",
        string landedState = "Flying")
        => new(
            $"{connectionId}:{vehicleId}",
            vehicleId,
            connectionId,
            $"logos-{vehicleId}",
            AvailabilityState.Online,
            false,
            landedState,
            "Multicopter",
            "Ready",
            "Healthy",
            "Ready",
            latitude,
            longitude,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            false,
            "OK",
            string.Empty,
            observedAt);
}
