namespace RobotCommand.Models;

public sealed record BehaviourPackageLibraryIssue(
    string Code,
    BehaviourPackageFindingSeverity Severity,
    string Message,
    string? Path = null,
    BehaviourPackageIdentity? Identity = null);

public sealed record BehaviourPackageImportResult(
    LocalBehaviourPackageRecord Package,
    bool Replaced,
    string Message);

public sealed record BehaviourPackageValidationContext(
    BehaviourPackageIdentity Identity,
    BehaviourPackageLayout Layout,
    BehaviourPackageManifestSummary Manifest,
    string? ContentSha256)
{
    public static BehaviourPackageValidationContext From(LocalBehaviourPackageRecord package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new BehaviourPackageValidationContext(
            package.Identity,
            package.Layout,
            package.Manifest ?? throw new InvalidOperationException(
                $"Local behaviour package '{package.Identity.Key}' has no readable manifest."),
            package.ContentSha256);
    }
}
