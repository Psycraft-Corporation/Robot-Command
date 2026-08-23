using RobotCommand.Models;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourLibraryPresentationTests
{
    [Fact]
    public void LibraryItem_ProjectsLocalAndInstalledStateWithoutParsingTreeSemantics()
    {
        var identity = new BehaviourPackageIdentity("test/search", "1.0.0");
        var local = Local(identity, "aaaaaaaaaaaaaaaa");
        var remote = new RemoteBehaviourPackageRecord(
            "connection-1",
            identity,
            "Search",
            "Search an area",
            "Ready",
            "stable",
            "aaaaaaaaaaaaaaaa",
            ["perception.tracks"],
            ["autonomy.search"],
            [Requirement()],
            Active: true,
            InUse: false);
        var deployment = new BehaviourDeploymentRecord(
            "connection-1",
            identity,
            BehaviourDeploymentStatus.Active,
            "The installed package matches the local package and is active.",
            local.ContentSha256,
            remote.ContentSha256,
            local.RemoteBaselineSha256,
            DateTimeOffset.UtcNow,
            Active: true);
        var item = new BehaviourLibraryItemViewModel(new BehaviourWorkspaceEntry(
            identity,
            "Search",
            "Search an area",
            local,
            remote,
            deployment));

        Assert.Equal("Local + Logos", item.SourceSummary);
        Assert.Equal("Active", item.DeploymentState);
        Assert.Contains("Requires: perception.tracks", item.CapabilitySummary, StringComparison.Ordinal);
        Assert.Contains("1 geometry slot", item.GeometrySummary, StringComparison.Ordinal);
        Assert.Contains("aaaaaaaaaaaa", item.HashSummary, StringComparison.Ordinal);
        Assert.True(item.Active);
    }

    [Fact]
    public void BindingSlot_ExposesOnlyCompatibleCandidatesForMutation()
    {
        var requirement = Requirement();
        var binding = new BehaviourGeometryBindingRecord(
            "connection-1",
            "test/search",
            "1.0.0",
            requirement.SlotId,
            "zone-alpha",
            true,
            true,
            true,
            true,
            false,
            "search_area",
            DateTimeOffset.UtcNow,
            []);
        var compatible = new BehaviourGeometryCandidate(
            "zone-alpha",
            "Zone Alpha",
            GeometryDocumentKind.Zone,
            GeometryCoordinateFrame.GlobalWgs84,
            "search_area",
            "include",
            true,
            "Compatible",
            []);
        var incompatible = compatible with
        {
            GeometryId = "route-alpha",
            DisplayName = "Route Alpha",
            Kind = GeometryDocumentKind.WaypointSequence,
            Compatible = false,
            Summary = "Wrong geometry kind"
        };
        var assessment = BehaviourBindingSlotAssessment.Create(
            requirement,
            binding,
            [compatible, incompatible]);
        var item = new BehaviourBindingSlotItemViewModel(assessment);

        Assert.True(item.Ready);
        Assert.Equal("zone-alpha", item.GeometryId);
        Assert.Equal("Zone", item.ExpectedKind);
        Assert.Equal("zone-alpha", Assert.Single(item.CompatibleCandidates).GeometryId);
    }

    private static LocalBehaviourPackageRecord Local(
        BehaviourPackageIdentity identity,
        string sha256)
    {
        var accepted = new BehaviourPackageValidationResult(
            BehaviourPackageValidationAuthority.RobotCommandIntegrity,
            BehaviourPackageValidationState.Valid,
            "Package structure is valid.",
            []);
        return new LocalBehaviourPackageRecord(
            identity,
            new BehaviourPackageLayout(
                "/tmp/search",
                "/tmp/search/manifest.yaml",
                "/tmp/search/tree.xml",
                "/tmp/search/geometry.json"),
            new BehaviourPackageManifestSummary(
                1,
                identity.BehaviourId,
                "Search",
                "Search an area",
                identity.Version,
                "tree.xml",
                "geometry.json"),
            "Search",
            "Search an area",
            sha256,
            BehaviourPackageLocalState.Imported,
            accepted,
            BehaviourPackageValidationResult.NotValidated(BehaviourPackageValidationAuthority.Logos),
            DateTimeOffset.UtcNow,
            RemoteBaselineSha256: sha256,
            RequiredCapabilities: ["perception.tracks"],
            ProvidedCapabilities: ["autonomy.search"],
            GeometrySlots: [Requirement()]);
    }

    private static BehaviourGeometryRequirement Requirement()
        => new(
            "search-area",
            "zone",
            RequiredRegistration: true,
            RequireObjectAtStart: true,
            AllowEmptyGeometry: false,
            ExpectedPolicyKind: "search_area",
            DefaultGeometryId: null,
            Description: "Search area");
}
