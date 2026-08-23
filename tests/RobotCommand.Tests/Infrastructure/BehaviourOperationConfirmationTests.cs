using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourOperationConfirmationTests
{
    [Fact]
    public void RemoteConfirmation_UsesOperationAndExactIdentity()
    {
        var assessment = new BehaviourPackageOperationAssessment(
            BehaviourPackageOperationKind.Update,
            "connection-1",
            new BehaviourPackageIdentity("test/search", "1.2.3"),
            true,
            "Ready",
            [],
            [],
            null,
            null,
            DateTimeOffset.UtcNow);

        var required = BehaviourOperationConfirmation.ForRemote(assessment);

        Assert.Equal("UPDATE test/search@1.2.3", required);
        Assert.True(BehaviourOperationConfirmation.Matches("  UPDATE test/search@1.2.3  ", required));
        Assert.False(BehaviourOperationConfirmation.Matches("update test/search@1.2.3", required));
        Assert.False(BehaviourOperationConfirmation.Matches("UPDATE test/search", required));
    }

    [Fact]
    public void LocalRemoval_IsDistinctFromRemoteRemoval()
    {
        var identity = new BehaviourPackageIdentity("test/arm_disarm", "1.0.0");

        Assert.Equal(
            "REMOVE LOCAL test/arm_disarm@1.0.0",
            BehaviourOperationConfirmation.ForLocalRemoval(identity));
    }
}
