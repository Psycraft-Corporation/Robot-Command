using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperationalMapEngineTests
{
    [Fact]
    public void Prepare_UsesNativeMapForGlobalScene()
    {
        var configuration = Configuration();
        var engine = Engine(configuration);
        var presentation = engine.Prepare(Scene(MapFrameKind.GlobalWgs84));

        Assert.Equal(OperationalMapRendererKind.NativeGlobal, presentation.Renderer);
        Assert.True(presentation.ShowNativeMap);
        Assert.False(presentation.OfflineMapAvailable);
        Assert.True(presentation.OnlineMapFallback);
        Assert.Equal("Standard online map", presentation.Status);
        Assert.Contains("selected online map", presentation.Detail);
        Assert.Equal("standard", presentation.Style?.StyleId);
    }

    [Fact]
    public void Prepare_UsesTorontoDefaultViewportForEmptyStartupScene()
    {
        var configuration = Configuration();
        var engine = Engine(configuration);

        var presentation = engine.Prepare(OperationalMapScene.Empty);

        Assert.Equal(OperationalMapRendererKind.NativeGlobal, presentation.Renderer);
        Assert.False(presentation.Scene.HasData);
        Assert.Equal(MapFrameKind.GlobalWgs84, presentation.Scene.Frame);
        Assert.Equal(-79.42, presentation.Scene.RequestedViewport?.LongitudeDegrees);
        Assert.Equal(43.73, presentation.Scene.RequestedViewport?.LatitudeDegrees);
        Assert.Equal(60, presentation.Scene.RequestedViewport?.Resolution);
    }

    [Fact]
    public void Prepare_OnlyRequestsTheDefaultViewportForTheInitialEmptyScene()
    {
        var engine = Engine(Configuration());

        var first = engine.Prepare(OperationalMapScene.Empty);
        var second = engine.Prepare(OperationalMapScene.Empty);

        Assert.NotNull(first.Scene.RequestedViewport);
        Assert.Equal(OperationalMapRendererKind.NativeGlobal, first.Renderer);
        Assert.Null(second.Scene.RequestedViewport);
        Assert.Equal(OperationalMapRendererKind.NativeGlobal, second.Renderer);
    }

    [Fact]
    public void Prepare_PreservesOperatorLocationWhenPromotingEmptySceneToGlobalMap()
    {
        var engine = Engine(Configuration());
        var location = OperatorLocationSnapshot.AvailableAt(
            -79.3832,
            43.6532,
            15,
            DateTimeOffset.UtcNow);
        var scene = OperationalMapScene.Empty with { OperatorLocation = location };

        var first = engine.Prepare(scene);
        var second = engine.Prepare(scene);

        Assert.Equal(location, first.Scene.OperatorLocation);
        Assert.Equal(location, second.Scene.OperatorLocation);
    }

    [Theory]
    [InlineData(MapFrameKind.LocalEnu)]
    [InlineData(MapFrameKind.LocalNed)]
    public void Prepare_KeepsLocalFramesOnSchematicRenderer(MapFrameKind frame)
    {
        var engine = Engine(Configuration());
        var presentation = engine.Prepare(Scene(frame));

        Assert.Equal(OperationalMapRendererKind.SchematicLocal, presentation.Renderer);
        Assert.True(presentation.ShowSchematicMap);
        Assert.False(presentation.ShowNativeMap);
    }

    [Fact]
    public void Prepare_AcceptsReadableLegacySqliteMbTilesHeader()
    {
        var directory = MapPackageTestData.CreateTempDirectory("legacy-map");
        var path = Path.Combine(directory, "region.mbtiles");
        File.WriteAllBytes(path, "SQLite format 3\0stub"u8.ToArray());
        var configuration = Configuration(operationalMapPath: path);
        var engine = Engine(configuration);

        var presentation = engine.Prepare(Scene(MapFrameKind.GlobalWgs84));

        Assert.True(presentation.OfflineMapAvailable);
        Assert.False(presentation.OnlineMapFallback);
        Assert.Equal(path, presentation.OfflineMapPath);
        Assert.Contains("Legacy", presentation.Status);
    }

    [Fact]
    public void Prepare_RejectsNonSqliteLegacyMapFile()
    {
        var directory = MapPackageTestData.CreateTempDirectory("legacy-map");
        var path = Path.Combine(directory, "region.mbtiles");
        File.WriteAllText(path, "not sqlite");
        var configuration = Configuration(operationalMapPath: path);
        var engine = Engine(configuration);

        var presentation = engine.Prepare(Scene(MapFrameKind.GlobalWgs84));

        Assert.False(presentation.OfflineMapAvailable);
        Assert.True(presentation.OnlineMapFallback);
        Assert.Contains("selected online map", presentation.Detail);
    }

    [Fact]
    public async Task Prepare_PrefersActiveCatalogPackageOverLegacyPath()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-library");
        var legacy = Path.Combine(MapPackageTestData.CreateTempDirectory("legacy-map"), "legacy.mbtiles");
        File.WriteAllBytes(legacy, "SQLite format 3\0legacy"u8.ToArray());
        var configuration = Configuration(library, legacy);
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(configuration, validator);
        var installer = new MapPackageInstaller(catalog, validator);
        var source = MapPackageTestData.CreatePackage(MapPackageTestData.CreateTempDirectory("map-source"));
        var imported = await installer.ImportAsync(source);
        await catalog.ActivateAsync(imported.Package.Key);
        var engine = new OperationalMapEngine(configuration, catalog, new MapDisplayPreferences(catalog));

        var presentation = engine.Prepare(Scene(MapFrameKind.GlobalWgs84));

        Assert.True(presentation.OfflineMapAvailable);
        Assert.Equal(imported.Package.PrimaryMbTilesPath, presentation.OfflineMapPath);
        Assert.Contains(imported.Package.DisplayName, presentation.Status);
    }

    [Fact]
    public async Task SelectStyle_UsesAndPersistsDeclaredPackagePresentation()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-library");
        var configuration = Configuration(library);
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(configuration, validator);
        var installer = new MapPackageInstaller(catalog, validator);
        var source = MapPackageTestData.CreateMultiStylePackage(MapPackageTestData.CreateTempDirectory("map-source"));
        var imported = await installer.ImportAsync(source);
        await catalog.ActivateAsync(imported.Package.Key);
        var preferences = new MapDisplayPreferences(catalog);
        var engine = new OperationalMapEngine(configuration, catalog, preferences);

        Assert.Equal(3, engine.AvailableStyles.Count);
        Assert.Equal("operational", engine.SelectedStyleId);

        await engine.SelectStyleAsync("imagery");
        var presentation = engine.Prepare(Scene(MapFrameKind.GlobalWgs84));

        Assert.Equal("imagery", presentation.Style?.StyleId);
        Assert.Equal(MapStyleKind.Imagery, presentation.Style?.Kind);
        Assert.Equal(2, presentation.OfflineLayers.Count);
        Assert.Contains("Imagery", presentation.Status);

        var reloaded = new OperationalMapEngine(
            configuration,
            catalog,
            new MapDisplayPreferences(catalog));
        Assert.Equal("imagery", reloaded.SelectedStyleId);
    }

    [Fact]
    public async Task SelectStyle_SupportsOnlineStandardTopographicAndSatellitePresentations()
    {
        var engine = Engine(Configuration());

        Assert.Equal(["standard", "topographic", "satellite"],
            engine.AvailableStyles.Select(item => item.StyleId).ToArray());
        Assert.Equal("Topographic", engine.AvailableStyles.Single(item => item.StyleId == "topographic").DisplayName);

        await engine.SelectStyleAsync("satellite");
        var presentation = engine.Prepare(Scene(MapFrameKind.GlobalWgs84));

        Assert.Equal("satellite", presentation.Style?.StyleId);
        Assert.Equal(MapStyleKind.Imagery, presentation.Style?.Kind);
        Assert.True(presentation.OnlineMapFallback);
    }

    private static OperationalMapEngine Engine(AppConfiguration configuration)
    {
        var catalog = new MapPackageCatalog(configuration, new MapPackageValidator());
        return new OperationalMapEngine(configuration, catalog, new MapDisplayPreferences(catalog));
    }

    private static AppConfiguration Configuration(
        string? mapLibraryPath = null,
        string? operationalMapPath = null)
        => new()
        {
            MapLibraryPath = mapLibraryPath ?? MapPackageTestData.CreateTempDirectory("map-library"),
            OperationalMapPath = operationalMapPath
        };

    private static OperationalMapScene Scene(MapFrameKind frame)
        => new(
            frame,
            frame.ToString(),
            [new MapVehicleVisual("vehicle", "Vehicle", -79.3832, 43.6532, 0, AvailabilityState.Online, true)],
            [],
            "vehicle",
            MapViewportMode.FitAll,
            true);
}
