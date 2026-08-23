using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public sealed class MapPackageValidator : IMapPackageValidator
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public MapPackageValidationResult Validate(string packageDirectory)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(packageDirectory))
        {
            return MapPackageValidationResult.Invalid("A package directory is required.");
        }

        var root = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(root))
        {
            return MapPackageValidationResult.Invalid($"Package directory was not found: {root}");
        }

        var manifestPath = Path.Combine(root, MapPackagePaths.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return MapPackageValidationResult.Invalid($"{MapPackagePaths.ManifestFileName} was not found.");
        }

        MapPackageManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize<MapPackageManifest>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return MapPackageValidationResult.Invalid($"The package manifest could not be read: {ex.Message}");
        }

        if (manifest is null)
        {
            return MapPackageValidationResult.Invalid("The package manifest was empty.");
        }

        ValidateManifest(manifest, issues);

        var totalSize = 0L;
        string? primaryMbTilesPath = null;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files ?? [])
        {
            if (file is null)
            {
                issues.Add("Package files cannot contain null entries.");
                continue;
            }

            if (!MapPackagePaths.TryResolveContainedPath(root, file.Path, out var fullPath))
            {
                issues.Add($"Package file path is unsafe: '{file.Path}'.");
                continue;
            }

            var relativeKey = NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
            if (!seenPaths.Add(relativeKey))
            {
                issues.Add($"Package file is listed more than once: '{file.Path}'.");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                issues.Add($"Package file was not found: '{file.Path}'.");
                continue;
            }

            resolvedFiles[relativeKey] = fullPath;
            var info = new FileInfo(fullPath);
            totalSize += info.Length;
            if (file.SizeBytes > 0 && info.Length != file.SizeBytes)
            {
                issues.Add($"Size mismatch for '{file.Path}': expected {file.SizeBytes}, found {info.Length}.");
            }

            if (!string.IsNullOrWhiteSpace(file.Sha256))
            {
                var actual = ComputeSha256(fullPath);
                if (!string.Equals(actual, NormalizeHash(file.Sha256), StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"SHA-256 mismatch for '{file.Path}'.");
                }
            }

            if (string.Equals(Path.GetExtension(fullPath), ".mbtiles", StringComparison.OrdinalIgnoreCase))
            {
                ValidateMbTiles(fullPath, file.Path, issues);
                if (primaryMbTilesPath is null ||
                    string.Equals(file.Role, "basemap", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(file.Role, "operational", StringComparison.OrdinalIgnoreCase))
                {
                    primaryMbTilesPath = fullPath;
                }
            }
        }

        if (primaryMbTilesPath is null)
        {
            issues.Add("The package must contain at least one .mbtiles basemap file.");
        }

        var styles = ResolveStyles(manifest, primaryMbTilesPath, resolvedFiles, issues);
        var defaultStyleId = ResolveDefaultStyleId(manifest, styles, issues);
        var result = issues.Count == 0
            ? new MapPackageValidationResult(
                true,
                $"Package '{manifest.DisplayName}' is valid.",
                [],
                manifest,
                primaryMbTilesPath,
                totalSize)
            : new MapPackageValidationResult(
                false,
                $"{issues.Count} package validation issue(s).",
                issues,
                manifest,
                primaryMbTilesPath,
                totalSize);

        return result with
        {
            Styles = styles,
            DefaultStyleId = defaultStyleId
        };
    }

    private static IReadOnlyList<MapStyleOption> ResolveStyles(
        MapPackageManifest manifest,
        string? primaryMbTilesPath,
        IReadOnlyDictionary<string, string> resolvedFiles,
        ICollection<string> issues)
    {
        var declaredStyles = manifest.Styles ?? [];
        if (declaredStyles.Count == 0)
        {
            if (primaryMbTilesPath is null)
            {
                return [];
            }

            var styleId = (manifest.StyleIds ?? []).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))?.Trim()
                          ?? "operational";
            var kind = manifest.Kind switch
            {
                MapPackageKind.Topographic => MapStyleKind.Topographic,
                MapPackageKind.Imagery => MapStyleKind.Imagery,
                _ => MapStyleKind.Operational
            };
            return
            [
                new MapStyleOption(
                    styleId,
                    kind.ToString(),
                    kind,
                    [new OfflineMapLayerDefinition(primaryMbTilesPath, "basemap", 0)],
                    manifest.Attribution,
                    true)
            ];
        }

        var styles = new List<MapStyleOption>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in declaredStyles)
        {
            if (style is null)
            {
                issues.Add("Map styles cannot contain null entries.");
                continue;
            }

            var styleId = style.StyleId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(styleId))
            {
                issues.Add("Every map style requires styleId.");
                continue;
            }

            if (!seenIds.Add(styleId))
            {
                issues.Add($"Map style '{styleId}' is declared more than once.");
                continue;
            }

            if (!TryResolveStyleFile(style.BaseMapFile, resolvedFiles, out var basePath))
            {
                issues.Add($"Map style '{styleId}' references an unavailable baseMapFile: '{style.BaseMapFile}'.");
                continue;
            }

            var layers = new List<OfflineMapLayerDefinition>
            {
                new(basePath, "basemap", 0)
            };
            var overlayFiles = style.OverlayFiles ?? [];
            for (var index = 0; index < overlayFiles.Count; index++)
            {
                var overlay = overlayFiles[index];
                if (!TryResolveStyleFile(overlay, resolvedFiles, out var overlayPath))
                {
                    issues.Add($"Map style '{styleId}' references an unavailable overlay file: '{overlay}'.");
                    continue;
                }

                layers.Add(new OfflineMapLayerDefinition(overlayPath, "overlay", index + 1));
            }

            styles.Add(new MapStyleOption(
                styleId,
                string.IsNullOrWhiteSpace(style.DisplayName) ? styleId : style.DisplayName.Trim(),
                style.Kind,
                layers,
                string.IsNullOrWhiteSpace(style.Attribution) ? manifest.Attribution : style.Attribution.Trim(),
                style.Default));
        }

        return styles;
    }

    private static string? ResolveDefaultStyleId(
        MapPackageManifest manifest,
        IReadOnlyList<MapStyleOption> styles,
        ICollection<string> issues)
    {
        if (styles.Count == 0)
        {
            return null;
        }

        if (styles.Count(item => item.Default) > 1)
        {
            issues.Add("Only one map style may be marked as the default.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.DefaultStyleId))
        {
            var requested = manifest.DefaultStyleId.Trim();
            if (styles.Any(item => string.Equals(item.StyleId, requested, StringComparison.OrdinalIgnoreCase)))
            {
                return styles.First(item => string.Equals(item.StyleId, requested, StringComparison.OrdinalIgnoreCase)).StyleId;
            }

            issues.Add($"defaultStyleId references an unknown style: '{requested}'.");
        }

        return styles.FirstOrDefault(item => item.Default)?.StyleId ?? styles[0].StyleId;
    }

    private static bool TryResolveStyleFile(
        string? relativePath,
        IReadOnlyDictionary<string, string> resolvedFiles,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var key = NormalizeRelativePath(relativePath);
        if (!resolvedFiles.TryGetValue(key, out var candidate) ||
            string.IsNullOrWhiteSpace(candidate) ||
            !string.Equals(Path.GetExtension(candidate), ".mbtiles", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static void ValidateManifest(MapPackageManifest manifest, ICollection<string> issues)
    {
        if (!string.Equals(manifest.SchemaVersion, "logos.map-package.v1", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("schemaVersion must be 'logos.map-package.v1'.");
        }

        Require(manifest.PackageId, "packageId", issues);
        Require(manifest.Version, "version", issues);
        Require(manifest.DisplayName, "displayName", issues);
        Require(manifest.Attribution, "attribution", issues);
        Require(manifest.License, "license", issues);

        if (!string.Equals(manifest.CoordinateSystem, "EPSG:3857", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("coordinateSystem must be 'EPSG:3857' for raster MBTiles packages.");
        }

        if (manifest.MinZoom < 0 || manifest.MinZoom > 24 ||
            manifest.MaxZoom < 0 || manifest.MaxZoom > 24 ||
            manifest.MaxZoom < manifest.MinZoom)
        {
            issues.Add("minZoom and maxZoom must define an ordered range between 0 and 24.");
        }

        if (manifest.Coverage is null)
        {
            issues.Add("coverage is required.");
        }
        else
        {
            ValidateCoverage(manifest.Coverage, issues);
        }

        if (manifest.Files is null || manifest.Files.Count == 0)
        {
            issues.Add("files must contain at least one package file.");
        }
    }

    private static void ValidateCoverage(MapPackageCoverage coverage, ICollection<string> issues)
    {
        if (!double.IsFinite(coverage.MinLatitude) || !double.IsFinite(coverage.MaxLatitude) ||
            !double.IsFinite(coverage.MinLongitude) || !double.IsFinite(coverage.MaxLongitude) ||
            coverage.MinLatitude is < -90 or > 90 || coverage.MaxLatitude is < -90 or > 90 ||
            coverage.MinLongitude is < -180 or > 180 || coverage.MaxLongitude is < -180 or > 180)
        {
            issues.Add("coverage coordinates are outside valid latitude/longitude bounds.");
        }

        if (coverage.MaxLatitude <= coverage.MinLatitude || coverage.MaxLongitude <= coverage.MinLongitude)
        {
            issues.Add("coverage maximum coordinates must be greater than minimum coordinates.");
        }
    }

    private static void ValidateMbTiles(string path, string relativePath, ICollection<string> issues)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < SqliteHeader.Length)
            {
                issues.Add($"MBTiles file is empty or truncated: '{relativePath}'.");
                return;
            }

            Span<byte> header = stackalloc byte[SqliteHeader.Length];
            if (stream.Read(header) != header.Length || !header.SequenceEqual(SqliteHeader))
            {
                issues.Add($"File is not a SQLite/MBTiles archive: '{relativePath}'.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add($"MBTiles file cannot be read ('{relativePath}'): {ex.Message}");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeHash(string value)
        => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static void Require(string? value, string name, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add($"{name} is required.");
        }
    }
}
