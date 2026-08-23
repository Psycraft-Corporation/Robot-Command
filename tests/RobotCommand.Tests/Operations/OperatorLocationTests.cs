using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperatorLocationTests
{
    [Fact]
    public void AvailableAt_ReportsValidLocationAndAccuracy()
    {
        var updated = DateTimeOffset.UtcNow;
        var snapshot = OperatorLocationSnapshot.AvailableAt(-79.42, 43.73, 12.5, updated);

        Assert.True(snapshot.IsAvailable);
        Assert.Equal(OperatorLocationState.Available, snapshot.State);
        Assert.Equal(-79.42, snapshot.LongitudeDegrees);
        Assert.Equal(43.73, snapshot.LatitudeDegrees);
        Assert.Equal(12.5, snapshot.AccuracyMeters);
        Assert.Equal(updated, snapshot.LastUpdated);
    }

    [Fact]
    public void Unavailable_DoesNotExposeCoordinates()
    {
        var snapshot = OperatorLocationSnapshot.Unavailable("Permission denied.");

        Assert.False(snapshot.IsAvailable);
        Assert.Equal(OperatorLocationState.Unavailable, snapshot.State);
        Assert.Null(snapshot.LongitudeDegrees);
        Assert.Null(snapshot.LatitudeDegrees);
        Assert.Equal("Permission denied.", snapshot.Status);
    }

    [Fact]
    public void OperationalMapScene_DefaultsToUnavailableOperatorLocation()
    {
        Assert.False(OperationalMapScene.Empty.OperatorLocation.IsAvailable);
        Assert.Equal(OperatorLocationState.Unavailable, OperationalMapScene.Empty.OperatorLocation.State);
    }
}
