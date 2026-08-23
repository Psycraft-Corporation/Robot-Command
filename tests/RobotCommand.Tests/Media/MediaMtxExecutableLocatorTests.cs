using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MediaMtxExecutableLocatorTests
{
    [Fact]
    public void Locate_PrefersConfiguredExecutable()
    {
        var root = CreateTempDirectory();
        var configured = Path.Combine(root, OperatingSystem.IsWindows() ? "custom.exe" : "custom");
        File.WriteAllText(configured, string.Empty);

        var result = MediaMtxExecutableLocator.Locate(configured, root);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(configured), result.Path);
    }

    [Fact]
    public void Locate_FindsPackagedRuntime()
    {
        var root = CreateTempDirectory();
        var directory = Path.Combine(root, "native", "mediamtx");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, OperatingSystem.IsWindows() ? "mediamtx.exe" : "mediamtx");
        File.WriteAllText(path, string.Empty);

        var result = MediaMtxExecutableLocator.Locate(null, root);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(path), result.Path);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "RobotCommand-media-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

