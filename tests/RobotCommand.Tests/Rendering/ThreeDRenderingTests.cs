using RobotCommand.Core;
using RobotCommand.Rendering;
using Xunit;

namespace RobotCommand.Tests;

public sealed class ThreeDRenderingTests
{
    [Fact]
    public void ProjectionPlacesPointInFrontOfCamera()
    {
        var camera = new ThreeDCameraSnapshot(
            new ThreeDVector3(0, 0, 0), 0, 0, 0, 60, 0.1, 1000, 2);

        var projected = ThreeDProjection.Project(new ThreeDVector3(0, 0, 10), camera, 800, 600);

        Assert.True(projected.Visible);
        Assert.Equal(400, projected.X, 6);
        Assert.Equal(300, projected.Y, 6);
        Assert.Equal(10, projected.Depth, 6);
    }

    [Fact]
    public void ProjectionClipsPointsBehindCamera()
    {
        var camera = new ThreeDCameraSnapshot(
            ThreeDVector3.Zero, 0, 0, 0, 60, 0.1, 1000, 2);

        Assert.False(ThreeDProjection.Project(new ThreeDVector3(0, 0, -1), camera, 800, 600).Visible);
    }

    [Fact]
    public void OrbitPositionKeepsTargetAtViewportCenter()
    {
        var target = new ThreeDVector3(3, 2, -4);
        var cameraPosition = ThreeDProjection.OrbitPosition(target, 42, 25, 80);
        var camera = new ThreeDCameraSnapshot(cameraPosition, 42, 25, 0, 60, 0.1, 1000, 2, true, target, 80);

        var projected = ThreeDProjection.Project(target, camera, 800, 600);

        Assert.True(projected.Visible);
        Assert.Equal(400, projected.X, 6);
        Assert.Equal(300, projected.Y, 6);
    }

    [Fact]
    public async Task NullRendererHasSafeLifecycle()
    {
        await using var renderer = new NullThreeDRenderer();

        await renderer.InitializeAsync(ThreeDRenderBackendPolicy.Auto);
        await renderer.ResizeAsync(640, 480);
        await renderer.RenderAsync(CreateScene());

        Assert.True(renderer.Status.IsInitialized);
        Assert.False(renderer.Status.IsHardwareAccelerated);
        Assert.Equal("None", renderer.Status.Backend);
    }

    [Fact]
    public void SceneSnapshotRetainsStableEntityIdentity()
    {
        var primitive = new ThreeDPrimitiveSnapshot(
            "unit:ghost-1", ThreeDPrimitiveKind.Arrow, ThreeDTransform.Identity, "#55B7E8", "Ghost 1");
        var scene = CreateScene() with { Primitives = [primitive] };

        Assert.Equal("unit:ghost-1", scene.Primitives[0].Id);
        Assert.Equal("Ghost 1", scene.Primitives[0].Label);
    }

    private static ThreeDSceneSnapshot CreateScene()
        => new(
            1,
            DateTimeOffset.UtcNow,
            new ThreeDOriginSnapshot(43.6532, -79.3832, 0, DateTimeOffset.UtcNow),
            new ThreeDCameraSnapshot(new(0, 60, -120), 0, -18, 0, 60, 0.1, 5000, 2),
            new ThreeDGridSnapshot(),
            new ThreeDAxisSnapshot(),
            [],
            [],
            new ThreeDHudSnapshot("Software", "Ready", "Camera", 0, 0),
            ThreeDRendererStatus.Uninitialized);
}
