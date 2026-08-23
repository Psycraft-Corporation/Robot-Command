using RobotCommand.Services;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapDisplayPreferencesTests
{
    [Fact]
    public async Task SelectedStyle_IsPersistedPerPackage()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-preferences");
        var configuration = new AppConfiguration { MapLibraryPath = library };
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(configuration, validator);
        var installer = new MapPackageInstaller(catalog, validator);
        var source = MapPackageTestData.CreateMultiStylePackage(MapPackageTestData.CreateTempDirectory("map-source"));
        var imported = await installer.ImportAsync(source);
        var first = new MapDisplayPreferences(catalog);

        await first.SelectStyleAsync(imported.Package.Key, "topographic");

        var reloaded = new MapDisplayPreferences(catalog);
        Assert.Equal("topographic", reloaded.ResolveStyleId(imported.Package));
    }
}
