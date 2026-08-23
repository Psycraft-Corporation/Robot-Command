namespace RobotCommand.Services.Media;

public sealed record MediaMtxExecutable(string Path);

public static class MediaMtxExecutableLocator
{
    public static MediaMtxExecutable? Locate(
        string? configuredPath = null,
        string? applicationBaseDirectory = null)
    {
        foreach (var candidate in Candidates(configuredPath, applicationBaseDirectory))
        {
            if (File.Exists(candidate))
            {
                return new MediaMtxExecutable(Path.GetFullPath(candidate));
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string? configuredPath, string? applicationBaseDirectory)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var value in new[]
        {
            configuredPath,
            Environment.GetEnvironmentVariable("MEDIAMTX_PATH"),
            Environment.GetEnvironmentVariable("MEDIAMTX_ROOT") is { Length: > 0 } root
                ? Path.Combine(root, "mediamtx")
                : null,
            Path.Combine(applicationBaseDirectory ?? AppContext.BaseDirectory, "native", "mediamtx", ExecutableName()),
            Path.Combine(applicationBaseDirectory ?? AppContext.BaseDirectory, "native", "mediamtx", "bin", ExecutableName())
        })
        {
            foreach (var candidate in Expand(value))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(Path.Combine(directory, ExecutableName())))
            {
                yield return Path.Combine(directory, ExecutableName());
            }
        }
    }

    private static IEnumerable<string> Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        }
        catch
        {
            yield break;
        }

        if (File.Exists(full) || Path.GetFileName(full).Equals(ExecutableName(), StringComparison.OrdinalIgnoreCase))
        {
            yield return full;
            yield break;
        }

        yield return Path.Combine(full, ExecutableName());
        yield return Path.Combine(full, "bin", ExecutableName());
    }

    private static string ExecutableName() => OperatingSystem.IsWindows() ? "mediamtx.exe" : "mediamtx";
}
