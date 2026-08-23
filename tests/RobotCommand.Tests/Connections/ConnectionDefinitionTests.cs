using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests;

public sealed class ConnectionDefinitionTests
{
    [Fact]
    public void CreateDirect_ProducesStableReadableId()
    {
        var first = ConnectionDefinition.CreateDirect("Dracula Field", "http://localhost:50051");
        var second = ConnectionDefinition.CreateDirect("Dracula Field", "http://localhost:50051");

        Assert.Equal(first.Id, second.Id);
        Assert.StartsWith("dracula-field-", first.Id);
        Assert.Equal(ConnectionMode.Direct, first.Mode);
    }
}
