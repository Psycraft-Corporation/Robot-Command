using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourWorkspaceServiceTests
{
    [Fact]
    public async Task Refresh_ReconcilesMatchingLocalAndRemotePackage()
    {
        var local = LocalPackage("search", "1.0.0", "abc123");
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource([
            RemotePackage("connection-1", "search", "1.0.0", "ABC123")
        ]);
        using var service = CreateService(store, remote);

        var snapshot = await service.RefreshAsync("connection-1");

        var entry = Assert.Single(snapshot.Entries);
        Assert.Same(local, entry.Local);
        Assert.NotNull(entry.Remote);
        Assert.Equal(BehaviourDeploymentStatus.Matching, entry.Deployment.Status);
        Assert.True(snapshot.RemoteInventory.Available);
        Assert.False(snapshot.RemoteInventory.Stale);
    }

    [Fact]
    public async Task Refresh_ReportsLocalPackageAsNotInstalled()
    {
        var store = new FakeStore(LocalPackage("search", "1.0.0", "abc123"));
        using var service = CreateService(store, new FakeRemoteSource([]));

        var snapshot = await service.RefreshAsync("connection-1");

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(BehaviourDeploymentStatus.NotInstalled, entry.Deployment.Status);
        Assert.True(entry.CanSubmitLocalPackage);
    }

    [Fact]
    public async Task Refresh_ReportsRemoteOnlyVersionsIndependently()
    {
        var store = new FakeStore();
        using var service = CreateService(store, new FakeRemoteSource([
            RemotePackage("connection-1", "search", "1.0.0", "abc123"),
            RemotePackage("connection-1", "search", "2.0.0", "def456")
        ]));

        var snapshot = await service.RefreshAsync("connection-1");

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.All(snapshot.Entries, item =>
            Assert.Equal(BehaviourDeploymentStatus.RemoteOnly, item.Deployment.Status));
    }

    [Fact]
    public async Task Refresh_UsesTargetCapabilitiesAndVehicleProfile()
    {
        var package = LocalPackage(
            "search",
            "1.0.0",
            "abc123",
            requiredCapabilities: ["perception.track"],
            compatibleProfiles: ["multicopter"]);
        var store = new FakeStore(package);
        using var service = CreateService(store, new FakeRemoteSource([]));
        var target = BehaviourCompatibilityTarget.Create(
            "vehicle-1",
            "Dracula",
            ["vehicle.flight-control"],
            "rover");

        var snapshot = await service.RefreshAsync("connection-1", target);

        var entry = Assert.Single(snapshot.Entries);
        Assert.NotNull(entry.Compatibility);
        Assert.False(entry.Compatibility!.Compatible);
        Assert.Equal(BehaviourDeploymentStatus.Incompatible, entry.Deployment.Status);
        Assert.Contains("perception.track", entry.Compatibility.MissingCapabilities);
    }

    [Fact]
    public async Task FailedRefresh_RetainsLastKnownRemoteInventoryAsStale()
    {
        var store = new FakeStore(LocalPackage("search", "1.0.0", "abc123"));
        var remote = new FakeRemoteSource([
            RemotePackage("connection-1", "search", "1.0.0", "abc123")
        ]);
        using var service = CreateService(store, remote);
        await service.RefreshAsync("connection-1");
        remote.Exception = new InvalidOperationException("runtime unavailable");

        var snapshot = await service.RefreshAsync("connection-1", refreshRemote: true);

        Assert.False(snapshot.RemoteInventory.Available);
        Assert.True(snapshot.RemoteInventory.Stale);
        Assert.Single(snapshot.RemoteInventory.Packages);
        Assert.Equal(BehaviourDeploymentStatus.Unavailable, Assert.Single(snapshot.Entries).Deployment.Status);
        Assert.Contains("runtime unavailable", snapshot.RemoteInventory.Summary);
    }

    [Fact]
    public async Task LocalStoreChange_RebuildsExistingSnapshots()
    {
        var store = new FakeStore(LocalPackage("search", "1.0.0", "abc123"));
        using var service = CreateService(store, new FakeRemoteSource([]));
        await service.RefreshAsync("connection-1");
        var changed = 0;
        service.Changed += (_, _) => changed++;

        store.SetPackages(LocalPackage("patrol", "1.0.0", "def456"));

        var snapshot = service.GetSnapshot("connection-1");
        Assert.Equal("patrol", Assert.Single(snapshot.Entries).Identity.BehaviourId);
        Assert.Equal(1, changed);
    }

    private static BehaviourWorkspaceService CreateService(
        IBehaviourPackageStore store,
        IBehaviourRemotePackageSource remote)
        => new(store, remote, NullLogger<BehaviourWorkspaceService>.Instance);

    private static LocalBehaviourPackageRecord LocalPackage(
        string behaviourId,
        string version,
        string hash,
        IReadOnlyList<string>? requiredCapabilities = null,
        IReadOnlyList<string>? compatibleProfiles = null)
    {
        var identity = new BehaviourPackageIdentity(behaviourId, version);
        var validation = new BehaviourPackageValidationResult(
            BehaviourPackageValidationAuthority.RobotCommandIntegrity,
            BehaviourPackageValidationState.Valid,
            "Valid",
            [],
            DateTimeOffset.UtcNow);
        return new LocalBehaviourPackageRecord(
            identity,
            new BehaviourPackageLayout("/library/package", "manifest.yaml", "tree.xml", null),
            new BehaviourPackageManifestSummary(
                1,
                behaviourId,
                behaviourId,
                string.Empty,
                version,
                "tree.xml",
                null),
            behaviourId,
            string.Empty,
            hash,
            BehaviourPackageLocalState.Imported,
            validation,
            BehaviourPackageValidationResult.NotValidated(BehaviourPackageValidationAuthority.Logos),
            DateTimeOffset.UtcNow,
            ImportedAt: DateTimeOffset.UtcNow,
            RequiredCapabilities: requiredCapabilities,
            CompatibleVehicleProfiles: compatibleProfiles);
    }

    private static RemoteBehaviourPackageRecord RemotePackage(
        string connectionId,
        string behaviourId,
        string version,
        string hash)
        => new(
            connectionId,
            new BehaviourPackageIdentity(behaviourId, version),
            behaviourId,
            string.Empty,
            "Stable",
            "Development",
            hash,
            [],
            [],
            []);

    private sealed class FakeRemoteSource(IReadOnlyList<RemoteBehaviourPackageRecord> packages)
        : IBehaviourRemotePackageSource
    {
        public Exception? Exception { get; set; }

        public Task<IReadOnlyList<RemoteBehaviourPackageRecord>> ListAsync(
            string connectionId,
            bool refresh = false,
            CancellationToken cancellationToken = default)
            => Exception is null
                ? Task.FromResult(packages)
                : Task.FromException<IReadOnlyList<RemoteBehaviourPackageRecord>>(Exception);
    }

    private sealed class FakeStore(params LocalBehaviourPackageRecord[] packages)
        : IBehaviourPackageStore
    {
        private LocalBehaviourPackageRecord[] _packages = packages;

        public event EventHandler? Changed;

        public string RootPath => "/library";

        public IReadOnlyList<LocalBehaviourPackageRecord> Packages => _packages;

        public IReadOnlyList<BehaviourPackageLibraryIssue> Issues => [];

        public bool TryGet(BehaviourPackageIdentity identity, out LocalBehaviourPackageRecord? package)
        {
            package = _packages.FirstOrDefault(item => item.Identity == identity);
            return package is not null;
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BehaviourPackageImportResult> ImportDirectoryAsync(
            string sourceDirectory,
            bool allowReplace = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ExportDirectoryAsync(
            BehaviourPackageIdentity identity,
            string destinationDirectory,
            bool allowReplace = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(
            BehaviourPackageIdentity identity,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetRemoteBaselineAsync(
            BehaviourPackageIdentity identity,
            string? remoteSha256,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetLogosValidationAsync(
            BehaviourPackageIdentity identity,
            BehaviourPackageValidationResult validation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void SetPackages(params LocalBehaviourPackageRecord[] values)
        {
            _packages = values;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
