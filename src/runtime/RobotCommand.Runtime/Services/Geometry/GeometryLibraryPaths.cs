using System.Text.RegularExpressions;

namespace RobotCommand.Services.Geometry;

public static class GeometryLibraryPaths
{
    public const string DocumentsDirectoryName = "documents";
    public const string StagingDirectoryName = ".staging";
    public const string DocumentFilePrefix = "geometry--";
    public const string DocumentFileExtension = ".geometry.json";

    private static readonly Regex GeometryIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string DefaultRootPath()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = AppContext.BaseDirectory;
        }

        return Path.GetFullPath(Path.Combine(
            localData,
            "Psycraft",
            "Robot Command",
            "Autonomy",
            "Geometry"));
    }

    public static string DocumentsPath(string rootPath)
        => Path.Combine(NormalizeRoot(rootPath), DocumentsDirectoryName);

    public static string StagingPath(string rootPath)
        => Path.Combine(NormalizeRoot(rootPath), StagingDirectoryName);

    public static string DocumentFileName(string geometryId)
    {
        ValidateGeometryId(geometryId);
        return $"{DocumentFilePrefix}{geometryId.Trim()}{DocumentFileExtension}";
    }

    public static string DocumentPath(string documentsPath, string geometryId)
    {
        var root = Path.GetFullPath(documentsPath);
        var candidate = Path.GetFullPath(Path.Combine(root, DocumentFileName(geometryId)));
        EnsureContained(root, candidate);
        return candidate;
    }

    public static bool TryGetGeometryId(string path, out string geometryId)
    {
        geometryId = string.Empty;
        var fileName = Path.GetFileName(path);
        if (!fileName.StartsWith(DocumentFilePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(DocumentFileExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var length = fileName.Length - DocumentFilePrefix.Length - DocumentFileExtension.Length;
        if (length <= 0)
        {
            return false;
        }

        var candidate = fileName.Substring(DocumentFilePrefix.Length, length);
        if (!IsValidGeometryId(candidate))
        {
            return false;
        }

        geometryId = candidate;
        return true;
    }

    public static bool IsValidGeometryId(string? geometryId)
        => !string.IsNullOrWhiteSpace(geometryId) && GeometryIdPattern.IsMatch(geometryId.Trim());

    public static void ValidateGeometryId(string? geometryId)
    {
        if (!IsValidGeometryId(geometryId))
        {
            throw new ArgumentException(
                "A geometry ID must begin with an ASCII letter or digit and contain only letters, digits, '.', '_' or '-'.",
                nameof(geometryId));
        }
    }

    public static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A geometry library root path is required.", nameof(rootPath));
        }

        return Path.GetFullPath(rootPath);
    }

    public static bool IsContained(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(root, candidate);
        return relative != "." &&
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    public static void EnsureContained(string rootPath, string candidatePath)
    {
        if (!IsContained(rootPath, candidatePath))
        {
            throw new InvalidOperationException(
                $"Path '{Path.GetFullPath(candidatePath)}' is outside the geometry library root '{Path.GetFullPath(rootPath)}'.");
        }
    }

    public static void EnsureNotSymbolicLink(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (IsSymbolicLink(fullPath))
        {
            throw new InvalidOperationException(
                $"Geometry library paths cannot use symbolic links or reparse points: '{fullPath}'.");
        }
    }

    public static void EnsureNoSymbolicLinksUnder(
        string rootPath,
        string candidatePath,
        bool includeLeaf = true)
    {
        var root = Path.GetFullPath(rootPath);
        var candidate = Path.GetFullPath(candidatePath);
        EnsureContained(root, candidate);
        EnsureNotSymbolicLink(root);

        var relative = Path.GetRelativePath(root, candidate);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!includeLeaf && index == segments.Length - 1)
            {
                break;
            }

            EnsureNotSymbolicLink(current);
        }
    }

    public static bool IsSymbolicLink(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null ||
                new DirectoryInfo(path).LinkTarget is not null)
            {
                return true;
            }

            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
