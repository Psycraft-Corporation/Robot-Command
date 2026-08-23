using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourPackageDeploymentServiceTests
{
    [Fact]
    public async Task Install_ValidatesCreatesRefreshesAndStoresVerifiedBaseline()
    {
        var local = LocalPackage("search", "1.0.0", Hash('a'));
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource();
        var gateway = new FakeGateway(remote);
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var assessment = await service.AssessAsync(
            BehaviourPackageOperationKind.Install,
            "connection-1",
            local.Identity);
        var result = await service.InstallAsync(assessment.CreateRequest());

        Assert.True(assessment.Allowed);
        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal(BehaviourPackageCommandState.Accepted, result.State);
        Assert.Equal(1, gateway.ValidateCalls);
        Assert.Equal(1, gateway.CreateCalls);
        Assert.Equal(Hash('a'), Assert.Single(store.Packages).RemoteBaselineSha256);
        Assert.Equal(BehaviourPackageValidationState.Valid, Assert.Single(store.Packages).LogosValidation.State);
    }

    [Fact]
    public async Task Install_ReturnsAcceptedUnverifiedWhenRuntimeDoesNotExposePackageHash()
    {
        var local = LocalPackage("search", "1.0.0", Hash('a'));
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource();
        var gateway = new FakeGateway(remote) { ExposeHash = false };
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.InstallAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity));

        Assert.True(result.Accepted);
        Assert.False(result.Verified);
        Assert.Equal(BehaviourPackageCommandState.AcceptedUnverified, result.State);
        Assert.Null(Assert.Single(store.Packages).RemoteBaselineSha256);
        Assert.Contains(result.Warnings, item => item.Contains("package-wide SHA-256", StringComparison.Ordinal));
    }



    [Fact]
    public async Task Install_ReturnsAcceptedUnverifiedWhenPostCommandInventoryIsUnavailable()
    {
        var local = LocalPackage("search", "1.0.0", Hash('a'));
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource();
        var gateway = new FakeGateway(remote) { FailReadsAfterMutation = true };
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.InstallAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity));

        Assert.True(result.Accepted);
        Assert.False(result.Verified);
        Assert.Equal(BehaviourPackageCommandState.AcceptedUnverified, result.State);
        Assert.Null(Assert.Single(store.Packages).RemoteBaselineSha256);
        Assert.Contains("inventory was unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_AllowsGatewayAttemptWhenInstalledInventoryIsUnavailable()
    {
        var local = LocalPackage("search", "1.0.0", Hash('a'));
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource { FailReads = true };
        var gateway = new FakeGateway(remote);
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.ValidateAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity));

        Assert.True(result.Accepted);
        Assert.Equal(1, gateway.ValidateCalls);
        Assert.Contains(result.Warnings, item => item.Contains("inventory unavailable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Install_FailsWhenPostCommandHashDoesNotMatchLocalPackage()
    {
        var local = LocalPackage("search", "1.0.0", Hash('a'));
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource();
        var gateway = new FakeGateway(remote) { InstalledHashOverride = Hash('b') };
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.InstallAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity));

        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.Equal(BehaviourPackageCommandState.Failed, result.State);
        Assert.Equal(Hash('b'), result.ObservedRemoteSha256);
        Assert.Null(Assert.Single(store.Packages).RemoteBaselineSha256);
        Assert.Contains("hash does not match", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_BlocksActiveInstalledPackageBeforeCallingGateway()
    {
        var local = LocalPackage("search", "1.0.0", Hash('b'));
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource(RemotePackage(
            "search",
            "1.0.0",
            Hash('a'),
            active: true));
        var gateway = new FakeGateway(remote);
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.UpdateAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity));

        Assert.False(result.Accepted);
        Assert.Equal(BehaviourPackageCommandState.Rejected, result.State);
        Assert.Contains("active", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, gateway.ValidateCalls);
        Assert.Equal(0, gateway.UpdateCalls);
    }

    [Fact]
    public async Task Update_RejectsFrozenRemoteHashWhenInstalledPackageChanged()
    {
        var local = LocalPackage("search", "1.0.0", Hash('b'));
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource(RemotePackage("search", "1.0.0", Hash('c')));
        var gateway = new FakeGateway(remote);
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.UpdateAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity,
            ExpectedLocalSha256: Hash('b'),
            ExpectedRemoteSha256: Hash('a')));

        Assert.False(result.Accepted);
        Assert.Equal(BehaviourPackageCommandState.Conflict, result.State);
        Assert.Contains("changed after", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, gateway.ValidateCalls);
        Assert.Equal(0, gateway.UpdateCalls);
    }

    [Fact]
    public async Task Remove_BlocksAmbiguousMultipleInstalledVersions()
    {
        var store = new FakeStore();
        var remote = new FakeRemoteSource(
            RemotePackage("search", "1.0.0", Hash('a')),
            RemotePackage("search", "2.0.0", Hash('b')));
        var gateway = new FakeGateway(remote);
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.RemoveAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            new BehaviourPackageIdentity("search", "1.0.0")));

        Assert.False(result.Accepted);
        Assert.Contains("ambiguous", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, gateway.DeleteCalls);
    }

    [Fact]
    public async Task Remove_DeletesByBehaviourIdAndVerifiesInventory()
    {
        var local = LocalPackage("search", "1.0.0", Hash('a')) with
        {
            RemoteBaselineSha256 = Hash('a')
        };
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource(RemotePackage("search", "1.0.0", Hash('a')));
        var gateway = new FakeGateway(remote);
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.RemoveAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity));

        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal(1, gateway.DeleteCalls);
        Assert.Empty(remote.Packages);
        Assert.Null(Assert.Single(store.Packages).RemoteBaselineSha256);
        Assert.Contains(result.Warnings, item => item.Contains("behaviour ID", StringComparison.Ordinal));
    }


    [Fact]
    public async Task Remove_FailsWhenPackageRemainsInRefreshedInventory()
    {
        var local = LocalPackage("search", "1.0.0", Hash('a')) with
        {
            RemoteBaselineSha256 = Hash('a')
        };
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource(RemotePackage("search", "1.0.0", Hash('a')));
        var gateway = new FakeGateway(remote) { RetainOnDelete = true };
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.RemoveAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity));

        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.Equal(BehaviourPackageCommandState.Failed, result.State);
        Assert.Single(remote.Packages);
        Assert.Equal(Hash('a'), Assert.Single(store.Packages).RemoteBaselineSha256);
        Assert.Contains("remains installed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_PersistsLogosAuthoritativeResultWithoutMutatingRemoteInventory()
    {
        var local = LocalPackage("search", "1.0.0", Hash('a'));
        var store = new FakeStore(local);
        var remote = new FakeRemoteSource();
        var gateway = new FakeGateway(remote);
        using var workspace = new BehaviourWorkspaceService(
            store,
            remote,
            NullLogger<BehaviourWorkspaceService>.Instance);
        using var service = CreateService(store, workspace, gateway);

        var result = await service.ValidateAsync(new BehaviourPackageOperationRequest(
            "connection-1",
            local.Identity));

        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal(1, gateway.ValidateCalls);
        Assert.Equal(0, gateway.CreateCalls);
        Assert.Empty(remote.Packages);
        Assert.Equal(BehaviourPackageValidationAuthority.Logos, Assert.Single(store.Packages).LogosValidation.Authority);
    }

    private static BehaviourPackageDeploymentService CreateService(
        IBehaviourPackageStore store,
        IBehaviourWorkspaceService workspace,
        IBehaviourPackageGateway gateway)
        => new(
            store,
            workspace,
            gateway,
            NullLogger<BehaviourPackageDeploymentService>.Instance);

    private static LocalBehaviourPackageRecord LocalPackage(
        string behaviourId,
        string version,
        string hash)
    {
        var identity = new BehaviourPackageIdentity(behaviourId, version);
        var integrity = new BehaviourPackageValidationResult(
            BehaviourPackageValidationAuthority.RobotCommandIntegrity,
            BehaviourPackageValidationState.Valid,
            "Valid",
            [],
            DateTimeOffset.UtcNow);
        return new LocalBehaviourPackageRecord(
            identity,
            new BehaviourPackageLayout(
                $"/library/{behaviourId}",
                "manifest.yaml",
                "tree.xml",
                "geometry.json"),
            new BehaviourPackageManifestSummary(
                1,
                behaviourId,
                behaviourId,
                string.Empty,
                version,
                "tree.xml",
                "geometry.json"),
            behaviourId,
            string.Empty,
            hash,
            BehaviourPackageLocalState.Imported,
            integrity,
            BehaviourPackageValidationResult.NotValidated(BehaviourPackageValidationAuthority.Logos),
            DateTimeOffset.UtcNow,
            ImportedAt: DateTimeOffset.UtcNow);
    }

    private static RemoteBehaviourPackageRecord RemotePackage(
        string behaviourId,
        string version,
        string? hash,
        bool active = false,
        bool inUse = false)
        => new(
            "connection-1",
            new BehaviourPackageIdentity(behaviourId, version),
            behaviourId,
            string.Empty,
            "Stable",
            "Development",
            hash,
            [],
            [],
            [],
            Active: active,
            InUse: inUse);

    private static string Hash(char value) => new string(value, 64);

    private sealed class FakeGateway(FakeRemoteSource remote) : IBehaviourPackageGateway
    {
        public int ValidateCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public bool ExposeHash { get; init; } = true;
        public string? InstalledHashOverride { get; init; }
        public bool RetainOnDelete { get; init; }
        public bool FailReadsAfterMutation { get; init; }

        public Task<BehaviourPackageValidationResult> ValidateAsync(
            BehaviourPackageGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateCalls++;
            return Task.FromResult(new BehaviourPackageValidationResult(
                BehaviourPackageValidationAuthority.Logos,
                BehaviourPackageValidationState.Valid,
                "Logos accepted the package.",
                [],
                DateTimeOffset.UtcNow));
        }

        public Task<BehaviourPackageGatewayMutationResult> CreateAsync(
            BehaviourPackageGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            remote.SetPackages(RemotePackage(
                request.Package.Identity.BehaviourId,
                request.Package.Identity.Version ?? "v1",
                ExposeHash ? InstalledHashOverride ?? request.Package.ContentSha256 : null));
            remote.FailReads = FailReadsAfterMutation;
            return Task.FromResult(Accepted("Installed"));
        }

        public Task<BehaviourPackageGatewayMutationResult> UpdateAsync(
            BehaviourPackageGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            remote.SetPackages(RemotePackage(
                request.Package.Identity.BehaviourId,
                request.Package.Identity.Version ?? "v1",
                ExposeHash ? InstalledHashOverride ?? request.Package.ContentSha256 : null));
            remote.FailReads = FailReadsAfterMutation;
            return Task.FromResult(Accepted("Updated"));
        }

        public Task<BehaviourPackageGatewayMutationResult> DeleteAsync(
            BehaviourPackageDeleteGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (!RetainOnDelete)
            {
                remote.SetPackages(remote.Packages
                    .Where(item => !string.Equals(
                        item.Identity.BehaviourId,
                        request.Identity.BehaviourId,
                        StringComparison.Ordinal))
                    .ToArray());
            }
            return Task.FromResult(Accepted("Removed"));
        }

        private static BehaviourPackageGatewayMutationResult Accepted(string message)
            => new(true, BehaviourPackageCommandState.Accepted, message);
    }

    private sealed class FakeRemoteSource(params RemoteBehaviourPackageRecord[] packages)
        : IBehaviourRemotePackageSource
    {
        private RemoteBehaviourPackageRecord[] _packages = packages;

        public IReadOnlyList<RemoteBehaviourPackageRecord> Packages => _packages;

        public bool FailReads { get; set; }

        public Task<IReadOnlyList<RemoteBehaviourPackageRecord>> ListAsync(
            string connectionId,
            bool refresh = false,
            CancellationToken cancellationToken = default)
            => FailReads
                ? Task.FromException<IReadOnlyList<RemoteBehaviourPackageRecord>>(
                    new InvalidOperationException("Remote inventory unavailable."))
                : Task.FromResult<IReadOnlyList<RemoteBehaviourPackageRecord>>(_packages);

        public void SetPackages(params RemoteBehaviourPackageRecord[] values)
            => _packages = values;
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
        {
            Replace(identity, package => package with
            {
                RemoteBaselineSha256 = string.IsNullOrWhiteSpace(remoteSha256)
                    ? null
                    : remoteSha256.Trim().ToLowerInvariant()
            });
            return Task.CompletedTask;
        }

        public Task SetLogosValidationAsync(
            BehaviourPackageIdentity identity,
            BehaviourPackageValidationResult validation,
            CancellationToken cancellationToken = default)
        {
            Replace(identity, package => package with { LogosValidation = validation });
            return Task.CompletedTask;
        }

        private void Replace(
            BehaviourPackageIdentity identity,
            Func<LocalBehaviourPackageRecord, LocalBehaviourPackageRecord> update)
        {
            _packages = _packages
                .Select(item => item.Identity == identity ? update(item) : item)
                .ToArray();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
