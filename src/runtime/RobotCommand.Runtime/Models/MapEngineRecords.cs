namespace RobotCommand.Models;

public enum OperationalMapRendererKind
{
    Empty,
    NativeGlobal,
    SchematicLocal
}

public sealed record OperationalMapPresentation(
    OperationalMapRendererKind Renderer,
    OperationalMapScene Scene,
    string? OfflineMapPath,
    bool OfflineMapAvailable,
    string Status,
    string Detail,
    string Attribution)
{
    public static OperationalMapPresentation Empty { get; } = new(
        OperationalMapRendererKind.Empty,
        OperationalMapScene.Empty,
        null,
        false,
        "No compatible map data",
        "Connect a Logos runtime with global or local position data.",
        string.Empty);

    public bool ShowNativeMap => Renderer == OperationalMapRendererKind.NativeGlobal;

    public bool ShowSchematicMap => Renderer is OperationalMapRendererKind.SchematicLocal or OperationalMapRendererKind.Empty;

    public bool OnlineMapFallback { get; init; }

    public bool ShowMapPackageMessage => ShowNativeMap && !OfflineMapAvailable && !OnlineMapFallback;

    public IReadOnlyList<OfflineMapLayerDefinition> OfflineLayers { get; init; } = [];

    public MapStyleOption? Style { get; init; }

    public WeatherRadarSnapshot? WeatherRadar { get; init; }

    public bool WeatherVisible { get; init; }

    public double WeatherOpacity { get; init; } = 0.55;

    public string StyleLabel => Style?.DisplayName ?? "Operational";

    public GeometryEditSnapshot GeometryEdit { get; init; } = GeometryEditSnapshot.Empty;

    public bool HasGeometryEdit => GeometryEdit.HasDraft;

    public MapVehicleMotionSnapshot Motion { get; init; } = MapVehicleMotionSnapshot.Empty;
}

public readonly record struct ProjectedMapPoint(double X, double Y);

public readonly record struct ProjectedMapExtent(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;

    public ProjectedMapExtent Expand(double amount)
        => new(MinX - amount, MinY - amount, MaxX + amount, MaxY + amount);
}
