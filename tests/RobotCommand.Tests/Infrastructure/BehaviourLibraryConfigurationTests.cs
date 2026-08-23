using RobotCommand.Services;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourLibraryConfigurationTests
{
    [Fact]
    public void Load_UsesAutonomyBehaviourLibrarySettings()
    {
        var root = TemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "appsettings.local.json"),
                """
                {
                  "autonomy": {
                    "behaviourLibraryPath": "autonomy/behaviours",
                    "maximumBehaviourPackageBytes": 12582912,
                    "maximumBehaviourPackageFiles": 240
                  }
                }
                """);

            var configuration = AppConfiguration.Load(root);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "autonomy/behaviours")),
                configuration.BehaviourLibraryPath);
            Assert.Equal(12 * 1024 * 1024, configuration.MaximumBehaviourPackageBytes);
            Assert.Equal(240, configuration.MaximumBehaviourPackageFiles);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void DefaultPath_UsesAutonomyBehaviourHierarchy()
    {
        var root = TemporaryDirectory();
        try
        {
            var configuration = AppConfiguration.Load(root);

            Assert.EndsWith(
                Path.Combine("Psycraft", "Robot Command", "Autonomy", "Behaviours"),
                configuration.BehaviourLibraryPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "logos-behaviour-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
