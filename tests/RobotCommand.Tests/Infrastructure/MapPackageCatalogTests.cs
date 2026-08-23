using RobotCommand.Services;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapPackageCatalogTests
{
    [Fact]
    public async Task Activate_PersistsAcrossCatalogInstances()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-library");
        var source = MapPackageTestData.CreatePackage(MapPackageTestData.CreateTempDirectory("map-source"));
        var configuration = new AppConfiguration { MapLibraryPath = library };
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(configuration, validator);
        var installer = new MapPackageInstaller(catalog, validator);

        var imported = await installer.ImportAsync(source);
        await catalog.ActivateAsync(imported.Package.Key);
        var reopened = new MapPackageCatalog(configuration, validator);

        Assert.NotNull(reopened.ActivePackage);
        Assert.Equal(imported.Package.Key, reopened.ActivePackage!.Key);
        Assert.True(reopened.ActivePackage.Valid);
    }

    [Fact]
    public async Task ClearActive_LeavesPackageInstalled()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-library");
        var source = MapPackageTestData.CreatePackage(MapPackageTestData.CreateTempDirectory("map-source"));
        var configuration = new AppConfiguration { MapLibraryPath = library };
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(configuration, validator);
        var installer = new MapPackageInstaller(catalog, validator);

        var imported = await installer.ImportAsync(source);
        await catalog.ActivateAsync(imported.Package.Key);
        await catalog.ClearActiveAsync();

        Assert.Null(catalog.ActivePackage);
        Assert.Single(catalog.Packages);
        Assert.False(catalog.Packages[0].Active);
    }
}
