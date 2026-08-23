using RobotCommand.Controls;
using Xunit;

namespace RobotCommand.Tests;

public sealed class UnitMarkerGeometryTests
{
    [Fact]
    public void Create_ReturnsSharedForwardArrowWithSymmetricTail()
    {
        var points = UnitMarkerGeometry.Create(26, 34);

        Assert.Equal(4, points.Length);
        Assert.Equal(13, points[0].X);
        Assert.Equal(1, points[0].Y);
        Assert.Equal(points[1].Y, points[3].Y);
        Assert.Equal(24, points[1].X);
        Assert.Equal(2, points[3].X);
        Assert.Equal(points[0].X, points[2].X);
        Assert.True(points[1].Y - points[0].Y > points[1].X - points[0].X);
    }

    [Fact]
    public void CreateCentered_PreservesTheSameShapeAroundTheMarkerOrigin()
    {
        var points = UnitMarkerGeometry.CreateCentered(26, 34);

        Assert.Equal(0, points[0].X);
        Assert.Equal(-16, points[0].Y);
        Assert.Equal(points[1].Y, points[3].Y);
        Assert.Equal(points[1].X, -points[3].X);
        Assert.Equal(points[0].X, points[2].X);
    }
}
