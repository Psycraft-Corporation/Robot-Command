using Xunit;

namespace RobotCommand.Tests;

public sealed class DeveloperPreviewSurfaceTests
{
    [Fact]
    public void DesktopNavigation_ExposesSupportedDeveloperPreviewWorkspaces()
    {
        var root = FindRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "ViewModels", "ShellViewModel.cs"));
        var templates = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "App.axaml"));

        Assert.Contains("\"autonomy\"", shell);
        Assert.DoesNotContain("\"evidence\"", shell);
        Assert.DoesNotContain("\"geometry\"", shell);
        Assert.DoesNotContain("\"behaviours\"", shell);
        Assert.DoesNotContain("\"missions\"", shell);
        Assert.DoesNotContain("\"tasks\"", shell);
        Assert.Contains("\"history\"", shell);
        Assert.DoesNotContain("\"events\"", shell);
        Assert.DoesNotContain("\"commands\"", shell);
        Assert.Contains("AutonomyWorkspaceViewModel", templates);
        Assert.DoesNotContain("EvidenceLibraryViewModel", templates);
        Assert.Contains("GeometryLibraryViewModel", templates);
        Assert.DoesNotContain("BehaviourLibraryViewModel", templates);
        Assert.DoesNotContain("MissionsViewModel", templates);
        Assert.DoesNotContain("TasksViewModel", templates);
    }

    [Fact]
    public void OperateMap_ExposesLocalGeometryAuthoringActions()
    {
        var root = FindRepositoryRoot();
        var operate = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml"));

        Assert.Contains("GeometryNewPoi", operate);
        Assert.Contains("GeometryNewWaypointSequence", operate);
        Assert.Contains("GeometryNewZone", operate);
        Assert.Contains("GeometryDeleteSelected", operate);
        Assert.Contains("ToolTip.Tip=\"{loc:Loc Key=GeometryNewPoi}\"", operate);
        Assert.DoesNotContain("GeometryToolDescription", operate);
        Assert.DoesNotContain("Shortcuts:", operate);
        Assert.DoesNotContain("Content=\"{loc:Loc Key=GeometryTool}\"", operate);
        Assert.Contains("GeometryRename", operate);
        Assert.Contains("GeometrySave", operate);
        Assert.Contains("CompleteGeometryEditCommand=\"{Binding Map.CompleteGeometryEditCommand}\"", operate);
        Assert.Contains("GeometrySelectionEnabled", operate);

        var mapControl = operate.IndexOf("<controls:NativeOperationalMapControl", StringComparison.Ordinal);
        var geometryToolbar = operate.IndexOf("ToolTip.Tip=\"{loc:Loc Key=GeometryNewPoi}\"", StringComparison.Ordinal);
        Assert.True(mapControl >= 0);
        Assert.True(geometryToolbar > mapControl,
            "The Geometry authoring toolbar must be a left-side map overlay, not part of the top map toolbar.");
    }

    [Fact]
    public void GeometryAuthoring_RequiresAnExplicitSaveInsteadOfAutosavingDrafts()
    {
        var root = FindRepositoryRoot();
        var map = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "ViewModels", "OperationalMapViewModel.cs"));

        Assert.Contains("CompleteGeometryEditAsync", map);
        Assert.Contains("SaveAsync(", map);
        Assert.DoesNotContain("QueueGeometryAutosave", map);
        Assert.DoesNotContain("PersistGeometryDraftAsync", map);
    }

    [Fact]
    public void Runtime_DoesNotStartLegacyRemoteGeometryRegistryForLocalGeometryLibrary()
    {
        var root = FindRepositoryRoot();
        var runtimeHost = File.ReadAllText(Path.Combine(
            root, "src", "runtime", "RobotCommand.Runtime", "Bootstrap", "RobotCommandRuntimeHost.cs"));

        Assert.DoesNotContain("AddHostedService<GeometryRegistrySupervisionService>", runtimeHost);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the RobotCommand repository root.");
    }
}
