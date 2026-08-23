using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GStreamerExecutableLocatorTests
{
    [Fact]
    public void Locate_AcceptsConfiguredBinDirectory()
    {
        var root = CreateTempDirectory();
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        var launch = Path.Combine(bin, ExecutableName("gst-launch-1.0"));
        var inspect = Path.Combine(bin, ExecutableName("gst-inspect-1.0"));
        File.WriteAllText(launch, string.Empty);
        File.WriteAllText(inspect, string.Empty);

        var result = GStreamerExecutableLocator.Locate(bin);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(launch), result.Launch);
        Assert.Equal(Path.GetFullPath(inspect), result.Inspect);
    }

    [Fact]
    public void Locate_AcceptsConfiguredInstallRootContainingBin()
    {
        var root = CreateTempDirectory();
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, ExecutableName("gst-launch-1.0")), string.Empty);
        File.WriteAllText(Path.Combine(bin, ExecutableName("gst-inspect-1.0")), string.Empty);

        var result = GStreamerExecutableLocator.Locate(root);

        Assert.NotNull(result);
        Assert.Equal(bin, Path.GetDirectoryName(result.Launch));
    }

    [Fact]
    public void Locate_PrefersConfiguredPathOverBundledRuntime()
    {
        var app = CreateTempDirectory();
        var bundledBin = Path.Combine(app, "native", "gstreamer", "bin");
        var configuredBin = Path.Combine(app, "configured");
        Directory.CreateDirectory(bundledBin);
        Directory.CreateDirectory(configuredBin);
        WriteExecutables(bundledBin);
        WriteExecutables(configuredBin);

        var result = GStreamerExecutableLocator.Locate(configuredBin, app);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(configuredBin), Path.GetDirectoryName(result.Launch));
    }

    [Fact]
    public void Locate_FindsBundledRuntimeWhenConfigurationIsEmpty()
    {
        var app = CreateTempDirectory();
        var bundledBin = Path.Combine(app, "native", "gstreamer", "bin");
        Directory.CreateDirectory(bundledBin);
        WriteExecutables(bundledBin);

        var result = GStreamerExecutableLocator.Locate(null, app);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(bundledBin), Path.GetDirectoryName(result.Launch));
    }

    [Fact]
    public void ResolvePluginPath_UsesBundledPluginDirectoryWhenPresent()
    {
        var app = CreateTempDirectory();
        var pluginPath = Path.Combine(app, "native", "gstreamer", "lib", "gstreamer-1.0");
        Directory.CreateDirectory(pluginPath);

        var result = GStreamerExecutableLocator.ResolvePluginPath(null, app);

        Assert.Equal(Path.GetFullPath(pluginPath), result);
    }

    private static void WriteExecutables(string directory)
    {
        File.WriteAllText(Path.Combine(directory, ExecutableName("gst-launch-1.0")), string.Empty);
        File.WriteAllText(Path.Combine(directory, ExecutableName("gst-inspect-1.0")), string.Empty);
    }

    private static string ExecutableName(string baseName)
        => OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Logos-robot-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
