using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests;

public sealed class ManualControlSessionStateTests
{
    [Fact]
    public void AcquisitionAndReleaseTransitionsAreNotReportedAsActiveControl()
    {
        var acquiring = ManualControlSessionSnapshot.Empty with
        {
            State = ManualControlSessionState.Acquiring,
            Status = "Taking manual control..."
        };
        var releasing = ManualControlSessionSnapshot.Empty with
        {
            State = ManualControlSessionState.Releasing,
            Status = "Releasing manual control..."
        };

        Assert.False(acquiring.IsActive);
        Assert.False(releasing.IsActive);
        Assert.True((ManualControlSessionSnapshot.Empty with { State = ManualControlSessionState.Hold }).IsActive);
    }
}
