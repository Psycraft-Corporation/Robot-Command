using RobotCommand.Services.Missions;
using Xunit;

namespace RobotCommand.Tests;

public sealed class LogosCommandMetadataFactoryTests
{
    [Fact]
    public void Create_PreservesSuppliedCommandIdentity()
    {
        var factory = new LogosCommandMetadataFactory();

        var metadata = factory.Create(
            "mission.start",
            "mission-1",
            "request-1",
            "correlation-1",
            "idempotency-1");

        Assert.Equal("request-1", metadata.RequestId.RequestId_);
        Assert.Equal("correlation-1", metadata.CorrelationId.CorrelationId_);
        Assert.Equal("idempotency-1", metadata.IdempotencyKey);
        Assert.Equal("Robot Command", metadata.ClientName);
        Assert.NotNull(metadata.RequestedAt);
    }

    [Fact]
    public void Create_GeneratesDistinctIdentifiers_WhenNoneAreSupplied()
    {
        var factory = new LogosCommandMetadataFactory();

        var first = factory.Create("task.start", "task-1");
        var second = factory.Create("task.start", "task-1");

        Assert.NotEqual(first.RequestId.RequestId_, second.RequestId.RequestId_);
        Assert.NotEqual(first.CorrelationId.CorrelationId_, second.CorrelationId.CorrelationId_);
        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }
}
