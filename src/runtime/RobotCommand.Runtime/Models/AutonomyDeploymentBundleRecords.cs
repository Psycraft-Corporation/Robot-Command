using System.Text.Json.Serialization;

namespace RobotCommand.Models;

public enum AutonomyBundleAssetKind
{
    Geometry,
    BehaviourPackage,
    BehaviourBinding,
    PolicyProfile,
    MissionTemplate,
    TaskTemplate
}

public enum AutonomyDeploymentStepState
{
    Planned,
    Completed,
    Deferred,
    Skipped,
    Failed
}

public sealed record AutonomyBundleFileEntry(
    string Path,
    AutonomyBundleAssetKind Kind,
    long SizeBytes,
    string Sha256);

public sealed record AutonomyBundleBehaviourAsset(
    string BehaviourId,
    string? Version,
    string RelativeDirectory,
    string? ContentSha256)
{
    [JsonIgnore]
    public BehaviourPackageIdentity Identity => new(BehaviourId, Version);
}

public sealed record AutonomyBundleDocumentAsset(
    string AssetId,
    string RelativePath);

public sealed record AutonomyBundleBindingIntent(
    string BehaviourId,
    string? Version,
    string SlotId,
    string GeometryId,
    bool Required = false)
{
    [JsonIgnore]
    public BehaviourPackageIdentity Identity => new(BehaviourId, Version);
}

public sealed record AutonomyDeploymentBundleManifest(
    string SchemaVersion,
    string BundleId,
    string DisplayName,
    string Description,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AutonomyBundleBehaviourAsset> Behaviours,
    IReadOnlyList<AutonomyBundleDocumentAsset> Geometry,
    IReadOnlyList<AutonomyBundleDocumentAsset> Policies,
    IReadOnlyList<AutonomyBundleDocumentAsset> Missions,
    IReadOnlyList<AutonomyBundleDocumentAsset> Tasks,
    IReadOnlyList<AutonomyBundleBindingIntent> Bindings,
    IReadOnlyList<AutonomyBundleFileEntry> Files,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public const string CurrentSchemaVersion = "logos.autonomy-deployment.v1";

    [JsonIgnore]
    public int AssetCount =>
        (Behaviours?.Count ?? 0) +
        (Geometry?.Count ?? 0) +
        (Policies?.Count ?? 0) +
        (Missions?.Count ?? 0) +
        (Tasks?.Count ?? 0) +
        (Bindings?.Count ?? 0);
}

public sealed record AutonomyBundleExportRequest(
    string DestinationPath,
    string DisplayName,
    string Description,
    IReadOnlyList<BehaviourPackageIdentity> BehaviourPackages,
    IReadOnlyList<string> GeometryIds,
    IReadOnlyList<string> MissionIds,
    IReadOnlyList<string> TaskIds,
    IReadOnlyList<string> PolicyFiles,
    IReadOnlyList<AutonomyBundleBindingIntent> BindingIntents,
    bool AllowReplace = false,
    string? BundleId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AutonomyBundleImportOptions(
    bool AllowReplaceBundle = false,
    bool AllowReplaceBehaviours = false,
    bool AllowReplaceGeometry = false,
    bool ImportMissions = true,
    bool ImportTasks = true);

public sealed record AutonomyDeploymentStep(
    int Sequence,
    AutonomyBundleAssetKind Kind,
    string AssetId,
    string Action,
    AutonomyDeploymentStepState State,
    string Summary)
{
    public string Label => $"{Sequence}. {Action}";
}

public sealed record AutonomyBundleInspectionResult(
    string ArchivePath,
    bool Valid,
    string Summary,
    IReadOnlyList<string> Issues,
    AutonomyDeploymentBundleManifest? Manifest,
    IReadOnlyList<AutonomyDeploymentStep> Plan)
{
    public static AutonomyBundleInspectionResult Invalid(
        string archivePath,
        string summary,
        IReadOnlyList<string> issues)
        => new(archivePath, false, summary, issues, null, []);
}

public sealed record AutonomyBundleImportResult(
    string ArchivePath,
    string ImportedBundlePath,
    bool Succeeded,
    string Summary,
    IReadOnlyList<string> Issues,
    IReadOnlyList<AutonomyDeploymentStep> Steps,
    int ImportedGeometryCount,
    int ImportedBehaviourCount,
    int ImportedMissionCount,
    int ImportedTaskCount)
{
    public bool Partial => !Succeeded && Steps.Any(item => item.State == AutonomyDeploymentStepState.Completed);
}
