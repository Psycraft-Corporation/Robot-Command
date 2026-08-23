using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperationalInspectorViewModelSurfaceTests
{
    [Fact]
    public void Surface_OwnsSupervisionAndInterventions()
    {
        var type = typeof(OperationalInspectorViewModel);

        Assert.NotNull(type.GetProperty("Snapshot"));
        Assert.NotNull(type.GetProperty("TreeNodes"));
        Assert.NotNull(type.GetProperty("RuntimeIssues"));
        Assert.NotNull(type.GetProperty("InspectSelectionCommand"));
        Assert.NotNull(type.GetProperty("StopWatchingCommand"));
        Assert.NotNull(type.GetProperty("PreparePauseMissionCommand"));
        Assert.NotNull(type.GetProperty("PrepareAbortMissionCommand"));
        Assert.NotNull(type.GetProperty("PrepareCancelTaskCommand"));
        Assert.NotNull(type.GetProperty("ExecuteInterventionCommand"));

        Assert.Null(type.GetProperty("PrepareArmVehicleCommand"));
        Assert.Null(type.GetProperty("ExecuteVehicleOperationCommand"));
    }
}
