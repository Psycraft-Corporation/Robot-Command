using System.Security.Cryptography;
using System.Text;
using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

public static class BehaviourPackageLibraryPaths
{
    public const string ManifestYaml = "manifest.yaml";
    public const string ManifestYml = "manifest.yml";

    public static string DefaultRootPath(string baseDirectory)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = baseDirectory;
        }

        return Path.GetFullPath(Path.Combine(
            localData,
            "Psycraft",
            "Robot Command",
            "Autonomy",
            "Behaviours"));
    }

    public static string ResolveRootPath(string baseDirectory, string? configuredPath)
        => string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultRootPath(baseDirectory)
            : Path.GetFullPath(Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(baseDirectory, configuredPath));

    public static string PackagesPath(string rootPath) => Path.Combine(rootPath, "packages");

    public static string MetadataPath(string rootPath) => Path.Combine(rootPath, "metadata");

    public static string StagingPath(string rootPath) => Path.Combine(rootPath, "staging");

    public static string StorageKey(BehaviourPackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var readableId = Slug(identity.BehaviourId, "behaviour");
        var readableVersion = Slug(identity.Version, "unversioned");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Key)))
            .ToLowerInvariant()[..16];
        return $"{readableId}--{readableVersion}--{digest}";
    }

    public static string PackagePath(string rootPath, BehaviourPackageIdentity identity)
        => Path.Combine(PackagesPath(rootPath), StorageKey(identity));

    public static string MetadataFilePath(string rootPath, BehaviourPackageIdentity identity)
        => Path.Combine(MetadataPath(rootPath), $"{StorageKey(identity)}.json");

    public static string FindManifestPath(string packageDirectory)
    {
        var yaml = Path.Combine(packageDirectory, ManifestYaml);
        var yml = Path.Combine(packageDirectory, ManifestYml);
        var yamlExists = File.Exists(yaml);
        var ymlExists = File.Exists(yml);
        if (yamlExists && ymlExists)
        {
            throw new InvalidDataException("The package contains both manifest.yaml and manifest.yml. Keep one canonical manifest.");
        }

        if (yamlExists) return yaml;
        if (ymlExists) return yml;
        throw new FileNotFoundException("The behaviour package does not contain manifest.yaml or manifest.yml.");
    }

    public static string ResolvePackageFile(string packageDirectory, string relativePath, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException($"Manifest field '{fieldName}' is empty.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Manifest field '{fieldName}' must be relative to the package folder.");
        }

        var root = EnsureTrailingSeparator(Path.GetFullPath(packageDirectory));
        var candidate = Path.GetFullPath(Path.Combine(packageDirectory, relativePath));
        if (!candidate.StartsWith(root, PathComparison))
        {
            throw new InvalidDataException($"Manifest field '{fieldName}' escapes the package folder.");
        }

        return candidate;
    }

    public static bool IsWithin(string rootPath, string candidatePath)
    {
        var root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(root, candidate, PathComparison) ||
               candidate.StartsWith(EnsureTrailingSeparator(root), PathComparison);
    }

    public static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string Slug(string? value, string fallback)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        normalized = normalized.Trim('-', '_');
        if (normalized.Length > 48)
        {
            normalized = normalized[..48].TrimEnd('-', '_');
        }

        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string EnsureTrailingSeparator(string value)
        => value.EndsWith(Path.DirectorySeparatorChar) || value.EndsWith(Path.AltDirectorySeparatorChar)
            ? value
            : value + Path.DirectorySeparatorChar;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
