using System.Text.Json.Serialization;

namespace RobotCommand.Models;

public enum MapPackageKind
{
    GlobalBase,
    RegionalOperational,
    Topographic,
    Imagery,
    Deployment
}

public sealed class MapPackageCoverage
{
    public double MinLatitude { get; init; }

    public double MinLongitude { get; init; }

    public double MaxLatitude { get; init; }

    public double MaxLongitude { get; init; }

    [JsonIgnore]
    public string Summary =>
        $"{MinLatitude:0.####}, {MinLongitude:0.####} to {MaxLatitude:0.####}, {MaxLongitude:0.####}";
}

public sealed class MapPackageFile
{
    public string Path { get; init; } = string.Empty;

    public string Role { get; init; } = "basemap";

    public string Sha256 { get; init; } = string.Empty;

    public long SizeBytes { get; init; }
}

public sealed class MapPackageManifest
{
    public string SchemaVersion { get; init; } = "logos.map-package.v1";

    public string PackageId { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public MapPackageKind Kind { get; init; } = MapPackageKind.RegionalOperational;

    public MapPackageCoverage? Coverage { get; init; }

    public int MinZoom { get; init; }

    public int MaxZoom { get; init; }

    public DateTimeOffset? DatasetDate { get; init; }

    public DateTimeOffset? BuildDate { get; init; }

    public string CoordinateSystem { get; init; } = "EPSG:3857";

    public string Attribution { get; init; } = string.Empty;

    public string License { get; init; } = string.Empty;

    public IReadOnlyList<string> StyleIds { get; init; } = [];

    public string? DefaultStyleId { get; init; }

    public IReadOnlyList<MapPackageStyle> Styles { get; init; } = [];

    public IReadOnlyList<MapPackageFile> Files { get; init; } = [];
}

public sealed record MapPackageValidationResult(
    bool IsValid,
    string Summary,
    IReadOnlyList<string> Issues,
    MapPackageManifest? Manifest = null,
    string? PrimaryMbTilesPath = null,
    long TotalSizeBytes = 0)
{
    public IReadOnlyList<MapStyleOption> Styles { get; init; } = [];

    public string? DefaultStyleId { get; init; }
    public static MapPackageValidationResult Invalid(params string[] issues)
        => new(false, $"{issues.Length} validation issue(s).", issues);
}

public sealed record InstalledMapPackage(
    string Key,
    string PackageId,
    string Version,
    string DisplayName,
    MapPackageKind Kind,
    string DirectoryPath,
    string? PrimaryMbTilesPath,
    MapPackageCoverage? Coverage,
    int MinZoom,
    int MaxZoom,
    DateTimeOffset? DatasetDate,
    DateTimeOffset? BuildDate,
    string Attribution,
    string License,
    IReadOnlyList<string> StyleIds,
    long InstalledSizeBytes,
    DateTimeOffset InstalledAt,
    bool Valid,
    string ValidationSummary,
    IReadOnlyList<string> ValidationIssues,
    bool Active)
{
    public string StateLabel => !Valid ? "INVALID" : Active ? "ACTIVE" : "INSTALLED";

    public string CoverageSummary => Coverage?.Summary ?? "Coverage not declared";

    public string ZoomSummary => $"z{MinZoom}-z{MaxZoom}";

    public IReadOnlyList<MapStyleOption> Styles { get; init; } = [];

    public string? DefaultStyleId { get; init; }

    public string StylesSummary => Styles.Count == 0
        ? "Operational"
        : string.Join(", ", Styles.Select(item => item.DisplayName));
}

public sealed record MapPackageImportResult(
    InstalledMapPackage Package,
    bool Replaced,
    string Message);
