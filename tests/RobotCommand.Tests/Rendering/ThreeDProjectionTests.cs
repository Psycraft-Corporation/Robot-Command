using RobotCommand.Core;
using RobotCommand.Rendering;
using Xunit;

namespace RobotCommand.Tests;

public sealed class ThreeDProjectionTests
{
    [Fact]
    public void GroundPlaneIsClippedInsteadOfDiscardedWhenNearCamera()
    {
        var camera = Camera(new(0, 1, -1), nearClip: 0.5, farClip: 100);
        var plane = new[]
        {
            new ThreeDVector3(-10, 0, -10),
            new ThreeDVector3(10, 0, -10),
            new ThreeDVector3(10, 0, 10),
            new ThreeDVector3(-10, 0, 10)
        };

        var projected = ThreeDProjection.ProjectPolygon(plane, camera, 800, 600);

        Assert.True(projected.Count >= 3);
        Assert.All(projected, point => Assert.True(point.Visible));
    }

    [Fact]
    public void GridLineIsClippedAtNearPlaneInsteadOfDisappearing()
    {
        var camera = Camera(new(0, 0, 0), nearClip: 1, farClip: 100);

        var visible = ThreeDProjection.TryProjectSegment(
            new ThreeDVector3(0, 0, 0),
            new ThreeDVector3(0, 0, 10),
            camera,
            800,
            600,
            out var from,
            out var to);

        Assert.True(visible);
        Assert.True(from.Visible);
        Assert.True(to.Visible);
        Assert.InRange(from.Depth, 1, 1.001);
    }

    [Fact]
    public void FullyClippedGeometryRemainsOmitted()
    {
        var camera = Camera(new(0, 0, 0), nearClip: 1, farClip: 100);

        var projected = ThreeDProjection.ProjectPolygon(
            [new(0, 0, -10), new(1, 0, -10), new(1, 1, -10), new(0, 1, -10)],
            camera,
            800,
            600);

        Assert.Empty(projected);
    }

    [Fact]
    public void FormationPreviewUsesGroundPlaneWithoutAxes()
    {
        var scene = FormationAuthoringSceneBuilder.Build(null, null);

        Assert.False(scene.Axes.Visible);
        Assert.Contains(scene.Primitives, primitive => primitive.Kind == ThreeDPrimitiveKind.GroundPlane);
    }

    [Fact]
    public void GroundFootprintProvidesVisibleGridBoundsForAimedDownCamera()
    {
        var camera = new ThreeDCameraSnapshot(new(0, 100, -100), 0, 45, 0, 60, 0.1, 10000, 2);

        var footprint = ThreeDProjection.GroundFootprint(camera, 800, 600);

        Assert.True(footprint.Count >= 2);
        Assert.All(footprint, point => Assert.Equal(0, point.Y));
    }

    private static ThreeDCameraSnapshot Camera(ThreeDVector3 position, double nearClip, double farClip)
        => new(position, 0, 0, 0, 60, nearClip, farClip, 1, false);
}
