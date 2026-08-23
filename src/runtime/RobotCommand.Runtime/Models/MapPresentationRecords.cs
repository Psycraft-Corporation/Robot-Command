namespace RobotCommand.Models;

public enum MapStyleKind
{
    Operational,
    Topographic,
    Imagery
}

public sealed class MapPackageStyle
{
    public string StyleId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public MapStyleKind Kind { get; init; } = MapStyleKind.Operational;

    public string BaseMapFile { get; init; } = string.Empty;

    public IReadOnlyList<string> OverlayFiles { get; init; } = [];

    public string Attribution { get; init; } = string.Empty;

    public bool Default { get; init; }
}

public sealed record OfflineMapLayerDefinition(
    string Path,
    string Role,
    int Order);

public sealed record MapStyleOption(
    string StyleId,
    string DisplayName,
    MapStyleKind Kind,
    IReadOnlyList<OfflineMapLayerDefinition> Layers,
    string Attribution,
    bool Default)
{
    public bool IsThreeD { get; init; }
    public string Summary => $"{DisplayName} · {Kind}";
}

public sealed record MapViewportSnapshot(
    double LongitudeDegrees,
    double LatitudeDegrees,
    double Resolution,
    double RotationDegrees)
{
    public string CoordinateText => $"{LatitudeDegrees:0.000000}, {LongitudeDegrees:0.000000}";
}

public sealed record SavedMapView(
    string Id,
    string Name,
    string? PackageKey,
    string? StyleId,
    MapViewportSnapshot Viewport,
    MapViewportMode ViewportMode,
    MapOrientationMode OrientationMode,
    bool GeometryVisible,
    bool PolicyVisible,
    bool TrailsVisible,
    bool VehicleLabelsVisible,
    SelectionKind SelectionKind,
    string? SelectionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string LocationSummary => Viewport.CoordinateText;

    public string StyleSummary => string.IsNullOrWhiteSpace(StyleId) ? "Package default" : StyleId;

    // Kept as init-only additions so existing saved-map-views.json files and
    // call sites using the legacy positional record shape remain compatible.
    public IReadOnlyList<string> SelectedUnitIds { get; init; } = [];

    public string? UnitSelectionAnchorId { get; init; }
}

public sealed class MapDeploymentBundleManifest
{
    public string SchemaVersion { get; init; } = "logos.map-deployment.v1";

    public string BundleId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<MapDeploymentPackageEntry> Packages { get; init; } = [];

    public string? SavedViewsPath { get; init; }

    public IReadOnlyList<string> DocumentPaths { get; init; } = [];
}

public sealed class MapDeploymentPackageEntry
{
    public string PackageKey { get; init; } = string.Empty;

    public string PackagePath { get; init; } = string.Empty;
}

public sealed record MapDeploymentExportRequest(
    string DestinationPath,
    string DisplayName,
    IReadOnlyList<string> PackageKeys,
    IReadOnlyList<string> SavedViewIds,
    IReadOnlyList<string> DocumentPaths);

public sealed record MapDeploymentExportResult(
    string BundlePath,
    int PackageCount,
    int SavedViewCount,
    int DocumentCount);

public sealed record MapDeploymentImportResult(
    string BundleId,
    string DisplayName,
    IReadOnlyList<InstalledMapPackage> Packages,
    int SavedViewCount,
    string? DocumentsDirectory);
