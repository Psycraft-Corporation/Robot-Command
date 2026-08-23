using RobotCommand.Core;
using RobotCommand.Rendering;
using Xunit;

namespace RobotCommand.Tests;

public sealed class ThreeDWorldSceneTests
{
    [Fact]
    public void ToLocalUsesEastUpNorthCoordinates()
    {
        var origin = new ThreeDOriginSnapshot(43.0, -79.0, 0, DateTimeOffset.UtcNow);
        var point = ThreeDSceneMath.ToLocal(43.001, -78.999, 25, origin);

        Assert.InRange(point.X, 80, 85);
        Assert.Equal(25, point.Y);
        Assert.InRange(point.Z, 110, 115);
    }

    [Fact]
    public void WorldCameraStartsOrbitingNorthWithOriginAsTarget()
    {
        var camera = ThreeDSceneMath.CreateWorldCamera(ThreeDVector3.Zero, 2, 0);

        Assert.True(camera.OrbitMode);
        Assert.Equal(ThreeDVector3.Zero, camera.OrbitTarget);
        Assert.Equal(0, camera.YawDegrees);
        Assert.True(camera.Position.Z < 0);
        Assert.True(camera.Position.Y > 0);
    }

    [Fact]
    public void WorldCameraTracksMapRotation()
    {
        var camera = ThreeDSceneMath.CreateWorldCamera(ThreeDVector3.Zero, 2, 90);

        Assert.Equal(90, camera.YawDegrees);
        Assert.True(camera.Position.X > 0);
    }
}
