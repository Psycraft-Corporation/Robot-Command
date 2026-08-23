using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RobotCommand.Models;

namespace RobotCommand.Tests;

internal static class MapPackageTestData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string CreatePackage(
        string parentDirectory,
        string packageId = "toronto-operational",
        string version = "1.0.0",
        string relativeMbTilesPath = "tiles/toronto.mbtiles",
        bool correctChecksum = true)
    {
        var root = Path.Combine(parentDirectory, $"{packageId}-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var mapPath = Path.Combine(root, relativeMbTilesPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        File.WriteAllBytes(mapPath, "SQLite format 3\0test-map-data"u8.ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mapPath))).ToLowerInvariant();
        if (!correctChecksum)
        {
            hash = new string('0', 64);
        }

        var manifest = new MapPackageManifest
        {
            PackageId = packageId,
            Version = version,
            DisplayName = "Toronto Operational",
            Kind = MapPackageKind.RegionalOperational,
            Coverage = new MapPackageCoverage
            {
                MinLatitude = 43.4,
                MinLongitude = -79.8,
                MaxLatitude = 44.0,
                MaxLongitude = -79.0
            },
            MinZoom = 0,
            MaxZoom = 15,
            DatasetDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            BuildDate = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            CoordinateSystem = "EPSG:3857",
            Attribution = "Open map test data",
            License = "ODbL-1.0",
            StyleIds = ["operational"],
            Files =
            [
                new MapPackageFile
                {
                    Path = relativeMbTilesPath,
                    Role = "basemap",
                    Sha256 = hash,
                    SizeBytes = new FileInfo(mapPath).Length
                }
            ]
        };

        File.WriteAllText(
            Path.Combine(root, "map-package.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
        return root;
    }

    public static string CreateMultiStylePackage(
        string parentDirectory,
        string packageId = "toronto-field-map",
        string version = "2.0.0")
    {
        var root = Path.Combine(parentDirectory, $"{packageId}-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var files = new List<MapPackageFile>();
        foreach (var (path, role) in new[]
        {
            ("tiles/operational.mbtiles", "operational"),
            ("tiles/topographic.mbtiles", "topographic"),
            ("tiles/imagery.mbtiles", "imagery"),
            ("tiles/reference-labels.mbtiles", "labels")
        })
        {
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, Encoding.UTF8.GetBytes($"SQLite format 3\0{role}-test-data"));
            files.Add(new MapPackageFile
            {
                Path = path,
                Role = role,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant(),
                SizeBytes = new FileInfo(fullPath).Length
            });
        }

        var manifest = new MapPackageManifest
        {
            PackageId = packageId,
            Version = version,
            DisplayName = "Toronto Field Map",
            Kind = MapPackageKind.Deployment,
            Coverage = new MapPackageCoverage
            {
                MinLatitude = 43.4,
                MinLongitude = -79.8,
                MaxLatitude = 44.0,
                MaxLongitude = -79.0
            },
            MinZoom = 0,
            MaxZoom = 16,
            DatasetDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            BuildDate = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            CoordinateSystem = "EPSG:3857",
            Attribution = "Open map test data",
            License = "ODbL-1.0",
            DefaultStyleId = "operational",
            Styles =
            [
                new MapPackageStyle
                {
                    StyleId = "operational",
                    DisplayName = "Operational",
                    Kind = MapStyleKind.Operational,
                    BaseMapFile = "tiles/operational.mbtiles",
                    Default = true
                },
                new MapPackageStyle
                {
                    StyleId = "topographic",
                    DisplayName = "Topographic",
                    Kind = MapStyleKind.Topographic,
                    BaseMapFile = "tiles/topographic.mbtiles"
                },
                new MapPackageStyle
                {
                    StyleId = "imagery",
                    DisplayName = "Imagery",
                    Kind = MapStyleKind.Imagery,
                    BaseMapFile = "tiles/imagery.mbtiles",
                    OverlayFiles = ["tiles/reference-labels.mbtiles"],
                    Attribution = "Imagery and open reference labels"
                }
            ],
            Files = files
        };

        File.WriteAllText(
            Path.Combine(root, "map-package.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
        return root;
    }

    public static string CreateTempDirectory(string category = "map-package")
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Logos-robot-command-tests",
            category,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
