using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.State;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourLibraryViewModelTests
{
    [Fact]
    public async Task PreparedInstall_RequiresExactTypedConfirmation()
    {
        var identity = new BehaviourPackageIdentity("test/search", "1.0.0");
        var local = Local(identity);
        var workspace = new FakeWorkspace(Snapshot(local));
        var deployment = new FakeDeployment
        {
            Assessment = new BehaviourPackageOperationAssessment(
                BehaviourPackageOperationKind.Install,
                "connection-1",
                identity,
                true,
                "Ready to install.",
                [],
                [],
                local,
                null,
                DateTimeOffset.UtcNow)
        };
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        connections.Upsert(new ConnectionRecord(
            "connection-1",
            "Runtime",
            "https://logos",
            ConnectionMode.Direct,
            AvailabilityState.Online,
            true));
        var viewModel = new BehaviourLibraryViewModel(
            workspace,
            new FakeStore(local),
            deployment,
            new FakeBindings(),
            connections,
            new ImmediateDispatcher());

        Assert.True(viewModel.PrepareInstallCommand.CanExecute(null));
        viewModel.PrepareInstallCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.PreparedOperation is not null);

        Assert.Equal("INSTALL test/search@1.0.0", viewModel.RequiredOperationConfirmation);
        viewModel.OperationConfirmation = "install test/search@1.0.0";
        Assert.False(viewModel.ExecutePreparedOperationCommand.CanExecute(null));
        viewModel.OperationConfirmation = "INSTALL test/search@1.0.0";
        Assert.True(viewModel.ExecutePreparedOperationCommand.CanExecute(null));
    }

    [Fact]
    public void LocalRemoval_UsesSeparateLocalOnlyConfirmation()
    {
        var identity = new BehaviourPackageIdentity("test/search", "1.0.0");
        var local = Local(identity);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        connections.Upsert(new ConnectionRecord(
            "connection-1",
            "Runtime",
            "https://logos",
            ConnectionMode.Direct,
            AvailabilityState.Online,
            true));
        var viewModel = new BehaviourLibraryViewModel(
            new FakeWorkspace(Snapshot(local)),
            new FakeStore(local),
            new FakeDeployment(),
            new FakeBindings(),
            connections,
            new ImmediateDispatcher());

        Assert.Equal("REMOVE LOCAL test/search@1.0.0", viewModel.RequiredLocalRemoveConfirmation);
        viewModel.LocalRemoveConfirmation = "REMOVE test/search@1.0.0";
        Assert.False(viewModel.RemoveLocalCommand.CanExecute(null));
        viewModel.LocalRemoveConfirmation = "REMOVE LOCAL test/search@1.0.0";
        Assert.True(viewModel.RemoveLocalCommand.CanExecute(null));
    }

    private static BehaviourWorkspaceSnapshot Snapshot(LocalBehaviourPackageRecord local)
    {
        var remote = new BehaviourRemoteInventoryState(
            "connection-1",
            true,
            false,
            "Installed inventory loaded.",
            []);
        var deployment = BehaviourDeploymentComparer.Compare(
            "connection-1",
            local,
            null,
            runtimeAvailable: true);
        return new BehaviourWorkspaceSnapshot(
            "connection-1",
            remote,
            [new BehaviourWorkspaceEntry(
                local.Identity,
                local.DisplayName,
                local.Description,
                local,
                null,
                deployment)],
            null,
            DateTimeOffset.UtcNow);
    }

    private static LocalBehaviourPackageRecord Local(BehaviourPackageIdentity identity)
    {
        var integrity = new BehaviourPackageValidationResult(
            BehaviourPackageValidationAuthority.RobotCommandIntegrity,
            BehaviourPackageValidationState.Valid,
            "Valid",
            []);
        return new LocalBehaviourPackageRecord(
            identity,
            new BehaviourPackageLayout("/tmp/search", "manifest.yaml", "tree.xml", null),
            new BehaviourPackageManifestSummary(
                1,
                identity.BehaviourId,
                "Search",
                "Search an area",
                identity.Version,
                "tree.xml",
                null),
            "Search",
            "Search an area",
            new string('a', 64),
            BehaviourPackageLocalState.Imported,
            integrity,
            BehaviourPackageValidationResult.NotValidated(BehaviourPackageValidationAuthority.Logos),
            DateTimeOffset.UtcNow);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The view-model command did not complete.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkspace(BehaviourWorkspaceSnapshot snapshot) : IBehaviourWorkspaceService
    {
        public event EventHandler? Changed;

        public IReadOnlyList<LocalBehaviourPackageRecord> LocalPackages => snapshot.LocalPackages;

        public IReadOnlyList<BehaviourPackageLibraryIssue> LocalIssues => [];

        public BehaviourWorkspaceSnapshot GetSnapshot(string connectionId) => snapshot;

        public Task<BehaviourWorkspaceSnapshot> RefreshAsync(
            string connectionId,
            BehaviourCompatibilityTarget? compatibilityTarget = null,
            bool refreshLocal = false,
            bool refreshRemote = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeStore(LocalBehaviourPackageRecord local) : IBehaviourPackageStore
    {
        public event EventHandler? Changed;

        public string RootPath => "/tmp/behaviours";

        public IReadOnlyList<LocalBehaviourPackageRecord> Packages => [local];

        public IReadOnlyList<BehaviourPackageLibraryIssue> Issues => [];

        public bool TryGet(BehaviourPackageIdentity identity, out LocalBehaviourPackageRecord? package)
        {
            package = identity == local.Identity ? local : null;
            return package is not null;
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
            => Task.CompletedTask;

        public Task SetRemoteBaselineAsync(
            BehaviourPackageIdentity identity,
            string? remoteSha256,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetLogosValidationAsync(
            BehaviourPackageIdentity identity,
            BehaviourPackageValidationResult validation,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeDeployment : IBehaviourPackageDeploymentService
    {
        public event EventHandler? Changed;

        public BehaviourPackageOperationAssessment? Assessment { get; init; }

        public BehaviourPackageCommandResult? LastResult { get; private set; }

        public Task<BehaviourPackageOperationAssessment> AssessAsync(
            BehaviourPackageOperationKind operation,
            string connectionId,
            BehaviourPackageIdentity identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Assessment ?? new BehaviourPackageOperationAssessment(
                operation,
                connectionId,
                identity,
                false,
                "Blocked",
                [],
                ["No assessment configured."],
                null,
                null,
                DateTimeOffset.UtcNow));

        public Task<BehaviourPackageCommandResult> ValidateAsync(
            BehaviourPackageOperationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BehaviourPackageCommandResult> InstallAsync(
            BehaviourPackageOperationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BehaviourPackageCommandResult> UpdateAsync(
            BehaviourPackageOperationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BehaviourPackageCommandResult> RemoveAsync(
            BehaviourPackageOperationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeBindings : IBehaviourBindingWorkspaceService
    {
        public event EventHandler? Changed;

        public bool IsAvailable => true;

        public string AvailabilityMessage => "Available";

        public BehaviourBindingWorkspaceSnapshot? GetSnapshot(
            string connectionId,
            BehaviourPackageIdentity identity)
            => null;

        public Task<IReadOnlyList<BehaviourBindingPackageOption>> ListPackagesAsync(
            string connectionId,
            bool refreshPackages = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BehaviourBindingPackageOption>>([]);

        public Task<BehaviourBindingWorkspaceSnapshot> InspectAsync(
            string connectionId,
            BehaviourPackageIdentity identity,
            bool refreshPackages = false,
            bool refreshGeometry = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BehaviourBindingMutationResult> SetAsync(
            BehaviourGeometryBindingCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BehaviourBindingMutationResult> ClearAsync(
            BehaviourGeometryBindingCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
