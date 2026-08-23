using RobotCommand.Models;
using RobotCommand.Services.ManualControl;
using Xunit;

namespace RobotCommand.Tests;

public sealed class ManualControlButtonMapperTests
{
    [Fact]
    public void MapsRequestedQueueButtonsToCommands()
    {
        Assert.Equal(OperatorCommandKind.Hold,
            ManualControlButtonMapper.QueuedCommandFor(true, false, false, false, true));
        Assert.Equal(OperatorCommandKind.Arm,
            ManualControlButtonMapper.QueuedCommandFor(false, true, false, false, true));
        Assert.Equal(OperatorCommandKind.Disarm,
            ManualControlButtonMapper.QueuedCommandFor(false, true, false, true, false));
        Assert.Equal(OperatorCommandKind.Takeoff,
            ManualControlButtonMapper.QueuedCommandFor(false, false, true, false, true));
        Assert.Equal(OperatorCommandKind.Land,
            ManualControlButtonMapper.QueuedCommandFor(false, false, true, true, false));
    }

    [Fact]
    public void NoQueueButtonProducesNoCommand()
    {
        Assert.Null(ManualControlButtonMapper.QueuedCommandFor(false, false, false, false, true));
    }
}
