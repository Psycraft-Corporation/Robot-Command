using RobotCommand.Controls;
using RobotCommand.Services.Maps;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class QuickRunViewModelSurfaceTests
{
    [Fact]
    public void DeveloperPreviewVehicleOperationsPanel_ExcludesUnfinishedRunComposition()
    {
        var root = FindRepositoryRoot();
        var panel = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "QuickRunPanel.axaml"));

        Assert.Contains("x:DataType=\"vm:OperatorControlsViewModel\"", panel);
        Assert.Contains("Content=\"Arm\"", panel);
        Assert.Contains("Content=\"Takeoff\"", panel);
        Assert.Contains("Execute command", panel);
        Assert.Contains("PendingCommandPanel", panel);
        Assert.Contains("HasRejectedCommand", panel);
        Assert.Contains("DismissRejectedCommand", panel);
        Assert.Contains("ToggleRejectedCommand", panel);
        Assert.Contains("RejectedCommandSummary", panel);
        Assert.Contains("RejectedCommandMessage", panel);
        Assert.Contains("MaxHeight=\"180\"", panel);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", panel);
        Assert.Contains("commandExecuteReady", panel);
        Assert.Contains("commandExecuteBlocked", panel);
        Assert.Contains("TogglePendingFindingsCommand", panel);
        Assert.Contains("ShowPendingFindings", panel);
        Assert.Contains("ParameterValidationMessage", panel);
        Assert.Contains("VerticalDistanceUnitSuffix", panel);
        Assert.DoesNotContain("Content=\"Queue", panel);
        Assert.DoesNotContain("Vehicle operations are submitted", panel);
        Assert.DoesNotContain("Logos, MAVLink, or simulated backend", panel);
        Assert.Contains("HorizontalContentAlignment=\"Center\"", panel);
        Assert.Contains("Text=\"&#176;\"", panel);
        Assert.DoesNotContain("OPERATIONS", panel);
        Assert.DoesNotContain("<Expander", panel);
        Assert.DoesNotContain("Reason for command", panel);
        Assert.DoesNotContain("Queue Â· review Â· execute", panel);
        Assert.DoesNotContain("PointerPressed=", panel);
        Assert.DoesNotContain("Target vehicle", panel);
        Assert.DoesNotContain("Installed behaviour", panel);
        Assert.DoesNotContain("Behaviour parameters", panel);
        Assert.DoesNotContain("Prepare\"", panel);
        Assert.DoesNotContain("Launch\"", panel);
        Assert.DoesNotContain("QuickRunViewModel", panel);
    }

    [Fact]
    public void OperatorControls_ExposeTheFullVehicleOperationSet()
    {
        var type = typeof(OperatorControlsViewModel);

        Assert.NotNull(type.GetProperty("PrepareArmCommand"));
        Assert.NotNull(type.GetProperty("PrepareDisarmCommand"));
        Assert.NotNull(type.GetProperty("PrepareTakeoffCommand"));
        Assert.NotNull(type.GetProperty("PrepareLandCommand"));
        Assert.NotNull(type.GetProperty("PrepareRecoverCommand"));
        Assert.NotNull(type.GetProperty("PrepareGoToCommand"));
        Assert.NotNull(type.GetProperty("PrepareChangeAltitudeCommand"));
        Assert.NotNull(type.GetProperty("PrepareSetHeadingCommand"));
        Assert.NotNull(type.GetProperty("PrepareMapGoToCommand"));
        Assert.NotNull(type.GetProperty("PrepareMapSetHeadingCommand"));
        Assert.NotNull(type.GetProperty("PrepareMapAssembleCommand"));
        Assert.NotNull(type.GetProperty("ExecuteQueuedCommandsCommand"));
        Assert.NotNull(type.GetProperty("ClearQueuedCommandsCommand"));
        Assert.NotNull(type.GetProperty("CancelExecutingCommandsCommand"));
        Assert.NotNull(type.GetProperty("SelectedQueuedAction"));
        Assert.NotNull(type.GetProperty("HasExecutingCommand"));
        Assert.NotNull(type.GetProperty("QueuedPlans"));
        Assert.NotNull(type.GetProperty("QueuedGoToTargets"));
        Assert.NotNull(type.GetProperty("ConfirmationMatches"));
        Assert.NotNull(type.GetProperty("ConfirmationPrompt"));
        Assert.NotNull(type.GetProperty("TakeoffAltitudeText"));
        Assert.NotNull(type.GetProperty("AltitudeTargetText"));
        Assert.NotNull(type.GetProperty("HeadingTargetText"));
        Assert.NotNull(type.GetProperty("ParameterValidationMessage"));
        Assert.NotNull(type.GetProperty("PendingExecutionReady"));
        Assert.NotNull(type.GetProperty("PendingExecutionBlocked"));
        Assert.NotNull(type.GetProperty("ShowPendingFindings"));
        Assert.NotNull(type.GetProperty("TogglePendingFindingsCommand"));
        Assert.NotNull(type.GetProperty("IsRejectedCommandExpanded"));
        Assert.NotNull(type.GetProperty("RejectedCommandSummary"));
        Assert.NotNull(type.GetProperty("RejectedCommandToggleText"));
        Assert.NotNull(type.GetProperty("ToggleRejectedCommand"));
    }

    [Fact]
    public void OperationalMap_ExposesAnInMapOperatorConfirmationSurface()
    {
        var type = typeof(NativeOperationalMapControl);

        Assert.NotNull(type.GetProperty("MapGoToCommand"));
        Assert.NotNull(type.GetProperty("MapSetHeadingCommand"));
        Assert.NotNull(type.GetProperty("MapOperatorControls"));
    }

    [Fact]
    public void OperatorSurfacesUseQueueThenExecuteLanguage()
    {
        var root = FindRepositoryRoot();
        var operate = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml"));
        var quickRun = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "QuickRunPanel.axaml"));
        var units = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Shell", "UnitsPanelView.axaml"));

        Assert.Contains("MapOperatorControls=\"{Binding Controls}\"", operate);
        Assert.DoesNotContain("<local:QuickRunPanel", operate);
        Assert.DoesNotContain("SAVED VIEWS", operate);
        Assert.Contains("MapViews", operate);
        Assert.Contains("MapLayers", operate);
        Assert.DoesNotContain("DataStatus", operate);
        Assert.Contains("Execute command", quickRun);
        Assert.Contains("Key=ExecuteCommand", units);
        Assert.Contains("Key=ExecuteAllCommands", units);
        Assert.Contains("Key=ClearAllQueuedCommands", units);
        Assert.Contains("Key=ClearCommandQueue", units);
        Assert.DoesNotContain("Confirm command", quickRun);
        Assert.Contains("Grid.ColumnSpan=\"2\"", quickRun);
    }

    [Fact]
    public void OperateInspector_DocksUnitsAndOperationsOutsideTheMapSurface()
    {
        var root = FindRepositoryRoot();
        var inspector = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Shell", "OperateInspectorView.axaml"));
        var shell = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "MainWindow.axaml"));

        Assert.DoesNotContain("TabControl", inspector);
        Assert.Contains("GridSplitter", inspector);
        Assert.Contains("RowDefinitions=\"1.6*,8,*\"", inspector);
        Assert.Contains("InspectorOperations", inspector);
        Assert.Contains("UnitsPanelView", inspector);
        Assert.Contains("QuickRunPanel", inspector);
        Assert.Contains("OperateInspectorView", shell);
        Assert.DoesNotContain("RightRailSplitterWidth", shell);
    }

    [Fact]
    public void OperationalMap_AcceptsTheRememberedViewportFromTheOperateWorkspace()
    {
        var type = typeof(NativeOperationalMapControl);
        Assert.NotNull(type.GetProperty("CurrentViewport"));

        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(
            root,
            "src",
            "app",
            "RobotCommand",
            "Views",
            "Workspaces",
            "OperateView.axaml"));
        Assert.Contains("CurrentViewport=\"{Binding Map.CurrentViewport}\"", view);
    }

    [Theory]
    [InlineData(43.65, -79.38, 43.65, -79.38, 0)]
    [InlineData(43.65, -79.38, 44.65, -79.38, 0)]
    [InlineData(43.65, -79.38, 43.65, -78.38, 90)]
    [InlineData(43.65, -79.38, 42.65, -79.38, 180)]
    public void MapCommandBearing_IsCompassHeading(
        double originLatitude,
        double originLongitude,
        double targetLatitude,
        double targetLongitude,
        double expected)
    {
        Assert.Equal(expected, MapCommandMath.BearingDegrees(
            originLatitude, originLongitude, targetLatitude, targetLongitude), 0);
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
