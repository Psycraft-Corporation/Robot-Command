using System.Text.Json;
using System.Text.Json.Serialization;
using RobotCommand.Models;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapPackageValidatorTests
{
    [Fact]
    public void Validate_AcceptsCompletePackage()
    {
        var root = MapPackageTestData.CreatePackage(MapPackageTestData.CreateTempDirectory());
        var result = new MapPackageValidator().Validate(root);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Manifest);
        Assert.NotNull(result.PrimaryMbTilesPath);
        Assert.True(result.PrimaryMbTilesPath!.EndsWith(".mbtiles", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.TotalSizeBytes > 0);
    }

    [Fact]
    public void Validate_RejectsChecksumMismatch()
    {
        var root = MapPackageTestData.CreatePackage(
            MapPackageTestData.CreateTempDirectory(),
            correctChecksum: false);
        var result = new MapPackageValidator().Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("SHA-256 mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsFileOutsidePackageRoot()
    {
        var root = MapPackageTestData.CreatePackage(MapPackageTestData.CreateTempDirectory());
        var manifestPath = Path.Combine(root, "map-package.json");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var manifest = JsonSerializer.Deserialize<MapPackageManifest>(File.ReadAllText(manifestPath), options)!;
        var unsafeManifest = new MapPackageManifest
        {
            PackageId = manifest.PackageId,
            Version = manifest.Version,
            DisplayName = manifest.DisplayName,
            Kind = manifest.Kind,
            Coverage = manifest.Coverage,
            MinZoom = manifest.MinZoom,
            MaxZoom = manifest.MaxZoom,
            DatasetDate = manifest.DatasetDate,
            BuildDate = manifest.BuildDate,
            CoordinateSystem = manifest.CoordinateSystem,
            Attribution = manifest.Attribution,
            License = manifest.License,
            StyleIds = manifest.StyleIds,
            Files = [new MapPackageFile { Path = "../escape.mbtiles", Role = "basemap" }]
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(unsafeManifest, options));

        var result = new MapPackageValidator().Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsNonSqliteMbTiles()
    {
        var root = MapPackageTestData.CreatePackage(MapPackageTestData.CreateTempDirectory());
        var map = Directory.EnumerateFiles(root, "*.mbtiles", SearchOption.AllDirectories).Single();
        File.WriteAllText(map, "not sqlite");

        var result = new MapPackageValidator().Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Contains("Size mismatch", StringComparison.Ordinal) ||
            issue.Contains("SQLite", StringComparison.Ordinal));
    }
    [Fact]
    public void Validate_ResolvesOperationalTopographicAndImageryStyles()
    {
        var root = MapPackageTestData.CreateMultiStylePackage(MapPackageTestData.CreateTempDirectory());
        var result = new MapPackageValidator().Validate(root);

        Assert.True(result.IsValid);
        Assert.Equal("operational", result.DefaultStyleId);
        Assert.Equal(3, result.Styles.Count);
        Assert.Contains(result.Styles, item => item.Kind == MapStyleKind.Topographic);
        var imagery = result.Styles.Single(item => item.Kind == MapStyleKind.Imagery);
        Assert.Equal(2, imagery.Layers.Count);
    }

    [Fact]
    public void Validate_RejectsMultipleDefaultStyles()
    {
        var root = MapPackageTestData.CreateMultiStylePackage(MapPackageTestData.CreateTempDirectory());
        var manifestPath = Path.Combine(root, "map-package.json");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var manifest = JsonSerializer.Deserialize<MapPackageManifest>(File.ReadAllText(manifestPath), options)!;
        var styles = manifest.Styles
            .Select(item => new MapPackageStyle
            {
                StyleId = item.StyleId,
                DisplayName = item.DisplayName,
                Kind = item.Kind,
                BaseMapFile = item.BaseMapFile,
                OverlayFiles = item.OverlayFiles,
                Attribution = item.Attribution,
                Default = item.StyleId is "operational" or "topographic"
            })
            .ToArray();
        var invalid = new MapPackageManifest
        {
            PackageId = manifest.PackageId,
            Version = manifest.Version,
            DisplayName = manifest.DisplayName,
            Kind = manifest.Kind,
            Coverage = manifest.Coverage,
            MinZoom = manifest.MinZoom,
            MaxZoom = manifest.MaxZoom,
            DatasetDate = manifest.DatasetDate,
            BuildDate = manifest.BuildDate,
            CoordinateSystem = manifest.CoordinateSystem,
            Attribution = manifest.Attribution,
            License = manifest.License,
            Styles = styles,
            Files = manifest.Files
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(invalid, options));

        var result = new MapPackageValidator().Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("Only one", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsStyleThatReferencesUnlistedMapFile()
    {
        var root = MapPackageTestData.CreateMultiStylePackage(MapPackageTestData.CreateTempDirectory());
        var manifestPath = Path.Combine(root, "map-package.json");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var manifest = JsonSerializer.Deserialize<MapPackageManifest>(File.ReadAllText(manifestPath), options)!;
        var invalid = new MapPackageManifest
        {
            PackageId = manifest.PackageId,
            Version = manifest.Version,
            DisplayName = manifest.DisplayName,
            Kind = manifest.Kind,
            Coverage = manifest.Coverage,
            MinZoom = manifest.MinZoom,
            MaxZoom = manifest.MaxZoom,
            DatasetDate = manifest.DatasetDate,
            BuildDate = manifest.BuildDate,
            CoordinateSystem = manifest.CoordinateSystem,
            Attribution = manifest.Attribution,
            License = manifest.License,
            DefaultStyleId = "operational",
            Styles =
            [
                new MapPackageStyle
                {
                    StyleId = "operational",
                    DisplayName = "Operational",
                    BaseMapFile = "tiles/not-listed.mbtiles"
                }
            ],
            Files = manifest.Files
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(invalid, options));

        var result = new MapPackageValidator().Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("baseMapFile", StringComparison.Ordinal));
    }

}
