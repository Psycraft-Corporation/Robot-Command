using RobotCommand.Models;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapOrientationResolverTests
{
    [Fact]
    public void ResolveRotation_ReturnsZeroForNorthUp()
    {
        Assert.Equal(0, MapOrientationResolver.ResolveRotation(Scene(MapOrientationMode.NorthUp, 135)));
    }

    [Theory]
    [InlineData(90, -90)]
    [InlineData(270, 90)]
    [InlineData(360, 0)]
    public void ResolveRotation_KeepsSelectedCourseAtTop(double heading, double expected)
    {
        Assert.Equal(expected, MapOrientationResolver.ResolveRotation(Scene(MapOrientationMode.CourseUp, heading)), 6);
    }

    [Fact]
    public void ResolveRotation_FallsBackToNorthWithoutSelectedHeading()
    {
        Assert.Equal(0, MapOrientationResolver.ResolveRotation(Scene(MapOrientationMode.CourseUp, null)));
    }

    private static OperationalMapScene Scene(MapOrientationMode mode, double? heading)
        => new(
            MapFrameKind.GlobalWgs84,
            "Global",
            [new MapVehicleVisual("vehicle", "Vehicle", -79.38, 43.65, heading, AvailabilityState.Online, true)],
            [],
            "vehicle",
            MapViewportMode.FitAll,
            true)
        {
            OrientationMode = mode
        };
}
