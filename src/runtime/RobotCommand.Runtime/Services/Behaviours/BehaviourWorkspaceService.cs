using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Shared read model for local behaviour assets, installed Logos packages,
/// package drift, and optional vehicle compatibility. Quick Run and the later
/// Behaviour workspace should consume this service instead of maintaining
/// separate package caches.
/// </summary>
public sealed class BehaviourWorkspaceService : IBehaviourWorkspaceService, IDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IBehaviourPackageStore _local;
    private readonly IBehaviourRemotePackageSource _remote;
    private readonly ILogger<BehaviourWorkspaceService> _logger;
    private readonly Dictionary<string, BehaviourWorkspaceSnapshot> _snapshots = new(StringComparer.Ordinal);
    private int _disposed;

    public BehaviourWorkspaceService(
        IBehaviourPackageStore local,
        IBehaviourRemotePackageSource remote,
        ILogger<BehaviourWorkspaceService> logger)
    {
        _local = local;
        _remote = remote;
        _logger = logger;
        _local.Changed += OnLocalPackagesChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<LocalBehaviourPackageRecord> LocalPackages => _local.Packages;

    public IReadOnlyList<BehaviourPackageLibraryIssue> LocalIssues => _local.Issues;

    public BehaviourWorkspaceSnapshot GetSnapshot(string connectionId)
    {
        var normalized = NormalizeConnectionId(connectionId);
        lock (_stateGate)
        {
            return _snapshots.TryGetValue(normalized, out var snapshot)
                ? snapshot
                : BehaviourWorkspaceSnapshot.Empty(normalized, _local.Packages);
        }
    }

    public async Task<BehaviourWorkspaceSnapshot> RefreshAsync(
        string connectionId,
        BehaviourCompatibilityTarget? compatibilityTarget = null,
        bool refreshLocal = false,
        bool refreshRemote = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalized = NormalizeConnectionId(connectionId);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (refreshLocal)
            {
                await _local.RefreshAsync(cancellationToken);
            }

            var previous = GetSnapshot(normalized);
            BehaviourRemoteInventoryState inventory;
            try
            {
                var packages = await _remote.ListAsync(
                    normalized,
                    refreshRemote,
                    cancellationToken);
                inventory = new BehaviourRemoteInventoryState(
                    normalized,
                    true,
                    false,
                    $"Loaded {packages.Count} installed behaviour package(s).",
                    packages,
                    DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not refresh installed behaviour packages for {ConnectionId}",
                    normalized);
                inventory = new BehaviourRemoteInventoryState(
                    normalized,
                    false,
                    previous.RemoteInventory.Packages.Count > 0,
                    $"Could not load the installed behaviour inventory: {ex.Message}",
                    previous.RemoteInventory.Packages,
                    previous.RemoteInventory.LoadedAt);
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = BehaviourWorkspaceSnapshotBuilder.Build(
                normalized,
                _local.Packages,
                inventory,
                compatibilityTarget,
                now);
            lock (_stateGate)
            {
                _snapshots[normalized] = snapshot;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _local.Changed -= OnLocalPackagesChanged;
        _refreshGate.Dispose();
    }

    private void OnLocalPackagesChanged(object? sender, EventArgs e)
    {
        lock (_stateGate)
        {
            foreach (var pair in _snapshots.ToArray())
            {
                var current = pair.Value;
                _snapshots[pair.Key] = BehaviourWorkspaceSnapshotBuilder.Build(
                    pair.Key,
                    _local.Packages,
                    current.RemoteInventory,
                    current.CompatibilityTarget,
                    DateTimeOffset.UtcNow);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizeConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A Logos connection ID is required.", nameof(connectionId));
        }

        return connectionId.Trim();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
