using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourAssetRecordsTests
{
    [Fact]
    public void Identity_PreservesManifestBehaviourIdAndAllowsUnversionedFolder()
    {
        var identity = new BehaviourPackageIdentity(" test/arm_disarm ");

        Assert.Equal("test/arm_disarm", identity.BehaviourId);
        Assert.Null(identity.Version);
        Assert.Equal("test/arm_disarm", identity.Key);
    }

    [Fact]
    public void Validation_SeparatesLocalIntegrityFromAuthoritativeLogosValidation()
    {
        var integrity = new BehaviourPackageValidationResult(
            BehaviourPackageValidationAuthority.RobotCommandIntegrity,
            BehaviourPackageValidationState.Valid,
            "The package folder is readable.",
            []);
        var logos = BehaviourPackageValidationResult.NotValidated(
            BehaviourPackageValidationAuthority.Logos,
            "Logos has not inspected the manifest.");

        Assert.True(integrity.Accepted);
        Assert.False(logos.Accepted);
        Assert.Equal(BehaviourPackageValidationAuthority.Logos, logos.Authority);
    }

    [Fact]
    public void Compatibility_UsesOnlyPackageDeclaredCapabilitiesAndProfiles()
    {
        var result = BehaviourCompatibilityRules.Evaluate(
            ["vehicle.flight-control", "vehicle.position"],
            ["multicopter"],
            ["vehicle.position"],
            "rover");

        Assert.False(result.Compatible);
        Assert.Equal(["vehicle.flight-control"], result.MissingCapabilities);
        Assert.False(result.VehicleProfileMatched);
    }

    [Fact]
    public void Deployment_LocalOnlyPackageIsNotInstalled()
    {
        var local = Local("sha-local");

        var result = BehaviourDeploymentComparer.Compare(
            "connection-1",
            local,
            remote: null,
            runtimeAvailable: true,
            compatibility: null);

        Assert.Equal(BehaviourDeploymentStatus.NotInstalled, result.Status);
    }

    [Fact]
    public void Deployment_EqualHashesAreMatching()
    {
        var local = Local("same-sha");
        var remote = Remote("same-sha");

        var result = BehaviourDeploymentComparer.Compare(
            "connection-1",
            local,
            remote,
            runtimeAvailable: true);

        Assert.Equal(BehaviourDeploymentStatus.Matching, result.Status);
    }

    [Fact]
    public void Deployment_UsesConfirmedBaselineToDistinguishUpdateDriftAndConflict()
    {
        var localUpdate = BehaviourDeploymentComparer.Compare(
            "connection-1",
            Local("local-new", baseline: "baseline"),
            Remote("baseline"),
            runtimeAvailable: true);
        var remoteDrift = BehaviourDeploymentComparer.Compare(
            "connection-1",
            Local("baseline", baseline: "baseline"),
            Remote("remote-new"),
            runtimeAvailable: true);
        var conflict = BehaviourDeploymentComparer.Compare(
            "connection-1",
            Local("local-new", baseline: "baseline"),
            Remote("remote-new"),
            runtimeAvailable: true);

        Assert.Equal(BehaviourDeploymentStatus.LocalUpdateAvailable, localUpdate.Status);
        Assert.Equal(BehaviourDeploymentStatus.Drifted, remoteDrift.Status);
        Assert.Equal(BehaviourDeploymentStatus.Conflict, conflict.Status);
    }

    [Fact]
    public void Deployment_InUseTakesPrecedenceOverHashComparison()
    {
        var result = BehaviourDeploymentComparer.Compare(
            "connection-1",
            Local("local"),
            Remote("remote") with { InUse = true },
            runtimeAvailable: true);

        Assert.Equal(BehaviourDeploymentStatus.InUse, result.Status);
        Assert.True(result.InUse);
    }

    [Fact]
    public void ExistingRemoteOption_AdaptsWithoutChangingQuickRunContract()
    {
        var option = new BehaviourPackageOption(
            "test/arm_disarm",
            "1.0.0",
            "Arm / disarm",
            "Example package",
            "Ready",
            "Development",
            ["vehicle.flight-control"],
            [],
            []);

        var remote = option.ToRemoteRecord("connection-1", "abc123");

        Assert.Equal("test/arm_disarm@1.0.0", remote.Identity.Key);
        Assert.Equal("connection-1:test/arm_disarm@1.0.0", remote.Key);
        Assert.Equal("abc123", remote.ContentSha256);
        Assert.Equal(option.RequiredCapabilities, remote.RequiredCapabilities);
    }

    private static LocalBehaviourPackageRecord Local(string sha, string? baseline = null)
        => new(
            new BehaviourPackageIdentity("test/arm_disarm", "1.0.0"),
            new BehaviourPackageLayout(
                "/tmp/test-arm-disarm",
                "manifest.yaml",
                "tree.xml",
                "geometry.json"),
            new BehaviourPackageManifestSummary(
                1,
                "test/arm_disarm",
                "arm_disarm",
                string.Empty,
                "1.0.0",
                "tree.xml",
                "geometry.json"),
            "arm_disarm",
            string.Empty,
            sha,
            BehaviourPackageLocalState.Imported,
            new BehaviourPackageValidationResult(
                BehaviourPackageValidationAuthority.RobotCommandIntegrity,
                BehaviourPackageValidationState.Valid,
                "Readable",
                []),
            BehaviourPackageValidationResult.NotValidated(BehaviourPackageValidationAuthority.Logos),
            DateTimeOffset.UtcNow,
            RemoteBaselineSha256: baseline);

    private static RemoteBehaviourPackageRecord Remote(string sha)
        => new(
            "connection-1",
            new BehaviourPackageIdentity("test/arm_disarm", "1.0.0"),
            "arm_disarm",
            string.Empty,
            "Ready",
            "Development",
            sha,
            [],
            [],
            []);
}
