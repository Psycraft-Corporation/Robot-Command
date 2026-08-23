using System.IO.Compression;
using RobotCommand.Services;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapPackageInstallerTests
{
    [Fact]
    public async Task Import_InstallsValidatedDirectoryAtomically()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-library");
        var source = MapPackageTestData.CreatePackage(MapPackageTestData.CreateTempDirectory("map-source"));
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(new AppConfiguration { MapLibraryPath = library }, validator);
        var installer = new MapPackageInstaller(catalog, validator);

        var result = await installer.ImportAsync(source);

        Assert.False(result.Replaced);
        Assert.True(result.Package.Valid);
        Assert.True(File.Exists(Path.Combine(result.Package.DirectoryPath, "map-package.json")));
        Assert.False(result.Package.DirectoryPath.StartsWith(source, StringComparison.OrdinalIgnoreCase));
        Assert.Single(catalog.Packages);
    }

    [Fact]
    public async Task Import_ReplacesSamePackageVersionOnlyAfterValidation()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-library");
        var sourceRoot = MapPackageTestData.CreateTempDirectory("map-source");
        var first = MapPackageTestData.CreatePackage(sourceRoot, version: "1.0.0");
        var replacement = MapPackageTestData.CreatePackage(sourceRoot, version: "1.0.0");
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(new AppConfiguration { MapLibraryPath = library }, validator);
        var installer = new MapPackageInstaller(catalog, validator);

        await installer.ImportAsync(first);
        var result = await installer.ImportAsync(replacement);

        Assert.True(result.Replaced);
        Assert.Single(catalog.Packages);
        Assert.True(catalog.Packages[0].Valid);
    }


    [Fact]
    public async Task Import_AcceptsZipWithTopLevelDirectory()
    {
        var root = MapPackageTestData.CreateTempDirectory("map-archive");
        var source = MapPackageTestData.CreatePackage(Path.Combine(root, "source"));
        var archivePath = Path.Combine(root, "package.zip");
        ZipFile.CreateFromDirectory(source, archivePath, CompressionLevel.NoCompression, includeBaseDirectory: true);
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(
            new AppConfiguration { MapLibraryPath = Path.Combine(root, "library") },
            validator);
        var installer = new MapPackageInstaller(catalog, validator);

        var result = await installer.ImportAsync(archivePath);

        Assert.True(result.Package.Valid);
        Assert.Single(catalog.Packages);
    }

    [Fact]
    public async Task Import_InvalidReplacementKeepsExistingPackage()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-library");
        var sourceRoot = MapPackageTestData.CreateTempDirectory("map-source");
        var first = MapPackageTestData.CreatePackage(sourceRoot, version: "1.0.0");
        var invalid = MapPackageTestData.CreatePackage(
            sourceRoot,
            version: "1.0.0",
            correctChecksum: false);
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(new AppConfiguration { MapLibraryPath = library }, validator);
        var installer = new MapPackageInstaller(catalog, validator);
        var installed = await installer.ImportAsync(first);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(invalid));

        Assert.Single(catalog.Packages);
        Assert.True(catalog.Packages[0].Valid);
        Assert.Equal(installed.Package.DirectoryPath, catalog.Packages[0].DirectoryPath);
        Assert.True(File.Exists(catalog.Packages[0].PrimaryMbTilesPath));
    }

    [Fact]
    public async Task Import_RejectsArchiveTraversal()
    {
        var root = MapPackageTestData.CreateTempDirectory("map-archive");
        var archivePath = Path.Combine(root, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("escape");
        }

        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(
            new AppConfiguration { MapLibraryPath = Path.Combine(root, "library") },
            validator);
        var installer = new MapPackageInstaller(catalog, validator);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(archivePath));
        Assert.Empty(catalog.Packages);
    }

    [Fact]
    public async Task Remove_ClearsActivePackageAndDeletesDirectory()
    {
        var library = MapPackageTestData.CreateTempDirectory("map-library");
        var source = MapPackageTestData.CreatePackage(MapPackageTestData.CreateTempDirectory("map-source"));
        var validator = new MapPackageValidator();
        var catalog = new MapPackageCatalog(new AppConfiguration { MapLibraryPath = library }, validator);
        var installer = new MapPackageInstaller(catalog, validator);
        var imported = await installer.ImportAsync(source);
        await catalog.ActivateAsync(imported.Package.Key);
        var installedDirectory = imported.Package.DirectoryPath;

        await installer.RemoveAsync(imported.Package.Key);

        Assert.Null(catalog.ActivePackage);
        Assert.Empty(catalog.Packages);
        Assert.False(Directory.Exists(installedDirectory));
    }
}
