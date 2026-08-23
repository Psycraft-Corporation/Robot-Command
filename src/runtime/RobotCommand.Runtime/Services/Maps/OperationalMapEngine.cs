using System.Text;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Maps;

public sealed class OperationalMapEngine : IOperationalMapEngine
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private readonly AppConfiguration _configuration;
    private readonly IMapPackageCatalog _catalog;
    private readonly IMapDisplayPreferences _preferences;
    private readonly ILocalizationService? _localization;
    private string _onlineStyleId = "standard";
    private bool _startupViewportIssued;

    public OperationalMapEngine(
        AppConfiguration configuration,
        IMapPackageCatalog catalog,
        IMapDisplayPreferences preferences,
        ILocalizationService? localization = null)
    {
        _configuration = configuration;
        _catalog = catalog;
        _preferences = preferences;
        _localization = localization;
        _catalog.Changed += OnDependencyChanged;
        _preferences.Changed += OnDependencyChanged;
        if (_localization is not null)
        {
            _localization.PropertyChanged += OnLocalizationChanged;
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<MapStyleOption> AvailableStyles
        => _catalog.ActivePackage?.Styles ??
           (_configuration.OnlineMapFallbackEnabled ? OnlineStyles() : LegacyStyles());

    public string? SelectedStyleId
    {
        get
        {
            var active = _catalog.ActivePackage;
            return active is null
                ? _onlineStyleId
                : _preferences.ResolveStyleId(active);
        }
    }

    public MapViewportSnapshot StartupViewport
        => new(
            _configuration.MapDefaultLongitude,
            _configuration.MapDefaultLatitude,
            _configuration.MapDefaultResolution,
            0);

    public OperationalMapPresentation Prepare(OperationalMapScene scene)
    {
        if (!_startupViewportIssued && !scene.HasData && scene.Frame == MapFrameKind.Unknown)
        {
            _startupViewportIssued = true;
            scene = new OperationalMapScene(
                MapFrameKind.GlobalWgs84,
                "Global WGS84 · default startup view",
                [],
                [],
                null,
                MapViewportMode.FitAll,
                true)
            {
                RequestedViewport = new MapViewportSnapshot(
                    StartupViewport.LongitudeDegrees,
                    StartupViewport.LatitudeDegrees,
                    StartupViewport.Resolution,
                    StartupViewport.RotationDegrees),
                OperatorLocation = scene.OperatorLocation
            };
        }
        else if (scene.Frame == MapFrameKind.Unknown && !scene.HasData)
        {
            // Once the global map has started, an empty unit list is still a
            // valid global map state. Keep the native basemap and current
            // viewport instead of switching to the empty schematic renderer.
            scene = new OperationalMapScene(
                MapFrameKind.GlobalWgs84,
                "Global WGS84",
                [],
                [],
                null,
                scene.ViewportMode,
                scene.GeometryVisible)
            {
                GeometryVisible = scene.GeometryVisible,
                PolicyVisible = scene.PolicyVisible,
                TrailsVisible = scene.TrailsVisible,
                VehicleLabelsVisible = scene.VehicleLabelsVisible,
                OrientationMode = scene.OrientationMode,
                NavigationRevision = scene.NavigationRevision,
                RequestedViewport = scene.RequestedViewport,
                NavigationRequest = scene.NavigationRequest,
                Follow = scene.Follow,
                OperatorLocation = scene.OperatorLocation
            };
        }

        if (scene.Frame == MapFrameKind.Unknown)
        {
            return OperationalMapPresentation.Empty with { Scene = scene };
        }

        if (scene.Frame is MapFrameKind.LocalEnu or MapFrameKind.LocalNed)
        {
            return new OperationalMapPresentation(
                OperationalMapRendererKind.SchematicLocal,
                scene,
                null,
                false,
                "Local coordinate map",
                "Local ENU/NED positions remain connection-scoped and use the native schematic renderer.",
                string.Empty);
        }

        var active = _catalog.ActivePackage;
        if (active is not null && active.Valid && active.Styles.Count > 0)
        {
            var styleId = _preferences.ResolveStyleId(active);
            var style = active.Styles.FirstOrDefault(item =>
                            string.Equals(item.StyleId, styleId, StringComparison.OrdinalIgnoreCase))
                        ?? active.Styles[0];
            return new OperationalMapPresentation(
                OperationalMapRendererKind.NativeGlobal,
                scene,
                style.Layers.FirstOrDefault()?.Path,
                style.Layers.Count > 0,
                $"{active.DisplayName} · {style.DisplayName}",
                $"Active offline package {active.PackageId} {active.Version} ({active.ZoomSummary}).",
                style.Attribution)
            {
                OfflineLayers = style.Layers,
                Style = style
            };
        }

        var path = _configuration.OperationalMapPath;
        var packageStatus = InspectLegacyPackage(path);
        var fallbackStyles = _configuration.OnlineMapFallbackEnabled
            ? OnlineStyles()
            : LegacyStyles();
        var legacyStyle = fallbackStyles.FirstOrDefault(item =>
                              string.Equals(item.StyleId, _onlineStyleId, StringComparison.OrdinalIgnoreCase))
                          ?? fallbackStyles.First();
        var useOnlineFallback = _configuration.OnlineMapFallbackEnabled && !packageStatus.Available;
        return new OperationalMapPresentation(
            OperationalMapRendererKind.NativeGlobal,
            scene,
            path,
            packageStatus.Available,
            packageStatus.Available
                ? "Legacy offline operational map"
                : useOnlineFallback
                    ? $"{legacyStyle.DisplayName} online map"
                    : "Offline map package unavailable",
            packageStatus.Available
                ? $"Loaded the legacy maps.operationalMbTilesPath setting. Install and activate a package in the Maps workspace to replace it. {packageStatus.Detail}"
                : useOnlineFallback
                    ? "No offline map package is active. Showing the selected online map while an internet connection is available."
                    : packageStatus.Detail,
            packageStatus.Available ? _configuration.OperationalMapAttribution : "© OpenStreetMap contributors")
        {
            OnlineMapFallback = useOnlineFallback,
            OfflineLayers = packageStatus.Available && path is not null
                ? [new OfflineMapLayerDefinition(path, "basemap", 0)]
                : [],
            Style = legacyStyle
        };
    }

    public Task SelectStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = _catalog.ActivePackage;
        if (active is null)
        {
            var online = OnlineStyles().FirstOrDefault(item =>
                string.Equals(item.StyleId, styleId, StringComparison.OrdinalIgnoreCase));
            if (online is null)
            {
                throw new KeyNotFoundException($"Online map style '{styleId}' is not available.");
            }

            _onlineStyleId = online.StyleId;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        var style = active.Styles.FirstOrDefault(item =>
            string.Equals(item.StyleId, styleId, StringComparison.OrdinalIgnoreCase));
        if (style is null)
        {
            throw new KeyNotFoundException($"Map style '{styleId}' is not available in {active.DisplayName}.");
        }

        return _preferences.SelectStyleAsync(active.Key, style.StyleId, cancellationToken);
    }

    private void OnDependencyChanged(object? sender, EventArgs e)
        => Changed?.Invoke(this, EventArgs.Empty);

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Changed?.Invoke(this, EventArgs.Empty);

    private IReadOnlyList<MapStyleOption> LegacyStyles()
    {
        var path = _configuration.OperationalMapPath;
        OfflineMapLayerDefinition[] layers = !string.IsNullOrWhiteSpace(path)
            ? [new OfflineMapLayerDefinition(path, "basemap", 0)]
            : [];
        return
        [
            new MapStyleOption(
                "operational",
                "Operational",
                MapStyleKind.Operational,
                layers,
                _configuration.OperationalMapAttribution,
                true)
        ];
    }

    private IReadOnlyList<MapStyleOption> OnlineStyles()
        =>
        [
            new MapStyleOption("standard", "Standard", MapStyleKind.Operational, [], "© OpenStreetMap contributors", true),
            new MapStyleOption("topographic", _localization?.Get("MapTopographic") ?? "Topographic", MapStyleKind.Topographic, [], "© OpenStreetMap contributors · OpenTopoMap", false),
            new MapStyleOption("satellite", "Satellite", MapStyleKind.Imagery, [], "© Esri", false)
        ];

    private static PackageStatus InspectLegacyPackage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PackageStatus(
                false,
                "Install and activate an offline package in the Maps workspace. Robot Command will not download online tiles automatically.");
        }

        if (!string.Equals(Path.GetExtension(path), ".mbtiles", StringComparison.OrdinalIgnoreCase))
        {
            return new PackageStatus(false, $"The configured map is not an MBTiles archive: {path}");
        }

        if (!File.Exists(path))
        {
            return new PackageStatus(false, $"The configured MBTiles archive was not found: {path}");
        }

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < SqliteHeader.Length)
            {
                return new PackageStatus(false, $"The configured MBTiles archive is empty or truncated: {path}");
            }

            Span<byte> header = stackalloc byte[SqliteHeader.Length];
            if (stream.Read(header) != header.Length || !header.SequenceEqual(SqliteHeader))
            {
                return new PackageStatus(false, $"The configured file is not a valid SQLite/MBTiles archive: {path}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PackageStatus(false, $"The configured MBTiles archive cannot be read: {ex.Message}");
        }

        return new PackageStatus(true, $"Loaded from {path}");
    }

    private sealed record PackageStatus(bool Available, string Detail);
}
