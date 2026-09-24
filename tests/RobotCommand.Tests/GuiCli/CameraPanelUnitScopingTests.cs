using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class CameraPanelUnitScopingTests
{
    [Fact]
    public void BuildUnitCameraOptions_ReturnsNoSourcesWithoutASelectedUnit()
    {
        var cameraSources = new[]
        {
            Camera("unassociated-camera", "zr10", "sik"),
            Camera("unassociated-video", "hm30", "media:hm30")
        };

        Assert.Empty(CameraPanelViewModel.BuildUnitCameraOptions(cameraSources, null));
    }

    [Fact]
    public void BuildUnitCameraOptions_IncludesOnlyOwningGhostCameraWithoutSavedUnit()
    {
        var owned = Camera("ghost-camera", "ghost-camera-1", "ghost-connection-1");
        var unrelated = Camera("other-camera", "other-camera", "ghost-connection-2");
        var ghost = new VehicleRecord("ghost-1", "Ghost 1", ["ghost-connection-1"], null, null,
            "Multicopter", "Air", "ghost", AvailabilityState.Online, IsGhost: true);

        var options = CameraPanelViewModel.BuildUnitCameraOptions([owned, unrelated], null, ghost);

        Assert.Same(owned, Assert.Single(options));
    }

    [Fact]
    public void BuildUnitCameraOptions_UsesOnlyTheUnitsControlAndMediaBindings()
    {
        var controlCamera = Camera("zr10-device", "zr10-camera", "dracula-sik", supportsGimbal: true, supportsPhoto: true);
        var mainVideo = Camera("hm30-main", "hm30-main", "media:hm30-main");
        var standbyVideo = Camera("hm30-standby", "hm30-standby", "media:hm30-standby");
        var unrelated = Camera("unassigned", "other-camera", "media:unassigned");
        var unit = Unit(
            cameraSources: [new UnitCameraSourceBinding("dracula-sik", "zr10-camera")],
            cameras:
            [
                new UnitCameraDeviceBinding(
                    "zr10",
                    "Dracula ZR10",
                    "dracula-sik",
                    "zr10-camera",
                    "hm30-main",
                    ["hm30-standby"])
            ]);

        var options = CameraPanelViewModel.BuildUnitCameraOptions(
            [controlCamera, mainVideo, standbyVideo, unrelated],
            unit);

        Assert.Equal(2, options.Count);
        Assert.Contains(options, camera => camera.Id == mainVideo.Id && camera.Name == "Dracula ZR10" && camera.SupportsGimbal && camera.SupportsPhoto);
        Assert.Contains(options, camera => camera.Id == standbyVideo.Id && camera.Name == "Dracula ZR10" && camera.SupportsGimbal && camera.SupportsPhoto);
        Assert.DoesNotContain(options, camera => camera.Id == controlCamera.Id || camera.Id == unrelated.Id);
    }

    [Fact]
    public void BuildUnitCameraOptions_IncludesExplicitlyAssociatedControlOnlyCamera()
    {
        var associated = Camera("bound-control", "zr10-camera", "dracula-sik");
        var unrelated = Camera("unbound-control", "other-camera", "dracula-sik");
        var unit = Unit([new UnitCameraSourceBinding("dracula-sik", "zr10-camera")], cameras: []);

        var options = CameraPanelViewModel.BuildUnitCameraOptions([associated, unrelated], unit);

        Assert.Same(associated, Assert.Single(options));
    }

    [Fact]
    public void StreamRemainsOpenForSelectionRefreshOfTheSameUnit()
    {
        Assert.False(CameraPanelViewModel.ShouldCloseStreamForSelection("ghost-1", ["ghost-1"]));
        Assert.True(CameraPanelViewModel.ShouldCloseStreamForSelection("ghost-1", []));
        Assert.True(CameraPanelViewModel.ShouldCloseStreamForSelection("ghost-1", ["dracula"]));
        Assert.True(CameraPanelViewModel.ShouldCloseStreamForSelection("ghost-1", ["ghost-1", "dracula"]));
    }

    [Fact]
    public void OperateView_HidesCameraWorkspaceUnlessAUnitIsSelected()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "ViewModels", "OperateViewModel.cs"));

        Assert.Contains("IsVisible=\"{Binding CameraPaneVisible}\"", view);
        Assert.Contains("No video source is configured for this unit.", File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "ViewModels", "CameraPanelViewModel.cs")));
        Assert.Contains("CameraPaneVisible => VideoVisible && Camera.HasSelectedUnit", viewModel);
    }

    [Fact]
    public void OperateView_PrioritizesVideoAndKeepsCameraControlsBelowPlayback()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml"));
        var cameraStart = view.IndexOf("<ScrollViewer Grid.Column=\"2\"", StringComparison.Ordinal);
        var cameraEnd = view.IndexOf("<Border Grid.Column=\"0\"", cameraStart, StringComparison.Ordinal);
        Assert.True(cameraStart >= 0 && cameraEnd > cameraStart);
        var cameraView = view[cameraStart..cameraEnd];
        var sourceControls = cameraView.IndexOf("ItemsSource=\"{Binding Camera.Cameras}\"", StringComparison.Ordinal);
        var video = cameraView.IndexOf("NativeVideoSurface", StringComparison.Ordinal);
        var playback = cameraView.IndexOf("LocalVideoTimelineControl", StringComparison.Ordinal);
        var gimbal = cameraView.IndexOf("GimbalCameraControls", StringComparison.Ordinal);

        Assert.True(sourceControls >= 0 && sourceControls < video);
        Assert.True(video < playback && playback < gimbal);
        Assert.Contains("ColumnDefinitions=\"Auto,96,Auto,96\"", cameraView);
        Assert.DoesNotContain("Commands are queued for operator review and execution", cameraView);
        Assert.DoesNotContain("Camera.TrackSummary", cameraView);
        Assert.DoesNotContain("No perception tracks", cameraView);
        Assert.DoesNotContain("LIVE EDGE", cameraView);
        Assert.DoesNotContain("Camera.RecordingStorageText", cameraView);
        Assert.DoesNotContain("Camera.NativeVideoStatus", cameraView);
        Assert.DoesNotContain("Camera.NativeVideoMetrics", cameraView);
        Assert.DoesNotContain("FontSize=\"9\"", cameraView);
        Assert.DoesNotContain("FontSize=\"10\"", cameraView);
    }

    [Fact]
    public void OperateCameraActionsUseTheSharedQueueWithoutDuplicateButtons()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml"));

        Assert.Contains("Command=\"{Binding Controls.PrepareCapturePhotoCommand}\"", view);
        Assert.Contains("Command=\"{Binding Controls.PrepareStartVideoCommand}\"", view);
        Assert.Contains("Command=\"{Binding Controls.PrepareStopVideoCommand}\"", view);
        Assert.Contains("Command=\"{Binding Controls.PrepareCenterGimbalCommand}\"", view);
        Assert.Contains("Command=\"{Binding Controls.PrepareNadirGimbalCommand}\"", view);
        Assert.DoesNotContain("Camera.CapturePhotoCommand", view);
        Assert.DoesNotContain("Camera.StartRemoteVideoCommand", view);
        Assert.DoesNotContain("Camera.StopRemoteVideoCommand", view);
        Assert.DoesNotContain("Camera.CenterGimbalCommand", view);
        Assert.DoesNotContain("Content=\"Queue photo\"", view);
    }

    private static UnitDefinitionSnapshot Unit(
        IReadOnlyList<UnitCameraSourceBinding> cameraSources,
        IReadOnlyList<UnitCameraDeviceBinding> cameras)
        => new(
            "dracula-unit",
            "Dracula",
            [new UnitVehicleSourceBinding("dracula-sik", "dracula")],
            cameraSources,
            "dracula-sik",
            "dracula-sik",
            "dracula-sik",
            DateTimeOffset.UtcNow,
            [],
            ["dracula-sik", "hm30"],
            cameras);

    private static CameraSourceRecord Camera(
        string id,
        string sourceId,
        string connectionId,
        bool supportsGimbal = false,
        bool supportsPhoto = false)
        => new(
            id,
            sourceId,
            connectionId,
            null,
            id,
            "camera",
            AvailabilityState.Online,
            "Ready",
            "Ready",
            true,
            true,
            false,
            0,
            0,
            0,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow,
            supportsPhoto,
            SupportsGimbal: supportsGimbal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("RobotCommand.sln was not found.");
    }
}
