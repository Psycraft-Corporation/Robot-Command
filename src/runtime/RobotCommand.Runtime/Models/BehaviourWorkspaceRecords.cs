namespace RobotCommand.Models;

/// <summary>
/// Optional vehicle or runtime target used to assess whether a behaviour can be
/// offered for a particular operation. Robot Command evaluates only package-
/// declared capability and profile requirements; Logos remains authoritative
/// for operational readiness and behaviour validity.
/// </summary>
public sealed record BehaviourCompatibilityTarget(
    string TargetId,
    string DisplayName,
    IReadOnlyList<string> CapabilityKeys,
    string? VehicleProfileKey = null)
{
    public static BehaviourCompatibilityTarget Create(
        string targetId,
        string displayName,
        IEnumerable<string>? capabilityKeys,
        string? vehicleProfileKey = null)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("A compatibility target ID is required.", nameof(targetId));
        }

        return new BehaviourCompatibilityTarget(
            targetId.Trim(),
            string.IsNullOrWhiteSpace(displayName) ? targetId.Trim() : displayName.Trim(),
            capabilityKeys?
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [],
            string.IsNullOrWhiteSpace(vehicleProfileKey) ? null : vehicleProfileKey.Trim());
    }
}

public sealed record BehaviourRemoteInventoryState(
    string ConnectionId,
    bool Available,
    bool Stale,
    string Summary,
    IReadOnlyList<RemoteBehaviourPackageRecord> Packages,
    DateTimeOffset? LoadedAt = null)
{
    public static BehaviourRemoteInventoryState NotLoaded(string connectionId)
        => new(
            connectionId,
            false,
            false,
            "The installed behaviour inventory has not been loaded.",
            []);
}

public sealed record BehaviourWorkspaceEntry(
    BehaviourPackageIdentity Identity,
    string DisplayName,
    string Description,
    LocalBehaviourPackageRecord? Local,
    RemoteBehaviourPackageRecord? Remote,
    BehaviourDeploymentRecord Deployment,
    BehaviourCompatibilityAssessment? Compatibility = null)
{
    public bool HasLocalPackage => Local is not null;

    public bool HasRemotePackage => Remote is not null;

    public bool CanSubmitLocalPackage => Local?.CanSubmitToLogos == true;

    public bool Compatible => Compatibility?.Compatible != false;

    public string Key => Identity.Key;
}

public sealed record BehaviourWorkspaceSnapshot(
    string ConnectionId,
    BehaviourRemoteInventoryState RemoteInventory,
    IReadOnlyList<BehaviourWorkspaceEntry> Entries,
    BehaviourCompatibilityTarget? CompatibilityTarget,
    DateTimeOffset RefreshedAt)
{
    public IReadOnlyList<LocalBehaviourPackageRecord> LocalPackages => Entries
        .Where(item => item.Local is not null)
        .Select(item => item.Local!)
        .ToArray();

    public IReadOnlyList<RemoteBehaviourPackageRecord> RemotePackages => RemoteInventory.Packages;

    public static BehaviourWorkspaceSnapshot Empty(
        string connectionId,
        IEnumerable<LocalBehaviourPackageRecord>? localPackages = null,
        BehaviourCompatibilityTarget? compatibilityTarget = null,
        DateTimeOffset? refreshedAt = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A Logos connection ID is required.", nameof(connectionId));
        }

        var normalized = connectionId.Trim();
        var remote = BehaviourRemoteInventoryState.NotLoaded(normalized);
        return BehaviourWorkspaceSnapshotBuilder.Build(
            normalized,
            localPackages ?? [],
            remote,
            compatibilityTarget,
            refreshedAt ?? DateTimeOffset.UtcNow);
    }
}

internal static class BehaviourWorkspaceSnapshotBuilder
{
    public static BehaviourWorkspaceSnapshot Build(
        string connectionId,
        IEnumerable<LocalBehaviourPackageRecord> localPackages,
        BehaviourRemoteInventoryState remoteInventory,
        BehaviourCompatibilityTarget? compatibilityTarget,
        DateTimeOffset refreshedAt)
    {
        var local = localPackages
            .GroupBy(item => item.Identity)
            .ToDictionary(group => group.Key, group => group.First());
        var remote = remoteInventory.Packages
            .Where(item => string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
            .GroupBy(item => item.Identity)
            .ToDictionary(group => group.Key, group => group.First());
        var identities = local.Keys
            .Concat(remote.Keys)
            .Distinct()
            .ToArray();
        var entries = new List<BehaviourWorkspaceEntry>(identities.Length);

        foreach (var identity in identities)
        {
            local.TryGetValue(identity, out var localPackage);
            remote.TryGetValue(identity, out var remotePackage);
            var compatibility = compatibilityTarget is null
                ? null
                : BehaviourCompatibilityRules.Evaluate(
                    localPackage?.RequiredCapabilityKeys ?? remotePackage?.RequiredCapabilities,
                    localPackage?.CompatibleProfiles,
                    compatibilityTarget.CapabilityKeys,
                    compatibilityTarget.VehicleProfileKey);
            var deployment = BehaviourDeploymentComparer.Compare(
                connectionId,
                localPackage,
                remotePackage,
                remoteInventory.Available,
                compatibility,
                refreshedAt);
            entries.Add(new BehaviourWorkspaceEntry(
                identity,
                ResolveDisplayName(identity, localPackage, remotePackage),
                localPackage?.Description ?? remotePackage?.Description ?? string.Empty,
                localPackage,
                remotePackage,
                deployment,
                compatibility));
        }

        return new BehaviourWorkspaceSnapshot(
            connectionId,
            remoteInventory,
            entries
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Identity.BehaviourId, StringComparer.Ordinal)
                .ThenByDescending(item => item.Identity.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            compatibilityTarget,
            refreshedAt);
    }

    private static string ResolveDisplayName(
        BehaviourPackageIdentity identity,
        LocalBehaviourPackageRecord? local,
        RemoteBehaviourPackageRecord? remote)
        => !string.IsNullOrWhiteSpace(local?.DisplayName)
            ? local.DisplayName
            : !string.IsNullOrWhiteSpace(remote?.DisplayName)
                ? remote.DisplayName
                : identity.BehaviourId;
}
