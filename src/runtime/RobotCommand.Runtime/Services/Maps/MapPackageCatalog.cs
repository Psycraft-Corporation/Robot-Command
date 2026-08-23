using System.Text.Json;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Maps;

public sealed class MapPackageCatalog : IMapPackageCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly IMapPackageValidator _validator;
    private InstalledMapPackage[] _packages = [];
    private InstalledMapPackage? _activePackage;

    public MapPackageCatalog(AppConfiguration configuration, IMapPackageValidator validator)
    {
        _validator = validator;
        RootPath = Path.GetFullPath(configuration.MapLibraryPath);
        Directory.CreateDirectory(PackagesPath);
        Directory.CreateDirectory(StagingPath);
        RefreshCore(raiseChanged: false);
    }

    public event EventHandler? Changed;

    public string RootPath { get; }

    public IReadOnlyList<InstalledMapPackage> Packages
    {
        get
        {
            lock (_gate)
            {
                return _packages.ToArray();
            }
        }
    }

    public InstalledMapPackage? ActivePackage
    {
        get
        {
            lock (_gate)
            {
                return _activePackage;
            }
        }
    }

    public string PackagesPath => Path.Combine(RootPath, "packages");

    public string StagingPath => Path.Combine(RootPath, ".staging");

    private string StatePath => Path.Combine(RootPath, "catalog-state.json");

    public bool TryGet(string key, out InstalledMapPackage? package)
    {
        lock (_gate)
        {
            package = _packages.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            return package is not null;
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshCore(raiseChanged: true);
        return Task.CompletedTask;
    }

    public Task ActivateAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstalledMapPackage package;
        lock (_gate)
        {
            package = _packages.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Map package '{key}' was not found.");
            if (!package.Valid || string.IsNullOrWhiteSpace(package.PrimaryMbTilesPath))
            {
                throw new InvalidOperationException($"Map package '{package.DisplayName}' is not valid and cannot be activated.");
            }
        }

        PersistState(key);
        RefreshCore(raiseChanged: true);
        return Task.CompletedTask;
    }

    public Task ClearActiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PersistState(null);
        RefreshCore(raiseChanged: true);
        return Task.CompletedTask;
    }

    private void RefreshCore(bool raiseChanged)
    {
        Directory.CreateDirectory(PackagesPath);
        Directory.CreateDirectory(StagingPath);
        var activeKey = ReadState();
        var packages = Directory.EnumerateFiles(
                PackagesPath,
                MapPackagePaths.ManifestFileName,
                SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => BuildInstalledPackage(path!, activeKey))
            .OrderByDescending(item => item.Active)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            _packages = packages;
            _activePackage = packages.FirstOrDefault(item => item.Active && item.Valid);
        }

        if (raiseChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private InstalledMapPackage BuildInstalledPackage(string directory, string? activeKey)
    {
        var validation = _validator.Validate(directory);
        var manifest = validation.Manifest;
        var packageId = string.IsNullOrWhiteSpace(manifest?.PackageId)
            ? Path.GetFileName(Path.GetDirectoryName(directory)) ?? "invalid-package"
            : manifest.PackageId;
        var version = string.IsNullOrWhiteSpace(manifest?.Version)
            ? Path.GetFileName(directory)
            : manifest.Version;
        var key = MapPackagePaths.PackageKey(packageId, version);
        var active = validation.IsValid && string.Equals(activeKey, key, StringComparison.Ordinal);
        var directoryInfo = new DirectoryInfo(directory);

        return new InstalledMapPackage(
            key,
            packageId,
            version,
            string.IsNullOrWhiteSpace(manifest?.DisplayName) ? packageId : manifest.DisplayName,
            manifest?.Kind ?? MapPackageKind.Deployment,
            directory,
            validation.PrimaryMbTilesPath,
            manifest?.Coverage,
            manifest?.MinZoom ?? 0,
            manifest?.MaxZoom ?? 0,
            manifest?.DatasetDate,
            manifest?.BuildDate,
            manifest?.Attribution ?? string.Empty,
            manifest?.License ?? string.Empty,
            manifest?.StyleIds ?? [],
            validation.TotalSizeBytes,
            directoryInfo.CreationTimeUtc,
            validation.IsValid,
            validation.Summary,
            validation.Issues,
            active)
        {
            Styles = validation.Styles,
            DefaultStyleId = validation.DefaultStyleId
        };
    }

    private string? ReadState()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(StatePath);
            return JsonSerializer.Deserialize<CatalogState>(stream, JsonOptions)?.ActivePackageKey;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PersistState(string? activePackageKey)
    {
        Directory.CreateDirectory(RootPath);
        var temporaryPath = StatePath + ".tmp";
        using (var stream = File.Create(temporaryPath))
        {
            JsonSerializer.Serialize(stream, new CatalogState(activePackageKey), JsonOptions);
        }

        File.Move(temporaryPath, StatePath, overwrite: true);
    }

    private sealed record CatalogState(string? ActivePackageKey);
}
