namespace RobotCommand.Services.Media;

public sealed record GStreamerExecutables(string Launch, string Inspect);

public static class GStreamerExecutableLocator
{
    private static readonly string[] RootEnvironmentVariables =
    [
        "GSTREAMER_1_0_ROOT_MSVC_X86_64",
        "GSTREAMER_1_0_ROOT_MSVC_X86",
        "GSTREAMER_1_0_ROOT_MINGW_X86_64",
        "GSTREAMER_1_0_ROOT_MINGW_X86",
        "GSTREAMER_1_0_ROOT_X86_64",
        "GSTREAMER_1_0_ROOT_X86",
        "GSTREAMER_ROOT"
    ];

    public static GStreamerExecutables? Locate(
        string? configuredBinPath = null,
        string? applicationBaseDirectory = null)
    {
        foreach (var directory in CandidateDirectories(configuredBinPath, applicationBaseDirectory))
        {
            var launch = Path.Combine(directory, ExecutableName("gst-launch-1.0"));
            var inspect = Path.Combine(directory, ExecutableName("gst-inspect-1.0"));
            if (File.Exists(launch) && File.Exists(inspect))
            {
                return new GStreamerExecutables(
                    Path.GetFullPath(launch),
                    Path.GetFullPath(inspect));
            }
        }

        return null;
    }

    public static string? ResolvePluginPath(
        string? configuredPluginPath = null,
        string? applicationBaseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPluginPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPluginPath.Trim()));
        }

        var bundled = Path.Combine(
            applicationBaseDirectory ?? AppContext.BaseDirectory,
            "native",
            "gstreamer",
            "lib",
            "gstreamer-1.0");
        return Directory.Exists(bundled) ? bundled : null;
    }

    public static string BundledRoot(string? applicationBaseDirectory = null)
        => Path.Combine(applicationBaseDirectory ?? AppContext.BaseDirectory, "native", "gstreamer");

    private static IEnumerable<string> CandidateDirectories(
        string? configuredBinPath,
        string? applicationBaseDirectory)
    {
        var seen = new HashSet<string>(PathComparer());

        foreach (var candidate in ExpandRoot(configuredBinPath))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (var candidate in ExpandRoot(Path.Combine(BundledRoot(applicationBaseDirectory), "bin")))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (var variable in RootEnvironmentVariables)
        {
            foreach (var candidate in ExpandRoot(Environment.GetEnvironmentVariable(variable)))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(entry);
            }
            catch
            {
                continue;
            }

            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static IEnumerable<string> ExpandRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        string root;
        try
        {
            root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        }
        catch
        {
            yield break;
        }

        yield return root;
        yield return Path.Combine(root, "bin");
    }

    private static string ExecutableName(string baseName)
        => OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;

    private static StringComparer PathComparer()
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
