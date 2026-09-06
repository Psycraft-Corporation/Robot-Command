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
        Assert.Contains("GeometryName", operate);
        Assert.Contains("GeometrySave", operate);
        Assert.Contains("CompleteGeometryEditCommand=\"{Binding Map.CompleteGeometryEditCommand}\"", operate);
        Assert.Contains("GeometrySelectionEnabled", operate);
        Assert.Contains("IsVisible=\"{Binding Map.HasGeometryDraft}\"", operate);
        Assert.DoesNotContain("BeginGeometryRenameCommand", operate);
        Assert.DoesNotContain("ApplyGeometryRenameCommand", operate);
        var layersButton = operate.IndexOf("Content=\"{loc:Loc Key=MapLayers}\"", StringComparison.Ordinal);
        var layersFlyoutEnd = operate.IndexOf("</Button.Flyout>", layersButton, StringComparison.Ordinal);
        Assert.True(layersButton >= 0);
        Assert.True(layersFlyoutEnd > layersButton);
        Assert.DoesNotContain("MapMissionPreviewLayers", operate[layersButton..layersFlyoutEnd]);
        Assert.Contains("MapMissionPreviews", operate[layersFlyoutEnd..]);

        var mapControl = operate.IndexOf("<controls:NativeOperationalMapControl", StringComparison.Ordinal);
        var geometryToolbar = operate.IndexOf("ToolTip.Tip=\"{loc:Loc Key=GeometryNewPoi}\"", StringComparison.Ordinal);
        Assert.True(mapControl >= 0);
        Assert.True(geometryToolbar > mapControl,
            "The Geometry authoring toolbar must be a left-side map overlay, not part of the top map toolbar.");
    }

    [Fact]
    public void GeometryAuthoring_UsesInlineNameAndRequiresExplicitSave()
    {
        var root = FindRepositoryRoot();
        var map = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "ViewModels", "OperationalMapViewModel.cs"));

        Assert.Contains("CompleteGeometryEditAsync", map);
        Assert.Contains("SaveAsync(", map);
        Assert.DoesNotContain("QueueGeometryAutosave", map);
        Assert.DoesNotContain("PersistGeometryDraftAsync", map);
        Assert.DoesNotContain("BeginGeometryRename", map);
        Assert.DoesNotContain("ApplyGeometryRename", map);
    }

    [Fact]
    public void FlightMissionAuthoring_UsesPerStepBindingInsteadOfGenericGeometryAdd()
    {
        var root = FindRepositoryRoot();
        var mission = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "FlightMissionView.axaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "ViewModels", "FlightMissionViewModel.cs"));

        Assert.Contains("SelectedStepGeometry", mission);
        Assert.Contains("SelectedStepGeometryOptions", mission);
        Assert.Contains("SelectionBoxItemTemplate", mission);
        Assert.Contains("PlaceholderText=\"{loc:Loc Key=GeometrySelect}\"", mission);
        Assert.Contains("Classes=\"compactList\"", mission);
        Assert.DoesNotContain("FlightMissionNewStepGeometry", mission);
        Assert.DoesNotContain("SelectedItem=\"{Binding SelectedGeometry}\"", mission);
        Assert.DoesNotContain("SelectedGeometry?.Kind", viewModel);
        Assert.DoesNotContain("if (SelectedGeometry is", viewModel);
        Assert.Contains("Text=\"{loc:Loc Key=FlightMissionLoiterSeconds}\"", mission);
        Assert.Contains("Grid.Column=\"5\" Value=\"{Binding LoiterSeconds", mission);
        var addStep = mission.IndexOf("FlightMissionAddStep", StringComparison.Ordinal);
        var selectedStep = mission.IndexOf("FlightMissionSelectedStep", StringComparison.Ordinal);
        var stepGeometry = mission.IndexOf("FlightMissionStepGeometry", StringComparison.Ordinal);
        Assert.True(addStep >= 0 && selectedStep > addStep && stepGeometry > selectedStep);
        Assert.DoesNotContain("Text=\"{loc:Loc Key=FlightMissionMission}\"", mission);
        Assert.DoesNotContain("FlightMissionAddGeometry", mission);
        Assert.DoesNotContain("AddGeometryCommand", viewModel);
        Assert.DoesNotContain("Mission step added.", viewModel);
        Assert.Contains("ConfirmDeleteCommand", viewModel);
        Assert.Contains("DeleteMissionOnlyCommand", viewModel);
        Assert.Contains("CancelDeleteCommand", viewModel);
        Assert.Contains("Content=\"{loc:Loc Key=FlightMissionCancel}\" Command=\"{Binding CancelDeleteCommand}\"", mission);
        Assert.Contains("Delete associated geometry", File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Resources", "Strings.resx")));
        Assert.Contains("DeleteConflictMessage", mission);

        var geometryLibrary = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "ViewModels", "GeometryLibraryViewModel.cs"));
        Assert.DoesNotContain("Confirm deletion of local geometry", geometryLibrary);
    }

    [Fact]
    public void FlightMissionAuthoring_ExposesCameraActionsAtMissionAndStepScopes()
    {
        var root = FindRepositoryRoot();
        var mission = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "FlightMissionView.axaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "ViewModels", "FlightMissionViewModel.cs"));

        Assert.Contains("MissionStartCameraActions", mission);
        Assert.Contains("AddMissionStartCameraActionCommand", mission);
        Assert.Contains("SelectedStepCameraActions", mission);
        Assert.Contains("AddSelectedStepCameraActionCommand", mission);
        Assert.Contains("FlightMissionNoCameraActions", mission);
        Assert.Contains("CameraPlanSummary", mission);
        Assert.DoesNotContain("CaptureEvents", mission);
        Assert.DoesNotContain("AddCameraIntentCommand", mission);
        Assert.Contains("SetMissionCameraActionsAsync", viewModel);
        Assert.Contains("SetStepCameraActionsAsync", viewModel);
        Assert.Contains("CameraActionKinds", viewModel);
        Assert.Contains("FlightMissionExecutionRouteItem", viewModel);
        Assert.Contains("FlightMissionExecutionUnconfirmed", viewModel);
        Assert.Contains("FlightMissionExecutionFailsafe", viewModel);
        Assert.Contains("ColumnDefinitions=\"125,125,145,110,110\"", mission);
        Assert.Contains("Width=\"120\" Value=\"{Binding CorridorWidth", mission);
        Assert.Contains("FlightMissionCorridorFrontLap", mission);
        Assert.Contains("FlightMissionCorridorImagesInTurns", mission);
        Assert.Contains("FlightMissionAutomaticPhotoCapture", mission);
        Assert.Contains("AutomaticPhotoCaptureEnabled", mission);
        Assert.Contains("HasStandaloneStatus", mission);
        Assert.Contains("HasVisiblePendingOperation", mission);
        Assert.Contains("HasVisibleExecutionStatus", mission);
        Assert.Contains("CanPauseMission", mission);
        Assert.Contains("CanContinueMission", mission);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", mission);
        Assert.Contains("Uploading...", viewModel);
        Assert.DoesNotContain("Uploading mission to the drone...", viewModel);
        Assert.DoesNotContain("CameraCaptureModes", mission);
        Assert.DoesNotContain("CameraCaptureMode", mission);
    }

    [Fact]
    public void OperateMissionPreview_UsesAuthoringPreviewPaletteAndDirectionalTrace()
    {
        var root = FindRepositoryRoot();
        var map = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "NativeOperationalMapControl.cs"));

        Assert.Contains("missionPreview ? 1.25", map);
        Assert.Contains("MissionPreviewAccentHex", map);
        Assert.Contains("MapsuiColor.FromString(\"#F6C453\")", map);
        Assert.Contains("BuildMissionPreviewArrows(geometry, viewport)", map);
        Assert.Contains("MissionPreviewArrowOverlayControl", map);
        Assert.DoesNotContain("MapsuiColor.FromString(\"#A78BFA\")", map);
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
