using System.Security.Cryptography;
using System.Text;

namespace RobotCommand.Services.Maps;

internal static class MapPackagePaths
{
    public const string ManifestFileName = "map-package.json";

    public static string PackageKey(string packageId, string version)
        => $"{packageId.Trim()}@{version.Trim()}";

    public static string SafeSegment(string value)
    {
        var raw = value.Trim();
        var builder = new StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('-');
            }
        }

        var result = builder.ToString().Trim('-', '.');
        result = string.IsNullOrWhiteSpace(result) ? "package" : result;
        var changed = !string.Equals(result, raw, StringComparison.Ordinal) || result.Length > 80;
        if (!changed)
        {
            return result;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..10];
        var prefix = result.Length > 64 ? result[..64].TrimEnd('-', '.') : result;
        return $"{prefix}-{hash}";
    }

    public static bool TryResolveContainedPath(
        string rootDirectory,
        string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(normalizedRoot, comparison))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
