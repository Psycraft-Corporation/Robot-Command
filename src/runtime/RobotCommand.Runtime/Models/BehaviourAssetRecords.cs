namespace RobotCommand.Models;

/// <summary>
/// Stable application identity for a Logos behaviour package. A package folder
/// may be unversioned until Logos has inspected its manifest, so Version is
/// intentionally optional at the local-library boundary.
/// </summary>
public sealed record BehaviourPackageIdentity
{
    public BehaviourPackageIdentity(string behaviourId, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(behaviourId))
        {
            throw new ArgumentException("A behaviour package ID is required.", nameof(behaviourId));
        }

        BehaviourId = behaviourId.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }

    public string BehaviourId { get; }

    public string? Version { get; }

    public string Key => Version is null ? BehaviourId : $"{BehaviourId}@{Version}";

    public override string ToString() => Key;
}

/// <summary>
/// File names declared by a behaviour package manifest. These values are data;
/// the package store introduced in the next drop is responsible for resolving
/// them safely inside PackageDirectory.
/// </summary>
public sealed record BehaviourPackageLayout(
    string PackageDirectory,
    string ManifestFile,
    string? TreeFile,
    string? GeometryFile);

/// <summary>
/// Small manifest projection needed by Robot Command. The raw manifest remains
/// the source of truth passed to Logos; Robot Command does not model individual
/// behaviour-tree node or port definitions.
/// </summary>
public sealed record BehaviourPackageManifestSummary(
    int SchemaVersion,
    string BehaviourId,
    string DisplayName,
    string Description,
    string? Version,
    string? TreeFile,
    string? GeometryFile,
    IReadOnlyDictionary<string, string>? Attributes = null)
{
    public BehaviourPackageIdentity Identity => new(BehaviourId, Version);
}

public enum BehaviourPackageLocalState
{
    Discovered,
    Imported,
    Modified,
    Missing,
    Unreadable,
    Invalid
}

/// <summary>
/// Robot Command can report package integrity and transport problems, while
/// Logos remains authoritative for whether a package is a valid Logos behaviour.
/// </summary>
public enum BehaviourPackageValidationAuthority
{
    RobotCommandIntegrity,
    Logos
}

public enum BehaviourPackageValidationState
{
    NotValidated,
    Validating,
    Valid,
    Warning,
    Invalid,
    Unavailable
}

public enum BehaviourPackageFindingSeverity
{
    Information,
    Warning,
    Error
}

public sealed record BehaviourPackageValidationFinding(
    string Code,
    BehaviourPackageFindingSeverity Severity,
    string Message,
    string? File = null,
    string? Field = null);

public sealed record BehaviourPackageValidationResult(
    BehaviourPackageValidationAuthority Authority,
    BehaviourPackageValidationState State,
    string Summary,
    IReadOnlyList<BehaviourPackageValidationFinding> Findings,
    DateTimeOffset? ValidatedAt = null)
{
    public bool Accepted => State is BehaviourPackageValidationState.Valid or BehaviourPackageValidationState.Warning;

    public static BehaviourPackageValidationResult NotValidated(
        BehaviourPackageValidationAuthority authority,
        string summary = "The package has not been validated.")
        => new(authority, BehaviourPackageValidationState.NotValidated, summary, []);
}

public sealed record LocalBehaviourPackageRecord(
    BehaviourPackageIdentity Identity,
    BehaviourPackageLayout Layout,
    BehaviourPackageManifestSummary? Manifest,
    string DisplayName,
    string Description,
    string? ContentSha256,
    BehaviourPackageLocalState State,
    BehaviourPackageValidationResult Integrity,
    BehaviourPackageValidationResult LogosValidation,
    DateTimeOffset DiscoveredAt,
    DateTimeOffset? ImportedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? RemoteBaselineSha256 = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    IReadOnlyList<string>? ProvidedCapabilities = null,
    IReadOnlyList<string>? CompatibleVehicleProfiles = null,
    IReadOnlyList<BehaviourGeometryRequirement>? GeometrySlots = null,
    IReadOnlyDictionary<string, string>? Attributes = null)
{
    public IReadOnlyList<string> RequiredCapabilityKeys => RequiredCapabilities ?? [];

    public IReadOnlyList<string> ProvidedCapabilityKeys => ProvidedCapabilities ?? [];

    public IReadOnlyList<string> CompatibleProfiles => CompatibleVehicleProfiles ?? [];

    public IReadOnlyList<BehaviourGeometryRequirement> GeometryRequirements => GeometrySlots ?? [];

    public bool LocallyUsable =>
        (State is BehaviourPackageLocalState.Discovered or BehaviourPackageLocalState.Imported or BehaviourPackageLocalState.Modified) &&
        Integrity.Accepted;

    public bool CanSubmitToLogos =>
        State == BehaviourPackageLocalState.Imported &&
        Integrity.Accepted &&
        Manifest is not null &&
        !string.IsNullOrWhiteSpace(ContentSha256);
}

