using RobotCommand.Models;
using RobotCommand.Services.Operations;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperationalInspectionTargetServiceTests
{
    [Fact]
    public void Publish_PreservesTargetAndOpenRequestUntilAcknowledged()
    {
        var service = new OperationalInspectionTargetService();
        var changed = 0;
        service.Changed += (_, _) => changed++;

        var request = service.Publish(
            Target(),
            OperationalInspectionSource.QuickRun,
            "Quick Run launched the target.",
            openInspector: true);

        Assert.Same(request, service.Current);
        Assert.True(service.OpenRequested);
        Assert.Equal(1, changed);

        service.AcknowledgeOpenRequest();

        Assert.False(service.OpenRequested);
        Assert.Same(request, service.Current);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Clear_RemovesCurrentTarget()
    {
        var service = new OperationalInspectionTargetService();
        service.Publish(Target(), OperationalInspectionSource.Manual, "Manual");

        service.Clear();

        Assert.Null(service.Current);
        Assert.False(service.OpenRequested);
    }

    private static OperationalExecutionTarget Target() => new(
        "connection-1",
        "vehicle-1",
        "Dracula",
        "logos-1",
        "mission-1",
        "task-1",
        "mission-execution-1",
        "task-execution-1",
        "correlation-1",
        DateTimeOffset.UtcNow);
}
