using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

internal sealed record BehaviourPackageInspection(
    BehaviourPackageManifestSummary Manifest,
    string ManifestFile,
    string? TreeFile,
    string? GeometryFile,
    string ContentSha256,
    int FileCount,
    long TotalBytes);

internal sealed class BehaviourPackageIntegrityInspector
{
    private readonly BehaviourPackageManifestReader _manifestReader;
    private readonly int _maximumFiles;
    private readonly long _maximumBytes;

    public BehaviourPackageIntegrityInspector(
        BehaviourPackageManifestReader manifestReader,
        int maximumFiles,
        long maximumBytes)
    {
        _manifestReader = manifestReader;
        _maximumFiles = maximumFiles;
        _maximumBytes = maximumBytes;
    }

    public async Task<BehaviourPackageInspection> InspectAsync(
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory))
        {
            throw new ArgumentException("A behaviour package directory is required.", nameof(packageDirectory));
        }

        var root = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Behaviour package directory '{root}' does not exist.");
        }

        if (BehaviourPackageLibraryPaths.IsReparsePoint(root))
        {
            throw new InvalidDataException("The behaviour package directory cannot be a symbolic link or reparse point.");
        }

        var files = EnumerateFiles(root, cancellationToken);
        var totalBytes = files.Sum(item => item.Length);
        if (files.Count > _maximumFiles)
        {
            throw new InvalidDataException($"The behaviour package contains {files.Count} files; the configured limit is {_maximumFiles}.");
        }

        if (totalBytes > _maximumBytes)
        {
            throw new InvalidDataException($"The behaviour package is {totalBytes} bytes; the configured limit is {_maximumBytes} bytes.");
        }

        var manifestPath = BehaviourPackageLibraryPaths.FindManifestPath(root);
        EnsureRegularFile(manifestPath, "manifest");
        var manifest = await _manifestReader.ReadAsync(manifestPath, cancellationToken);
        var manifestFile = NormalizeRelativePath(root, manifestPath);
        var treeFile = ValidateReferencedFile(root, manifest.TreeFile, "tree_file");
        var geometryFile = ValidateReferencedFile(root, manifest.GeometryFile, "geometry_file");
        var hash = await ComputeHashAsync(root, files, cancellationToken);
        return new BehaviourPackageInspection(
            manifest,
            manifestFile,
            treeFile,
            geometryFile,
            hash,
            files.Count,
            totalBytes);
    }

    public async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(sourceRoot);
        }

        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
        {
            throw new IOException($"Destination '{destinationDirectory}' already exists.");
        }

        Directory.CreateDirectory(destinationDirectory);
        try
        {
            foreach (var entry in EnumerateEntries(sourceRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceRoot, entry.Path);
                var destination = Path.Combine(destinationDirectory, relative);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = new FileStream(
                    entry.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);
            }
        }
        catch
        {
            TryDeleteDirectory(destinationDirectory);
            throw;
        }
    }

    private List<FileInfo> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var files = new List<FileInfo>();
        long totalBytes = 0;
        foreach (var entry in EnumerateEntries(root, cancellationToken))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var file = new FileInfo(entry.Path);
            files.Add(file);
            totalBytes = checked(totalBytes + file.Length);
            if (files.Count > _maximumFiles)
            {
                throw new InvalidDataException(
                    $"The behaviour package contains more than the configured limit of {_maximumFiles} files.");
            }

            if (totalBytes > _maximumBytes)
            {
                throw new InvalidDataException(
                    $"The behaviour package exceeds the configured limit of {_maximumBytes} bytes.");
            }
        }

        return files;
    }

    private IEnumerable<PackageEntry> EnumerateEntries(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(item => item, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!BehaviourPackageLibraryPaths.IsWithin(root, entry))
                {
                    throw new InvalidDataException($"Package entry '{entry}' escapes the package directory.");
                }

                if (BehaviourPackageLibraryPaths.IsReparsePoint(entry))
                {
                    throw new InvalidDataException($"Package entry '{entry}' is a symbolic link or reparse point.");
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    yield return new PackageEntry(entry, true);
                    pending.Push(entry);
                }
                else
                {
                    yield return new PackageEntry(entry, false);
                }
            }
        }
    }

    private static string? ValidateReferencedFile(string root, string? relativePath, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var resolved = BehaviourPackageLibraryPaths.ResolvePackageFile(root, relativePath, fieldName);
        EnsureRegularFile(resolved, fieldName);
        return NormalizeRelativePath(root, resolved);
    }

    private static void EnsureRegularFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The package {label} file '{path}' does not exist.", path);
        }

        if (BehaviourPackageLibraryPaths.IsReparsePoint(path))
        {
            throw new InvalidDataException($"The package {label} file cannot be a symbolic link or reparse point.");
        }
    }

    private static async Task<string> ComputeHashAsync(
        string root,
        IReadOnlyList<FileInfo> files,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        foreach (var file in files
                     .OrderBy(item => NormalizeRelativePath(root, item.FullName), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(root, file.FullName);
            var pathBytes = Encoding.UTF8.GetBytes(relative);
            var prefix = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(0, 4), pathBytes.Length);
            BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(4, 8), file.Length);
            hash.AppendData(prefix);
            hash.AppendData(pathBytes);

            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record PackageEntry(string Path, bool IsDirectory);
}