public sealed record RemoteBehaviourPackageRecord(
    string ConnectionId,
    BehaviourPackageIdentity Identity,
    string DisplayName,
    string Description,
    string Status,
    string Channel,
    string? ContentSha256,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> ProvidedCapabilities,
    IReadOnlyList<BehaviourGeometryRequirement> GeometrySlots,
    DateTimeOffset? UpdatedAt = null,
    bool Active = false,
    bool InUse = false,
    BehaviourPackageValidationResult? LogosValidation = null)
{
    public string Key => $"{ConnectionId}:{Identity.Key}";
}

public enum BehaviourDeploymentStatus
{
    Unknown,
    NotInstalled,
    Matching,
    LocalUpdateAvailable,
    RemoteOnly,
    Drifted,
    Conflict,
    Active,
    InUse,
    Incompatible,
    Invalid,
    Unavailable
}

public sealed record BehaviourCompatibilityAssessment(
    bool Compatible,
    IReadOnlyList<string> MissingCapabilities,
    bool VehicleProfileMatched,
    string Summary)
{
    public static BehaviourCompatibilityAssessment Unknown(string summary = "Compatibility has not been assessed.")
        => new(false, [], false, summary);
}

public static class BehaviourCompatibilityRules
{
    public static BehaviourCompatibilityAssessment Evaluate(
        IEnumerable<string>? requiredCapabilities,
        IEnumerable<string>? compatibleVehicleProfiles,
        IEnumerable<string>? vehicleCapabilities,
        string? vehicleProfile)
    {
        var required = Normalize(requiredCapabilities);
        var available = Normalize(vehicleCapabilities).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required
            .Where(item => !available.Contains(item))
            .ToArray();

        var profiles = Normalize(compatibleVehicleProfiles);
        var profileMatched = profiles.Length == 0 ||
                             !string.IsNullOrWhiteSpace(vehicleProfile) &&
                             profiles.Contains(vehicleProfile.Trim(), StringComparer.OrdinalIgnoreCase);
        var compatible = missing.Length == 0 && profileMatched;
        var summary = compatible
            ? "The vehicle satisfies the package-declared compatibility requirements."
            : missing.Length > 0 && !profileMatched
                ? $"Missing capabilities: {string.Join(", ", missing)}. Vehicle profile is not declared compatible."
                : missing.Length > 0
                    ? $"Missing capabilities: {string.Join(", ", missing)}."
                    : "The vehicle profile is not declared compatible.";

        return new BehaviourCompatibilityAssessment(compatible, missing, profileMatched, summary);
    }

    private static string[] Normalize(IEnumerable<string>? values)
        => values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
}

public sealed record BehaviourDeploymentRecord(
    string ConnectionId,
    BehaviourPackageIdentity Identity,
    BehaviourDeploymentStatus Status,
    string Summary,
    string? LocalSha256,
    string? RemoteSha256,
    string? BaselineSha256,
    DateTimeOffset ComparedAt,
    BehaviourCompatibilityAssessment? Compatibility = null,
    bool Active = false,
    bool InUse = false)
{
    public string Key => $"{ConnectionId}:{Identity.Key}";
}

public static class BehaviourDeploymentComparer
{
    public static BehaviourDeploymentRecord Compare(
        string connectionId,
        LocalBehaviourPackageRecord? local,
        RemoteBehaviourPackageRecord? remote,
        bool runtimeAvailable,
        BehaviourCompatibilityAssessment? compatibility = null,
        DateTimeOffset? comparedAt = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A Logos connection ID is required.", nameof(connectionId));
        }

