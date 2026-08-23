using System.Xml.Linq;

using Xunit;

namespace RobotCommand.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void RenderingProjectOwnsMeshPipelineWithoutPlatformReferences()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "rendering", "RobotCommand.Rendering", "RobotCommand.Rendering.csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .ToArray();

        Assert.Contains(references, value => value!.Contains("RobotCommand.Core", StringComparison.Ordinal));
        Assert.DoesNotContain(references, value => value!.Contains("Runtime", StringComparison.Ordinal));
        Assert.DoesNotContain(references, value => value!.Contains("Avalonia", StringComparison.Ordinal));
        Assert.DoesNotContain(references, value => value!.Contains("Veldrid", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(root, "src", "rendering", "RobotCommand.Rendering", "Meshes", "MeshAssetLoader.cs")));
        Assert.False(File.Exists(Path.Combine(root, "src", "RobotCommand.Rendering.Meshes", "RobotCommand.Rendering.Meshes.csproj")));
        Assert.False(File.Exists(Path.Combine(root, "src", "RobotCommand.Rendering.Avalonia", "RobotCommand.Rendering.Avalonia.csproj")));
    }

    [Fact]
    public void SolutionHasNoStaleMeshProjectReference()
    {
        var root = FindRepositoryRoot();
        var solution = File.ReadAllText(Path.Combine(root, "RobotCommand.sln"));

        Assert.DoesNotContain("RobotCommand.Rendering.Meshes.csproj", solution, StringComparison.Ordinal);

        var projectFiles = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains("\\artifacts\\", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(12, projectFiles.Length);
        Assert.DoesNotContain(projectFiles, path => path.Contains("RobotCommand.Rendering.Meshes", StringComparison.Ordinal));
        Assert.DoesNotContain(projectFiles, path => path.Contains("RobotCommand.Rendering.Avalonia", StringComparison.Ordinal));
        Assert.Contains(projectFiles, path => path.EndsWith(Path.Combine("Rendering.Veldrid", "RobotCommand.Rendering.Veldrid.csproj"), StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(root, "sdks", "dart")));
        Assert.False(File.Exists(Path.Combine(root, "src", "RobotCommand.Sdk.Dart", "pubspec.yaml")));
    }

    [Fact]
    public void SimulationAndWorkerRemainSeparatePortableBoundaries()
    {
        var root = FindRepositoryRoot();
        var simulationProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "simulation",
            "RobotCommand.Simulation",
            "RobotCommand.Simulation.csproj"));
        var simulatorProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "simulation",
            "RobotCommand.Simulator",
            "RobotCommand.Simulator.csproj"));

        Assert.Contains("RobotCommand.Core", simulationProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", simulationProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows", simulationProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<OutputType>Exe</OutputType>", simulatorProject, StringComparison.Ordinal);
        Assert.Contains("RobotCommand.Simulation", simulatorProject, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
