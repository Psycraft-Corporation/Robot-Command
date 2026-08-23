using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public sealed class MapDeploymentBundleService : IMapDeploymentBundleService
{
    private const string ManifestFileName = "deployment-manifest.json";
    private const string ViewsFileName = "saved-views.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IMapPackageCatalog _catalog;
    private readonly IMapPackageInstaller _installer;
    private readonly IMapPackageValidator _validator;
    private readonly ISavedMapViewRepository _views;

    public MapDeploymentBundleService(
        IMapPackageCatalog catalog,
        IMapPackageInstaller installer,
        IMapPackageValidator validator,
        ISavedMapViewRepository views)
    {
        _catalog = catalog;
        _installer = installer;
        _validator = validator;
        _views = views;
    }

    public async Task<MapDeploymentExportResult> ExportAsync(
        MapDeploymentExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw new ArgumentException("A deployment bundle destination is required.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var packages = (request.PackageKeys ?? [])
            .Distinct(StringComparer.Ordinal)
            .Select(key => _catalog.TryGet(key, out var package) ? package : null)
            .Where(package => package is not null)
            .Cast<InstalledMapPackage>()
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException("Select at least one installed map package for the deployment bundle.");
        }

        var packageKeys = packages.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var requestedViewIds = (request.SavedViewIds ?? []).ToHashSet(StringComparer.Ordinal);
        var views = _views.Views
            .Where(view => requestedViewIds.Count == 0 || requestedViewIds.Contains(view.Id))
            .ToArray();
        var externalView = views.FirstOrDefault(view =>
            !string.IsNullOrWhiteSpace(view.PackageKey) && !packageKeys.Contains(view.PackageKey));
        if (externalView is not null)
        {
            throw new InvalidOperationException(
                $"Saved view '{externalView.Name}' requires map package '{externalView.PackageKey}', which is not included in the deployment bundle.");
        }

        var documents = ResolveDocuments(request.DocumentPaths ?? []);
        var destination = EnsureZipExtension(Path.GetFullPath(request.DestinationPath));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Logos-robot-command", "map-deployment", Guid.NewGuid().ToString("N"));
        var archiveTemporary = destination + ".tmp";
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var packageEntries = new List<MapDeploymentPackageEntry>();
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.Combine(
                    "packages",
                    MapPackagePaths.SafeSegment(package.PackageId),
                    MapPackagePaths.SafeSegment(package.Version));
                var target = Path.Combine(temporaryRoot, relative);
                CopyDirectory(package.DirectoryPath, target, cancellationToken);
                packageEntries.Add(new MapDeploymentPackageEntry
                {
                    PackageKey = package.Key,
                    PackagePath = relative.Replace('\\', '/')
                });
            }

            string? viewsPath = null;
            if (views.Length > 0)
            {
                viewsPath = ViewsFileName;
                await File.WriteAllTextAsync(
                    Path.Combine(temporaryRoot, ViewsFileName),
                    JsonSerializer.Serialize(views, JsonOptions),
                    cancellationToken);
            }

            var documentEntries = new List<string>();
            if (documents.Length > 0)
            {
                var documentDirectory = Path.Combine(temporaryRoot, "documents");
                Directory.CreateDirectory(documentDirectory);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var document in documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = UniqueFileName(Path.GetFileName(document), usedNames);
                    File.Copy(document, Path.Combine(documentDirectory, name), overwrite: false);
                    documentEntries.Add($"documents/{name}");
                }
            }

            var bundleId = $"deployment-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..48];
            var manifest = new MapDeploymentBundleManifest
            {
                BundleId = bundleId,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? Path.GetFileNameWithoutExtension(destination)
                    : request.DisplayName.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
                Packages = packageEntries,
                SavedViewsPath = viewsPath,
                DocumentPaths = documentEntries
            };
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken);

            if (File.Exists(archiveTemporary))
            {
                File.Delete(archiveTemporary);
            }

            ZipFile.CreateFromDirectory(temporaryRoot, archiveTemporary, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(archiveTemporary, destination, overwrite: true);
            return new MapDeploymentExportResult(destination, packages.Length, views.Length, documents.Length);
        }
        finally
        {
            if (File.Exists(archiveTemporary))
            {
                File.Delete(archiveTemporary);
            }

            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    public async Task<MapDeploymentImportResult> ImportAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            throw new ArgumentException("A deployment bundle path is required.", nameof(bundlePath));
        }

        var source = Path.GetFullPath(bundlePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The deployment bundle was not found.", source);
        }

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Logos-robot-command", "map-deployment", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            ExtractArchive(source, temporaryRoot, cancellationToken);
            var manifestPath = Path.Combine(temporaryRoot, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException($"The deployment bundle does not contain {ManifestFileName}.");
            }

            await using var manifestStream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<MapDeploymentBundleManifest>(
                manifestStream,
                JsonOptions,
                cancellationToken)
                ?? throw new InvalidDataException("The deployment bundle manifest was empty.");
            ValidateManifest(manifest);

            var packageSources = ValidatePackageSources(manifest, temporaryRoot, cancellationToken);
            var importedViews = await ReadSavedViewsAsync(manifest, temporaryRoot, cancellationToken);
            var documentSources = ResolveBundleDocuments(manifest, temporaryRoot, cancellationToken);

            var installed = new List<InstalledMapPackage>();
            foreach (var sourcePackage in packageSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _installer.ImportAsync(sourcePackage.Path, cancellationToken);
                installed.Add(result.Package);
            }

            if (importedViews.Length > 0)
            {
                await _views.ImportAsync(importedViews, cancellationToken);
            }

            var importedViewCount = importedViews.Length;

            var documentsDirectory = CopyDocuments(manifest.BundleId, documentSources, cancellationToken);
            return new MapDeploymentImportResult(
                manifest.BundleId,
                manifest.DisplayName,
                installed,
                importedViewCount,
                documentsDirectory);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private IReadOnlyList<PackageSource> ValidatePackageSources(
        MapDeploymentBundleManifest manifest,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        var sources = new List<PackageSource>();
        foreach (var entry in manifest.Packages ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null)
            {
                throw new InvalidDataException("Deployment packages cannot contain null entries.");
            }

            if (!MapPackagePaths.TryResolveContainedPath(temporaryRoot, entry.PackagePath, out var packagePath) ||
                !Directory.Exists(packagePath))
            {
                throw new InvalidDataException($"Deployment package path is unavailable: '{entry.PackagePath}'.");
            }

            var validation = _validator.Validate(packagePath);
            if (!validation.IsValid || validation.Manifest is null)
            {
                throw new InvalidDataException(
                    $"Deployment map package validation failed: {string.Join(" ", validation.Issues)}");
            }

            var actualKey = MapPackagePaths.PackageKey(
                validation.Manifest.PackageId,
                validation.Manifest.Version);
            if (!string.Equals(actualKey, entry.PackageKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Deployment package key mismatch. Manifest expected '{entry.PackageKey}' but package contains '{actualKey}'.");
            }

            sources.Add(new PackageSource(packagePath));
        }

        return sources;
    }

    private static async Task<SavedMapView[]> ReadSavedViewsAsync(
        MapDeploymentBundleManifest manifest,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.SavedViewsPath))
        {
            return [];
        }

        if (!MapPackagePaths.TryResolveContainedPath(temporaryRoot, manifest.SavedViewsPath, out var viewsPath) ||
            !File.Exists(viewsPath))
        {
            throw new InvalidDataException("The deployment bundle saved views file is unavailable.");
        }

        await using var viewsStream = File.OpenRead(viewsPath);
        var views = await JsonSerializer.DeserializeAsync<SavedMapView[]>(
            viewsStream,
            JsonOptions,
            cancellationToken) ?? [];
        foreach (var view in views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (view is null)
            {
                throw new InvalidDataException("Deployment saved views cannot contain null entries.");
            }

            SavedMapViewRepository.ValidateView(view);
        }

        return views;
    }

    private static IReadOnlyList<DocumentSource> ResolveBundleDocuments(
        MapDeploymentBundleManifest manifest,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        if (manifest.DocumentPaths is null || manifest.DocumentPaths.Count == 0)
        {
            return [];
        }

        var documents = new List<DocumentSource>();
        var names = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var relative in manifest.DocumentPaths ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MapPackagePaths.TryResolveContainedPath(temporaryRoot, relative, out var source) || !File.Exists(source))
            {
                throw new InvalidDataException($"Deployment document is unavailable: '{relative}'.");
            }

            var name = Path.GetFileName(source);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"Deployment bundle contains duplicate document names: '{name}'.");
            }

            documents.Add(new DocumentSource(source, name));
        }

        return documents;
    }

    private string? CopyDocuments(
        string bundleId,
        IReadOnlyList<DocumentSource> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return null;
        }

        var target = Path.Combine(
            _catalog.RootPath,
            "deployment-documents",
            MapPackagePaths.SafeSegment(bundleId));
        Directory.CreateDirectory(target);
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(document.Path, Path.Combine(target, document.Name), overwrite: true);
        }

        return target;
    }

    private static void ValidateManifest(MapDeploymentBundleManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, "logos.map-deployment.v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unsupported deployment bundle schemaVersion.");
        }

        if (string.IsNullOrWhiteSpace(manifest.BundleId) || string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            throw new InvalidDataException("The deployment bundle requires bundleId and displayName.");
        }

        if (manifest.Packages is null || manifest.Packages.Count == 0)
        {
            throw new InvalidDataException("The deployment bundle does not contain any map packages.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in manifest.Packages ?? [])
        {
            if (package is null ||
                string.IsNullOrWhiteSpace(package.PackageKey) ||
                string.IsNullOrWhiteSpace(package.PackagePath))
            {
                throw new InvalidDataException("Every deployment package requires packageKey and packagePath.");
            }

            if (!keys.Add(package.PackageKey) || !paths.Add(package.PackagePath))
            {
                throw new InvalidDataException("The deployment bundle contains duplicate map package entries.");
            }
        }
    }

    private static string[] ResolveDocuments(IEnumerable<string> paths)
        => paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(path => File.Exists(path)
                ? path
                : throw new FileNotFoundException("An optional deployment document was not found.", path))
            .ToArray();

    private static string UniqueFileName(string name, ISet<string> used)
    {
        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var index = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{stem}-{index++}{extension}";
        }

        return candidate;
    }

    private static string EnsureZipExtension(string path)
        => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ".zip";

    private static void CopyDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Symbolic links are not allowed in deployment packages: {directory}");
            }

            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Symbolic links are not allowed in deployment packages: {file}");
            }

            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private sealed record PackageSource(string Path);

    private sealed record DocumentSource(string Path, string Name);

    private static void ExtractArchive(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var extracted = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MapPackagePaths.TryResolveContainedPath(destination, entry.FullName.Replace('\\', '/'), out var target))
            {
                throw new InvalidDataException($"Deployment archive entry path is unsafe: '{entry.FullName}'.");
            }

            if (!extracted.Add(target))
            {
                throw new InvalidDataException($"Deployment archive contains a duplicate entry: '{entry.FullName}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }
}
