using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapDeploymentBundleServiceTests
{
    [Fact]
    public async Task ExportAndImport_RoundTripsPackageViewsAndDocuments()
    {
        var source = Services(MapPackageTestData.CreateTempDirectory("deployment-source"));
        var packageDirectory = MapPackageTestData.CreateMultiStylePackage(
            MapPackageTestData.CreateTempDirectory("deployment-package"));
        var importedPackage = await source.Installer.ImportAsync(packageDirectory);
        var savedView = new SavedMapView(
            "view-toronto",
            "Toronto deployment",
            importedPackage.Package.Key,
            "topographic",
            new MapViewportSnapshot(-79.3832, 43.6532, 40, 0),
            MapViewportMode.FitAll,
            MapOrientationMode.NorthUp,
            true,
            true,
            true,
            true,
            SelectionKind.None,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await source.Views.UpsertAsync(savedView);
        var document = Path.Combine(MapPackageTestData.CreateTempDirectory("deployment-document"), "mission.logos-mission.json");
        await File.WriteAllTextAsync(document, "{\"schemaVersion\":\"logos.mission.v1\"}");
        var bundlePath = Path.Combine(MapPackageTestData.CreateTempDirectory("deployment-output"), "toronto-field.zip");
        var exporter = new MapDeploymentBundleService(source.Catalog, source.Installer, new MapPackageValidator(), source.Views);

        var exported = await exporter.ExportAsync(new MapDeploymentExportRequest(
            bundlePath,
            "Toronto field deployment",
            [importedPackage.Package.Key],
            [savedView.Id],
            [document]));

        Assert.True(File.Exists(exported.BundlePath));
        Assert.Equal(1, exported.PackageCount);
        Assert.Equal(1, exported.SavedViewCount);
        Assert.Equal(1, exported.DocumentCount);

        var target = Services(MapPackageTestData.CreateTempDirectory("deployment-target"));
        var importer = new MapDeploymentBundleService(target.Catalog, target.Installer, new MapPackageValidator(), target.Views);
        var result = await importer.ImportAsync(exported.BundlePath);

        Assert.Equal("Toronto field deployment", result.DisplayName);
        Assert.Single(result.Packages);
        Assert.Equal(3, result.Packages[0].Styles.Count);
        Assert.Single(target.Views.Views);
        Assert.NotNull(result.DocumentsDirectory);
        Assert.True(File.Exists(Path.Combine(result.DocumentsDirectory!, Path.GetFileName(document))));
    }

    [Fact]
    public async Task Export_RejectsSavedViewWhosePackageIsNotIncluded()
    {
        var source = Services(MapPackageTestData.CreateTempDirectory("deployment-dependency-source"));
        var firstDirectory = MapPackageTestData.CreateMultiStylePackage(
            MapPackageTestData.CreateTempDirectory("deployment-dependency-package"));
        var first = await source.Installer.ImportAsync(firstDirectory);
        var view = new SavedMapView(
            "view-external",
            "External package view",
            "other-package:1.0.0",
            "operational",
            new MapViewportSnapshot(-79.3832, 43.6532, 40, 0),
            MapViewportMode.FitAll,
            MapOrientationMode.NorthUp,
            true,
            true,
            true,
            true,
            SelectionKind.None,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await source.Views.UpsertAsync(view);
        var service = new MapDeploymentBundleService(source.Catalog, source.Installer, new MapPackageValidator(), source.Views);
        var bundlePath = Path.Combine(
            MapPackageTestData.CreateTempDirectory("deployment-dependency-output"),
            "invalid.zip");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(
            new MapDeploymentExportRequest(
                bundlePath,
                "Invalid dependency",
                [first.Package.Key],
                [view.Id],
                [])));

        Assert.Contains("not included", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(bundlePath));
    }

    private static TestServices Services(string libraryPath)
    {
        var configuration = new AppConfiguration { MapLibraryPath = libraryPath };
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(configuration, validator);
        var installer = new MapPackageInstaller(catalog, validator);
        var views = new SavedMapViewRepository(catalog);
        return new TestServices(catalog, installer, views);
    }

    private sealed record TestServices(
        MapPackageCatalog Catalog,
        MapPackageInstaller Installer,
        SavedMapViewRepository Views);
}
