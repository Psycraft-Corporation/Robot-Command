using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class SavedMapViewRepositoryTests
{
    [Fact]
    public async Task UpsertAndReload_PreservesViewState()
    {
        var library = MapPackageTestData.CreateTempDirectory("saved-map-views");
        var catalog = new MapPackageCatalog(
            new AppConfiguration { MapLibraryPath = library },
            new MapPackageValidator());
        var repository = new SavedMapViewRepository(catalog);
        var view = View("view-1", "Toronto base");

        await repository.UpsertAsync(view);

        var reloaded = new SavedMapViewRepository(catalog);
        var saved = Assert.Single(reloaded.Views);
        Assert.Equal(view.Id, saved.Id);
        Assert.Equal(view.Name, saved.Name);
        Assert.Equal(view.PackageKey, saved.PackageKey);
        Assert.Equal(view.StyleId, saved.StyleId);
        Assert.Equal(view.Viewport, saved.Viewport);
        Assert.Equal(view.SelectionKind, saved.SelectionKind);
        Assert.Equal(view.SelectionId, saved.SelectionId);
    }

    [Fact]
    public async Task Import_MergesByStableId()
    {
        var library = MapPackageTestData.CreateTempDirectory("saved-map-views");
        var catalog = new MapPackageCatalog(
            new AppConfiguration { MapLibraryPath = library },
            new MapPackageValidator());
        var repository = new SavedMapViewRepository(catalog);
        await repository.UpsertAsync(View("view-1", "Old name"));

        await repository.ImportAsync([View("view-1", "Updated name"), View("view-2", "Second")]);

        Assert.Equal(2, repository.Views.Count);
        Assert.Contains(repository.Views, item => item.Id == "view-1" && item.Name == "Updated name");
    }

    [Fact]
    public async Task Remove_DeletesPersistedView()
    {
        var library = MapPackageTestData.CreateTempDirectory("saved-map-views");
        var catalog = new MapPackageCatalog(
            new AppConfiguration { MapLibraryPath = library },
            new MapPackageValidator());
        var repository = new SavedMapViewRepository(catalog);
        await repository.UpsertAsync(View("view-1", "Toronto base"));

        await repository.RemoveAsync("view-1");

        Assert.Empty(new SavedMapViewRepository(catalog).Views);
    }

    [Fact]
    public async Task Upsert_RejectsInvalidViewport()
    {
        var library = MapPackageTestData.CreateTempDirectory("saved-map-invalid");
        var catalog = new MapPackageCatalog(
            new AppConfiguration { MapLibraryPath = library },
            new MapPackageValidator());
        var repository = new SavedMapViewRepository(catalog);
        var invalid = View("view-invalid", "Invalid") with
        {
            Viewport = new MapViewportSnapshot(double.NaN, 43.6532, 0, 0)
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.UpsertAsync(invalid));
        Assert.Empty(repository.Views);
    }

    [Fact]
    public async Task UpsertAndReload_PreservesMultiUnitSelection()
    {
        var library = MapPackageTestData.CreateTempDirectory("saved-map-multi-selection");
        var catalog = new MapPackageCatalog(new AppConfiguration { MapLibraryPath = library }, new MapPackageValidator());
        var repository = new SavedMapViewRepository(catalog);
        var view = View("view-multi", "Formation") with
        {
            SelectedUnitIds = ["vehicle-1", "vehicle-2"],
            UnitSelectionAnchorId = "vehicle-1"
        };

        await repository.UpsertAsync(view);

        var saved = Assert.Single(new SavedMapViewRepository(catalog).Views);
        Assert.Equal(["vehicle-1", "vehicle-2"], saved.SelectedUnitIds);
        Assert.Equal("vehicle-1", saved.UnitSelectionAnchorId);
    }

    [Fact]
    public async Task UpsertAndReload_PreservesWeatherOverlayState()
    {
        var library = MapPackageTestData.CreateTempDirectory("saved-map-weather");
        var catalog = new MapPackageCatalog(new AppConfiguration { MapLibraryPath = library }, new MapPackageValidator());
        var repository = new SavedMapViewRepository(catalog);
        await repository.UpsertAsync(View("view-weather", "Weather") with
        {
            WeatherVisible = true,
            WeatherOpacity = 0.7
        });

        var saved = Assert.Single(new SavedMapViewRepository(catalog).Views);
        Assert.True(saved.WeatherVisible);
        Assert.Equal(0.7, saved.WeatherOpacity);
    }

    private static SavedMapView View(string id, string name)
        => new(
            id,
            name,
            "package:1.0.0",
            "operational",
            new MapViewportSnapshot(-79.3832, 43.6532, 25, 0),
            MapViewportMode.FitAll,
            MapOrientationMode.NorthUp,
            true,
            true,
            true,
            true,
            SelectionKind.Vehicle,
            "dracula",
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
}
