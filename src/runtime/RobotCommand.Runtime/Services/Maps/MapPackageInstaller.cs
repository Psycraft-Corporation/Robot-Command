using System.IO.Compression;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Maps;

public sealed class MapPackageInstaller : IMapPackageInstaller
{
    private readonly IMapPackageCatalog _catalog;
    private readonly IMapPackageValidator _validator;

    public MapPackageInstaller(
        IMapPackageCatalog catalog,
        IMapPackageValidator validator)
    {
        _catalog = catalog;
        _validator = validator;
    }

    public async Task<MapPackageImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("A local map package directory or archive is required.", nameof(sourcePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(source) && !File.Exists(source))
        {
            throw new FileNotFoundException("The map package source was not found.", source);
        }

        if (Directory.Exists(source) && IsSameOrAncestor(source, _catalog.RootPath))
        {
            throw new InvalidDataException("The map library or one of its parent directories cannot be imported as a package.");
        }

        Directory.CreateDirectory(_catalog.StagingPath);
        var staging = Path.Combine(_catalog.StagingPath, $"import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        string? backup = null;
        string? target = null;
        var targetInstalled = false;

        try
        {
            if (Directory.Exists(source))
            {
                CopyDirectory(source, staging, cancellationToken);
            }
            else
            {
                ExtractArchive(source, staging, cancellationToken);
            }

            var packageRoot = LocatePackageRoot(staging);
            var validation = _validator.Validate(packageRoot);
            if (!validation.IsValid || validation.Manifest is null)
            {
                throw new InvalidDataException(
                    $"Map package validation failed: {string.Join(" ", validation.Issues)}");
            }

            var manifest = validation.Manifest;
            target = Path.Combine(
                _catalog.PackagesPath,
                MapPackagePaths.SafeSegment(manifest.PackageId),
                MapPackagePaths.SafeSegment(manifest.Version));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var replaced = Directory.Exists(target);
            if (replaced)
            {
                backup = Path.Combine(_catalog.StagingPath, $"backup-{Guid.NewGuid():N}");
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(packageRoot, target);
                targetInstalled = true;
            }
            catch
            {
                if (backup is not null && Directory.Exists(backup) && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                    backup = null;
                }

                throw;
            }

            await _catalog.RefreshAsync(cancellationToken);
            var key = MapPackagePaths.PackageKey(manifest.PackageId, manifest.Version);
            if (!_catalog.TryGet(key, out var installed) || installed is null)
            {
                throw new InvalidOperationException("The installed map package could not be found in the refreshed catalog.");
            }

            if (backup is not null && Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
                backup = null;
            }

            return new MapPackageImportResult(
                installed,
                replaced,
                replaced
                    ? $"Replaced {installed.DisplayName} {installed.Version}."
                    : $"Installed {installed.DisplayName} {installed.Version}.");
        }
        catch
        {
            if (target is not null && targetInstalled && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
                targetInstalled = false;
            }

            if (target is not null && backup is not null && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
                backup = null;
            }

            if (target is not null)
            {
                await _catalog.RefreshAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (backup is not null && Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_catalog.TryGet(key, out var package) || package is null)
        {
            return;
        }

        var wasActive = package.Active;
        if (wasActive)
        {
            await _catalog.ClearActiveAsync(cancellationToken);
        }

        Directory.CreateDirectory(_catalog.StagingPath);
        var removedPath = Path.Combine(_catalog.StagingPath, $"removed-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(package.DirectoryPath, removedPath);
            await _catalog.RefreshAsync(cancellationToken);
            Directory.Delete(removedPath, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(package.DirectoryPath) && Directory.Exists(removedPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(package.DirectoryPath)!);
                Directory.Move(removedPath, package.DirectoryPath);
            }

            await _catalog.RefreshAsync(CancellationToken.None);
            if (wasActive && _catalog.TryGet(key, out var restored) && restored is { Valid: true })
            {
                await _catalog.ActivateAsync(key, CancellationToken.None);
            }

            throw;
        }
    }

    private static string LocatePackageRoot(string staging)
    {
        var direct = Path.Combine(staging, MapPackagePaths.ManifestFileName);
        if (File.Exists(direct))
        {
            return staging;
        }

        var manifests = Directory.EnumerateFiles(
                staging,
                MapPackagePaths.ManifestFileName,
                SearchOption.AllDirectories)
            .ToArray();
        return manifests.Length switch
        {
            1 => Path.GetDirectoryName(manifests[0])!,
            0 => throw new InvalidDataException($"The import does not contain {MapPackagePaths.ManifestFileName}."),
            _ => throw new InvalidDataException("The import contains more than one map package manifest.")
        };
    }

    private static void CopyDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Symbolic-link map package directories are not supported.");
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Symbolic links are not allowed in map packages: {directory}");
            }

            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Symbolic links are not allowed in map packages: {file}");
            }

            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsSameOrAncestor(string possibleAncestor, string path)
    {
        var ancestor = Path.GetFullPath(possibleAncestor)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(
            ancestor,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void ExtractArchive(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var extractedPaths = new HashSet<string>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = entry.FullName.Replace('\\', '/');
                if (!MapPackagePaths.TryResolveContainedPath(destination, relative, out var target))
                {
                    throw new InvalidDataException($"Archive entry path is unsafe: '{entry.FullName}'.");
                }

                if (!extractedPaths.Add(target))
                {
                    throw new InvalidDataException($"Archive contains a duplicate entry: '{entry.FullName}'.");
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
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The map package archive could not be extracted: {ex.Message}", ex);
        }
    }
}
