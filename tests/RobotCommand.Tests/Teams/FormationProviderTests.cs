using RobotCommand.Models;
using RobotCommand.Services.Operations;
using Xunit;

namespace RobotCommand.Tests;

public sealed class FormationProviderTests
{
    [Fact]
    public void Line_UsesExactEndpointsAndEqualSpacing()
    {
        var provider = new LineFormationProvider();
        var units = new[]
        {
            Unit("one"),
            Unit("two"),
            Unit("three")
        };

        var result = provider.Calculate(
            units,
            new MapCommandTarget(43, -79),
            new MapCommandTarget(44, -78));

        Assert.Equal("line", result.FormationId);
        Assert.Equal(["one", "two", "three"], result.Destinations.Select(item => item.VehicleId));
        Assert.Equal(43, result.Destinations[0].LatitudeDegrees);
        Assert.Equal(-79, result.Destinations[0].LongitudeDegrees);
        Assert.Equal(43.5, result.Destinations[1].LatitudeDegrees);
        Assert.Equal(-78.5, result.Destinations[1].LongitudeDegrees);
        Assert.Equal(44, result.Destinations[2].LatitudeDegrees);
        Assert.Equal(-78, result.Destinations[2].LongitudeDegrees);
        var linePreview = Assert.Single(result.PreviewPaths);
        Assert.False(linePreview.Closed);
        Assert.Equal([new MapCommandTarget(43, -79), new MapCommandTarget(44, -78)], linePreview.Points);
    }

    [Fact]
    public void Line_RejectsFewerThanTwoUnitsAndInvalidCoordinates()
    {
        var provider = new LineFormationProvider();

        Assert.False(provider.CanCalculate([Unit("one")]));
        Assert.Throws<ArgumentException>(() => provider.Calculate(
            [Unit("one"), Unit("two")],
            new MapCommandTarget(91, 0),
            new MapCommandTarget(44, -78)));
    }

    [Fact]
    public void Circle_PlacesFirstUnitAtStartAndDistributesOthersAroundTheCircle()
    {
        var provider = new CircleFormationProvider();
        var units = new[]
        {
            Unit("one"),
            Unit("two"),
            Unit("three"),
            Unit("four")
        };

        var result = provider.Calculate(
            units,
            new MapCommandTarget(43, -79),
            new MapCommandTarget(43, -78));

        Assert.Equal("circle", result.FormationId);
        Assert.Equal(["one", "two", "three", "four"],
            result.Destinations.Select(item => item.VehicleId));
        Assert.Equal(43, result.Destinations[0].LatitudeDegrees);
        Assert.Equal(-79, result.Destinations[0].LongitudeDegrees);

        var centerLatitude = 43d;
        var centerLongitude = -78.5d;
        var radii = result.Destinations
            .Select(item => DistanceMetres(
                centerLatitude,
                centerLongitude,
                item.LatitudeDegrees,
                item.LongitudeDegrees))
            .ToArray();
        Assert.All(radii, radius => Assert.InRange(radius, 38_000, 42_000));

        var bearings = result.Destinations
            .Select(item => BearingDegrees(
                centerLatitude,
                centerLongitude,
                item.LatitudeDegrees,
                item.LongitudeDegrees))
            .ToArray();
        Assert.Equal(4, bearings.DistinctBy(value => Math.Round(value, 1)).Count());
        var circlePreview = Assert.Single(result.PreviewPaths);
        Assert.True(circlePreview.Closed);
        Assert.True(circlePreview.Points.Count >= 73);
        Assert.Equal(circlePreview.Points[0], circlePreview.Points[^1]);
    }

    [Fact]
    public void Circle_WithTwoUnitsUsesTheOppositePointForTheSecondUnit()
    {
        var provider = new CircleFormationProvider();
        var result = provider.Calculate(
            [Unit("one"), Unit("two")],
            new MapCommandTarget(43, -79),
            new MapCommandTarget(43, -78));

        Assert.Equal(43, result.Destinations[1].LatitudeDegrees, precision: 4);
        Assert.Equal(-78, result.Destinations[1].LongitudeDegrees, precision: 4);
    }

    [Fact]
    public void Circle_RejectsFewerThanTwoUnitsAndInvalidCoordinates()
    {
        var provider = new CircleFormationProvider();

        Assert.False(provider.CanCalculate([Unit("one")]));
        Assert.Throws<ArgumentException>(() => provider.Calculate(
            [Unit("one"), Unit("two")],
            new MapCommandTarget(43, -79),
            new MapCommandTarget(91, -78)));
    }

