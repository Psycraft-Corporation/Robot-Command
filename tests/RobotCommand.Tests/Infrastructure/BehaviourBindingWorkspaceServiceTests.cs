using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Missions;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourBindingWorkspaceServiceTests
{
    [Fact]
    public void SlotAssessment_DoesNotTreatUnverifiedDefaultGeometryAsReady()
    {
        var requirement = SearchAreaRequirement() with { DefaultGeometryId = "zone-default" };

        var assessment = BehaviourBindingSlotAssessment.Create(requirement, binding: null);

        Assert.Equal(BehaviourBindingSlotState.Unknown, assessment.State);
        Assert.False(assessment.Ready);
        Assert.Equal("zone-default", assessment.GeometryId);
    }

    [Fact]
    public async Task Inspect_ClassifiesRequiredSlotAndCompatibleCandidates()
    {
        var requirement = SearchAreaRequirement();
        var bindings = new FakeBindingService
        {
            Readiness = UnboundReadiness(requirement)
        };
        var geometry = new FakeGeometryGateway
        {
            Listed =
            [
                Remote("zone-good", GeometryDocumentKind.Zone),
                Remote("zone-wrong-policy", GeometryDocumentKind.Zone),
                Remote("route-one", GeometryDocumentKind.WaypointSequence)
            ],
            Objects =
            {
                ["zone-good"] = Geometry("zone-good", GeometryDocumentKind.Zone, "search_area"),
                ["zone-wrong-policy"] = Geometry("zone-wrong-policy", GeometryDocumentKind.Zone, "no_fly")
            }
        };
        var service = CreateService(requirement, bindings, geometry);

        var snapshot = await service.InspectAsync(
            ConnectionId,
            new BehaviourPackageIdentity(BehaviourId, Version));

        var slot = Assert.Single(snapshot.Slots);
        Assert.False(snapshot.Ready);
        Assert.Equal(BehaviourBindingSlotState.RequiredUnbound, slot.State);
        Assert.Equal("zone-good", Assert.Single(slot.CompatibleCandidates).GeometryId);
        Assert.Contains(slot.Candidates, item =>
            item.GeometryId == "zone-wrong-policy" && !item.Compatible);
        Assert.Contains(slot.Candidates, item =>
            item.GeometryId == "route-one" && !item.Compatible);
    }

    [Fact]
    public async Task Set_RejectsIncompatibleGeometryBeforeCallingLogosMutation()
    {
        var requirement = SearchAreaRequirement();
        var bindings = new FakeBindingService
        {
            Readiness = UnboundReadiness(requirement)
        };
        var geometry = new FakeGeometryGateway
        {
            Listed = [Remote("route-one", GeometryDocumentKind.WaypointSequence)]
        };
        var service = CreateService(requirement, bindings, geometry);

        var result = await service.SetAsync(Request("route-one"));

        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.Equal(0, bindings.SetCalls);
        Assert.Contains("expects Zone", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_VerifiesRefreshedBindingAndReturnsReadySnapshot()
    {
        var requirement = SearchAreaRequirement();
        var bindings = new FakeBindingService
        {
            Readiness = UnboundReadiness(requirement),
            OnSet = geometryId => BoundReadiness(requirement, geometryId)
        };
        var geometry = new FakeGeometryGateway
        {
            Listed = [Remote("zone-good", GeometryDocumentKind.Zone)],
            Objects =
            {
                ["zone-good"] = Geometry("zone-good", GeometryDocumentKind.Zone, "search_area")
            }
        };
        var service = CreateService(requirement, bindings, geometry);

        var result = await service.SetAsync(Request("zone-good"));

        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal(1, bindings.SetCalls);
        Assert.True(result.Snapshot!.Ready);
        Assert.Equal("zone-good", Assert.Single(result.Snapshot.Readiness.GeometryIds));
    }

    [Fact]
    public async Task Set_ReportsAcceptedButUnverifiedWhenLogosStateDoesNotChange()
    {
        var requirement = SearchAreaRequirement();
        var bindings = new FakeBindingService
        {
            Readiness = UnboundReadiness(requirement)
        };
        var geometry = new FakeGeometryGateway
        {
            Listed = [Remote("zone-good", GeometryDocumentKind.Zone)],
            Objects =
            {
                ["zone-good"] = Geometry("zone-good", GeometryDocumentKind.Zone, "search_area")
            }
        };
        var service = CreateService(requirement, bindings, geometry);

        var result = await service.SetAsync(Request("zone-good"));

        Assert.True(result.Accepted);
        Assert.False(result.Verified);
        Assert.False(result.Snapshot!.Ready);
        Assert.Contains("does not match", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clear_VerifiesThatExplicitBindingIsAbsent()
    {
        var requirement = SearchAreaRequirement();
        var bindings = new FakeBindingService
        {
            Readiness = BoundReadiness(requirement, "zone-good"),
            OnClear = () => UnboundReadiness(requirement)
        };
        var geometry = new FakeGeometryGateway
        {
            Listed = [Remote("zone-good", GeometryDocumentKind.Zone)],
            Objects =
            {
                ["zone-good"] = Geometry("zone-good", GeometryDocumentKind.Zone, "search_area")
            }
        };
        var service = CreateService(requirement, bindings, geometry);

        var result = await service.ClearAsync(Request());

        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal(1, bindings.ClearCalls);
        Assert.Equal(BehaviourBindingSlotState.RequiredUnbound, Assert.Single(result.Snapshot!.Slots).State);
    }

    [Fact]
    public async Task Inspect_BlocksStaleInstalledInventory()
    {
        var requirement = SearchAreaRequirement();
        var workspace = new FakeBehaviourWorkspace(
            Snapshot(requirement, available: true, stale: true, summary: "Installed inventory is stale."));
        var service = new BehaviourBindingWorkspaceService(
            workspace,
            new FakeBindingService { Readiness = UnboundReadiness(requirement) },
            new FakeGeometryGateway(),
            NullLogger<BehaviourBindingWorkspaceService>.Instance);

        var result = await service.InspectAsync(
            ConnectionId,
            new BehaviourPackageIdentity(BehaviourId, Version));

        Assert.False(result.Available);
        Assert.False(result.Ready);
        Assert.Contains("stale", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static BehaviourBindingWorkspaceService CreateService(
        BehaviourGeometryRequirement requirement,
        FakeBindingService bindings,
        FakeGeometryGateway geometry)
        => new(
            new FakeBehaviourWorkspace(Snapshot(requirement)),
            bindings,
            geometry,
            NullLogger<BehaviourBindingWorkspaceService>.Instance);

    private static BehaviourWorkspaceSnapshot Snapshot(
        BehaviourGeometryRequirement requirement,
        bool available = true,
        bool stale = false,
        string summary = "Installed inventory loaded.")
    {
        var identity = new BehaviourPackageIdentity(BehaviourId, Version);
        var remote = new RemoteBehaviourPackageRecord(
            ConnectionId,
            identity,
            "Search",
            "Search a registered area.",
            "Installed",
            "stable",
            "package-sha",
            [],
            [],
            [requirement],
            DateTimeOffset.UtcNow);
        var inventory = new BehaviourRemoteInventoryState(
            ConnectionId,
            available,
            stale,
            summary,
            [remote],
            DateTimeOffset.UtcNow);
        var deployment = new BehaviourDeploymentRecord(
            ConnectionId,
            identity,
            BehaviourDeploymentStatus.RemoteOnly,
            "Installed only on Logos.",
            null,
            remote.ContentSha256,
            null,
            DateTimeOffset.UtcNow);
        var entry = new BehaviourWorkspaceEntry(
            identity,
            remote.DisplayName,
            remote.Description,
            null,
            remote,
            deployment);
        return new BehaviourWorkspaceSnapshot(
            ConnectionId,
            inventory,
            [entry],
            null,
            DateTimeOffset.UtcNow);
    }

    private static BehaviourGeometryRequirement SearchAreaRequirement()
        => new(
            SlotId,
            "zone",
            RequiredRegistration: true,
            RequireObjectAtStart: true,
            AllowEmptyGeometry: false,
            ExpectedPolicyKind: "search_area",
            DefaultGeometryId: null,
            Description: "Area to search.");

    private static BehaviourGeometryReadiness UnboundReadiness(
        BehaviourGeometryRequirement requirement)
    {
        var binding = Binding(requirement, null, bound: false, objectExists: false);
        return new BehaviourGeometryReadiness(
            ConnectionId,
            BehaviourId,
            Version,
            [binding],
            [],
            [],
            [$"Required geometry slot '{requirement.SlotId}' is unbound."]);
    }

    private static BehaviourGeometryReadiness BoundReadiness(
        BehaviourGeometryRequirement requirement,
        string geometryId)
    {
        var binding = Binding(requirement, geometryId, bound: true, objectExists: true);
        return new BehaviourGeometryReadiness(
            ConnectionId,
            BehaviourId,
            Version,
            [binding],
            [geometryId],
            [],
            []);
    }

    private static BehaviourGeometryBindingRecord Binding(
        BehaviourGeometryRequirement requirement,
        string? geometryId,
        bool bound,
        bool objectExists)
        => new(
            ConnectionId,
            BehaviourId,
            Version,
            requirement.SlotId,
            geometryId,
            bound,
            objectExists,
            requirement.RequiredRegistration,
            requirement.RequireObjectAtStart,
            requirement.AllowEmptyGeometry,
            requirement.ExpectedPolicyKind,
            DateTimeOffset.UtcNow,
            []);

    private static BehaviourGeometryBindingCommandRequest Request(string? geometryId = null)
        => new(ConnectionId, BehaviourId, Version, SlotId, geometryId);

    private static RemoteGeometryRecord Remote(string id, GeometryDocumentKind kind)
        => new(
            ConnectionId,
            id,
            kind,
            id,
            GeometryCoordinateFrame.GlobalWgs84,
            kind == GeometryDocumentKind.Zone,
            kind == GeometryDocumentKind.PointOfInterest ? 1 : 2,
            kind == GeometryDocumentKind.Zone ? 1 : 0,
            100,
            $"sha-{id}",
            "revision-1",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

    private static RemoteGeometryObject Geometry(
        string id,
        GeometryDocumentKind kind,
        string policyKind)
    {
        var document = new GeometryDocument
        {
            GeometryId = id,
            DisplayName = id,
            Kind = kind,
            Frame = GeometryCoordinateFrame.GlobalWgs84,
            Policy = new GeometryPolicyAnnotation
            {
                Kind = policyKind,
                Constraint = policyKind
            },
            IsDirty = false
        };
        return new RemoteGeometryObject(document, Remote(id, kind));
    }

    private const string ConnectionId = "connection-1";
    private const string BehaviourId = "search";
    private const string Version = "1.0.0";
    private const string SlotId = "search-area";

    private sealed class FakeBehaviourWorkspace(BehaviourWorkspaceSnapshot snapshot)
        : IBehaviourWorkspaceService
    {
        public event EventHandler? Changed;

        public IReadOnlyList<LocalBehaviourPackageRecord> LocalPackages => [];

        public IReadOnlyList<BehaviourPackageLibraryIssue> LocalIssues => [];

        public BehaviourWorkspaceSnapshot Current { get; set; } = snapshot;

        public BehaviourWorkspaceSnapshot GetSnapshot(string connectionId) => Current;

        public Task<BehaviourWorkspaceSnapshot> RefreshAsync(
            string connectionId,
            BehaviourCompatibilityTarget? compatibilityTarget = null,
            bool refreshLocal = false,
            bool refreshRemote = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Current);
    }

    private sealed class FakeBindingService : IBehaviourGeometryBindingService
    {
        public bool IsAvailable => true;

        public string AvailabilityMessage => "Behaviour bindings available.";

        public required BehaviourGeometryReadiness Readiness { get; set; }

        public Func<string, BehaviourGeometryReadiness>? OnSet { get; init; }

        public Func<BehaviourGeometryReadiness>? OnClear { get; init; }

        public int SetCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public Task<IReadOnlyList<BehaviourGeometryBindingRecord>> ListAsync(
            string connectionId,
            string behaviourId,
            string version,
            bool checkObjectExists = true,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Readiness.Bindings);

        public Task<BehaviourGeometryReadiness> AssessAsync(
            string connectionId,
            BehaviourPackageOption package,
            bool refreshGeometry = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Readiness);

        public Task<BehaviourGeometryBindingCommandResult> SetAsync(
            BehaviourGeometryBindingCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            SetCalls++;
            if (OnSet is not null)
            {
                Readiness = OnSet(request.GeometryId!);
            }

            return Task.FromResult(new BehaviourGeometryBindingCommandResult(
                true,
                "Logos accepted the geometry binding.",
                Readiness.Bindings.FirstOrDefault(item => item.SlotId == request.SlotId)));
        }

        public Task<BehaviourGeometryBindingCommandResult> DeleteAsync(
            BehaviourGeometryBindingCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            if (OnClear is not null)
            {
                Readiness = OnClear();
            }

            return Task.FromResult(new BehaviourGeometryBindingCommandResult(
                true,
                "Logos accepted the geometry binding deletion."));
        }
    }

    private sealed class FakeGeometryGateway : IGeometryGateway
    {
        public bool IsAvailable => true;

        public string AvailabilityMessage => "Geometry available.";

        public IReadOnlyList<RemoteGeometryRecord> Listed { get; set; } = [];

        public Dictionary<string, RemoteGeometryObject> Objects { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<RemoteGeometryRecord>> ListAsync(
            string connectionId,
            GeometryQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Listed);

        public Task<RemoteGeometryObject?> GetAsync(
            string connectionId,
            string geometryId,
            bool refresh = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Objects.GetValueOrDefault(geometryId));

        public Task<GeometryValidationResult> ValidateAsync(
            string connectionId,
            GeometryDocument document,
            bool checkUpdateCompatibility = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryCommandResult> CreateAsync(
            GeometryCreateRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryCommandResult> UpdateAsync(
            GeometryUpdateRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryCommandResult> DeleteAsync(
            GeometryDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryRegistrySnapshot> GetRegistryStatusAsync(
            string connectionId,
            bool includeDetails = true,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<GeometryRegistryEvent> WatchAsync(
            string connectionId,
            GeometryQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