        if (local is null && remote is null)
        {
            throw new ArgumentException("A local or remote behaviour package is required for comparison.");
        }

        var identity = local?.Identity ?? remote!.Identity;
        if (local is not null && remote is not null && local.Identity != remote.Identity)
        {
            throw new ArgumentException("Local and remote behaviour package identities do not match.");
        }

        var now = comparedAt ?? DateTimeOffset.UtcNow;
        var localHash = NormalizeHash(local?.ContentSha256);
        var remoteHash = NormalizeHash(remote?.ContentSha256);
        var baselineHash = NormalizeHash(local?.RemoteBaselineSha256);

        if (!runtimeAvailable)
        {
            return Result(BehaviourDeploymentStatus.Unavailable, "The selected Logos runtime is unavailable.");
        }

        if (local is null)
        {
            return Result(BehaviourDeploymentStatus.RemoteOnly, "The package is installed on Logos but is not in the local library.");
        }

        if (!local.LocallyUsable)
        {
            return Result(BehaviourDeploymentStatus.Invalid, "The local package is unavailable or failed Robot Command integrity checks.");
        }

        if (compatibility is { Compatible: false })
        {
            return Result(BehaviourDeploymentStatus.Incompatible, compatibility.Summary);
        }

        if (remote is null)
        {
            return Result(BehaviourDeploymentStatus.NotInstalled, "The local package is not installed on the selected Logos runtime.");
        }

        if (remote.InUse)
        {
            return Result(BehaviourDeploymentStatus.InUse, "The installed package is referenced by an active Logos execution.");
        }

        if (remote.Active)
        {
            return Result(BehaviourDeploymentStatus.Active, "The installed package is active on the selected Logos runtime.");
        }

        if (localHash is not null && remoteHash is not null && HashEquals(localHash, remoteHash))
        {
            return Result(BehaviourDeploymentStatus.Matching, "Local and installed package content match.");
        }

        if (baselineHash is not null)
        {
            var localChanged = localHash is not null && !HashEquals(localHash, baselineHash);
            var remoteChanged = remoteHash is not null && !HashEquals(remoteHash, baselineHash);
            if (localChanged && !remoteChanged)
            {
                return Result(BehaviourDeploymentStatus.LocalUpdateAvailable, "The local package changed since the last confirmed deployment.");
            }

            if (!localChanged && remoteChanged)
            {
                return Result(BehaviourDeploymentStatus.Drifted, "The installed package changed outside this Robot Command library.");
            }

            if (localChanged && remoteChanged)
            {
                return Result(BehaviourDeploymentStatus.Conflict, "Local and installed packages both changed since the last confirmed deployment.");
            }
        }

        return Result(
            BehaviourDeploymentStatus.Drifted,
            localHash is null || remoteHash is null
                ? "The package is present locally and remotely, but matching hashes are unavailable."
                : "Local and installed package content differ.");

        BehaviourDeploymentRecord Result(BehaviourDeploymentStatus status, string summary)
            => new(
                connectionId.Trim(),
                identity,
                status,
                summary,
                localHash,
                remoteHash,
                baselineHash,
                now,
                compatibility,
                remote?.Active == true,
                remote?.InUse == true);
    }

    private static bool HashEquals(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeHash(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class BehaviourPackageOptionExtensions
{
    public static BehaviourPackageIdentity ToIdentity(this BehaviourPackageOption package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new BehaviourPackageIdentity(package.BehaviourId, package.Version);
    }

    public static RemoteBehaviourPackageRecord ToRemoteRecord(
        this BehaviourPackageOption package,
        string connectionId,
        string? contentSha256 = null,
        bool active = false,
        bool inUse = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A Logos connection ID is required.", nameof(connectionId));
        }

        return new RemoteBehaviourPackageRecord(
            connectionId.Trim(),
            package.ToIdentity(),
            package.DisplayName,
            package.Description,
            package.Status,
            package.Channel,
            contentSha256,
            package.RequiredCapabilities,
            package.ProvidedCapabilities,
            package.GeometrySlots,
            package.UpdatedAt,
            active,
            inUse);
    }
}
