using RobotCommand.Models;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapCoordinateProjectorTests
{
    [Fact]
    public void TryProject_ProjectsLongitudeAndLatitudeToWebMercator()
    {
        var projected = MapCoordinateProjector.TryProject(-79.3832, 43.6532, out var point);

        Assert.True(projected);
        Assert.InRange(point.X, -8_840_000, -8_830_000);
        Assert.InRange(point.Y, 5_405_000, 5_415_000);
    }

    [Theory]
    [InlineData(-181, 0)]
    [InlineData(181, 0)]
    [InlineData(0, 86)]
    [InlineData(0, -86)]
    public void TryProject_RejectsCoordinatesOutsideWebMercator(double longitude, double latitude)
    {
        Assert.False(MapCoordinateProjector.TryProject(longitude, latitude, out _));
    }

    [Fact]
    public void TryUnproject_RoundTripsWebMercatorCoordinate()
    {
        Assert.True(MapCoordinateProjector.TryProject(-79.3832, 43.6532, out var projected));
        Assert.True(MapCoordinateProjector.TryUnproject(projected.X, projected.Y, out var longitude, out var latitude));

        Assert.Equal(-79.3832, longitude, 6);
        Assert.Equal(43.6532, latitude, 6);
    }

    [Fact]
    public void TryUnproject_RejectsNonFiniteCoordinates()
    {
        Assert.False(MapCoordinateProjector.TryUnproject(double.NaN, 0, out _, out _));
        Assert.False(MapCoordinateProjector.TryUnproject(0, double.PositiveInfinity, out _, out _));
    }

    [Fact]
    public void TryBuildExtent_AddsUsablePaddingForSingleVehicle()
    {
        Assert.True(MapCoordinateProjector.TryBuildExtent([new ProjectedMapPoint(1000, 2000)], out var extent));

        Assert.True(extent.Width >= 500);
        Assert.True(extent.Height >= 500);
        Assert.True(extent.MinX < 1000);
        Assert.True(extent.MaxY > 2000);
    }

    [Fact]
    public void TryBuildExtent_ReturnsFalseWithoutValidPoints()
    {
        Assert.False(MapCoordinateProjector.TryBuildExtent([], out _));
    }
}