    [Fact]
    public void Rectangle_UsesOppositeCornersAndDistributesUnitsAroundThePerimeter()
    {
        var provider = new RectangleFormationProvider();
        var units = new[]
        {
            Unit("one"),
            Unit("two"),
            Unit("three"),
            Unit("four")
        };

        var result = provider.Calculate(
            units,
            new MapCommandTarget(43, -79),
            new MapCommandTarget(44, -78));

        Assert.Equal("rectangle", result.FormationId);
        Assert.Equal(["one", "two", "three", "four"],
            result.Destinations.Select(item => item.VehicleId));
        Assert.Equal(new MapCommandTarget(43, -79),
            new MapCommandTarget(result.Destinations[0].LatitudeDegrees, result.Destinations[0].LongitudeDegrees));
        Assert.Equal(-78, result.Destinations[1].LongitudeDegrees, precision: 4);
        Assert.InRange(result.Destinations[1].LatitudeDegrees, 43d, 44d);
        Assert.Equal(new MapCommandTarget(44, -78),
            new MapCommandTarget(result.Destinations[2].LatitudeDegrees, result.Destinations[2].LongitudeDegrees));
        Assert.Equal(-79, result.Destinations[3].LongitudeDegrees, precision: 4);
        Assert.InRange(result.Destinations[3].LatitudeDegrees, 43d, 44d);

        var preview = Assert.Single(result.PreviewPaths);
        Assert.True(preview.Closed);
        Assert.Equal(5, preview.Points.Count);
        Assert.Equal(preview.Points[0], preview.Points[^1]);
        Assert.Equal(new MapCommandTarget(43, -79), preview.Points[0]);
        Assert.Equal(new MapCommandTarget(44, -78), preview.Points[2]);
    }

    [Fact]
    public void Rectangle_RejectsFewerThanTwoUnitsInvalidCoordinatesAndDegenerateCorners()
    {
        var provider = new RectangleFormationProvider();

        Assert.False(provider.CanCalculate([Unit("one")]));
        Assert.Throws<ArgumentException>(() => provider.Calculate(
            [Unit("one"), Unit("two")],
            new MapCommandTarget(43, -79),
            new MapCommandTarget(43, -78)));
        Assert.Throws<ArgumentException>(() => provider.Calculate(
            [Unit("one"), Unit("two")],
            new MapCommandTarget(43, -79),
            new MapCommandTarget(91, -78)));
    }

    private static double DistanceMetres(
        double firstLatitude,
        double firstLongitude,
        double secondLatitude,
        double secondLongitude)
    {
        const double earthRadiusMetres = 6_378_137;
        var firstLatitudeRadians = firstLatitude * Math.PI / 180;
        var secondLatitudeRadians = secondLatitude * Math.PI / 180;
        var deltaLatitude = (secondLatitude - firstLatitude) * Math.PI / 180;
        var deltaLongitude = (secondLongitude - firstLongitude) * Math.PI / 180;
        var haversine = Math.Pow(Math.Sin(deltaLatitude / 2), 2) +
                        Math.Cos(firstLatitudeRadians) * Math.Cos(secondLatitudeRadians) *
                        Math.Pow(Math.Sin(deltaLongitude / 2), 2);
        return earthRadiusMetres * 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
    }

    private static double BearingDegrees(
        double firstLatitude,
        double firstLongitude,
        double secondLatitude,
        double secondLongitude)
        => (Math.Atan2(
                Math.Sin((secondLongitude - firstLongitude) * Math.PI / 180) *
                    Math.Cos(secondLatitude * Math.PI / 180),
                Math.Cos(firstLatitude * Math.PI / 180) *
                    Math.Sin(secondLatitude * Math.PI / 180) -
                Math.Sin(firstLatitude * Math.PI / 180) *
                    Math.Cos(secondLatitude * Math.PI / 180) *
                    Math.Cos((secondLongitude - firstLongitude) * Math.PI / 180)) *
            180 / Math.PI + 360) % 360;

    private static VehicleRecord Unit(string id)
        => new(id, id, [$"connection-{id}"], $"logos-{id}", null,
            "Multicopter", "Air", "default", AvailabilityState.Online,
            "Ready", "Landed", "Disarmed", "Healthy", ["operator_control"]);
}
