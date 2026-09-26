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
        Assert.Contains("CameraPaneVisible => VideoVisible && Camera.HasSelectedUnit && VideoGrid?.IsVisible != true", viewModel);
        Assert.Contains("VideoGridVisible => VideoVisible && VideoGrid?.IsVisible == true", viewModel);
    }

    [Fact]
    public async Task MultiUnitGrid_ShowsFiveTilesAndUsesExplicitOverflowSwap()
    {
        var selection = new RobotCommand.State.SelectionService();
        var vehicles = new RobotCommand.State.EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var sources = new RobotCommand.State.EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal);
        var streams = new RobotCommand.State.EntityStore<string, CameraStreamRecord>(item => item.Id, StringComparer.Ordinal);
        for (var index = 0; index < 7; index++)
            vehicles.Upsert(new VehicleRecord($"unit-{index}", $"Unit {index}", [], null, null,
                "Multicopter", "Air", "test", AvailabilityState.Online));
        selection.SetUnitSelection(Enumerable.Range(0, 7)
            .Select(index => new OperationalSelection(SelectionKind.Vehicle, $"unit-{index}", $"Unit {index}", "", []))
            .ToArray());

        using var grid = new MultiUnitVideoGridViewModel(selection, vehicles, sources, streams,
            null!, null!, null!, null!);

        Assert.True(grid.IsVisible);
        Assert.Equal(5, grid.Tiles.Count);
        Assert.Equal(["unit-0", "unit-1", "unit-2", "unit-3", "unit-4"], grid.Tiles.Select(tile => tile.UnitId));
        Assert.Equal(["unit-5", "unit-6"], grid.OverflowUnits.Select(unit => unit.Id));
        Assert.All(grid.Tiles, tile => Assert.Equal("No source", tile.Status));

        var firstTile = grid.Tiles[0];
        firstTile.FocusCommand.Execute(null);
        Assert.Same(firstTile, grid.FocusedTile);
        firstTile.SelectedSwapUnitId = "unit-6";
        grid.SwapTileCommand.Execute(firstTile);
        await EventuallyAsync(() => firstTile.UnitId == "unit-6");

        Assert.Null(grid.FocusedTile);
        Assert.Equal("unit-6", grid.Tiles[0].UnitId);
        Assert.Contains(grid.OverflowUnits, unit => unit.Id == "unit-0");

        selection.SetUnitSelection([
            new OperationalSelection(SelectionKind.Vehicle, "unit-1", "Unit 1", "", []),
            new OperationalSelection(SelectionKind.Vehicle, "unit-2", "Unit 2", "", [])]);
        await EventuallyAsync(() => grid.Tiles.Count == 2 && grid.Tiles.All(tile => tile.UnitId is "unit-1" or "unit-2"));
        Assert.Null(grid.FocusedTile);
        Assert.Empty(grid.OverflowUnits);

        selection.Clear();
        Assert.False(grid.IsVisible);
        await EventuallyAsync(() => grid.Tiles.Count == 0);
    }

    [Fact]
    public void MultiUnitGrid_ResolvesEachGhostsBuiltInCameraWithoutAVideoRoute()
    {
        var selection = new RobotCommand.State.SelectionService();
        var vehicles = new RobotCommand.State.EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var sources = new RobotCommand.State.EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal);
        var streams = new RobotCommand.State.EntityStore<string, CameraStreamRecord>(item => item.Id, StringComparer.Ordinal);
        for (var index = 1; index <= 2; index++)
        {
            var connectionId = $"ghost-connection-{index}";
            vehicles.Upsert(new VehicleRecord($"ghost-{index}", $"Ghost {index}", [connectionId], null, null,
                "Multicopter", "Air", "ghost", AvailabilityState.Online, IsGhost: true));
            sources.Upsert(Camera($"ghost-camera-{index}", $"ghost-camera-{index}", connectionId));
        }
        selection.SetUnitSelection(Enumerable.Range(1, 2)
            .Select(index => new OperationalSelection(SelectionKind.Vehicle, $"ghost-{index}", $"Ghost {index}", "", []))
            .ToArray());

        using var grid = new MultiUnitVideoGridViewModel(selection, vehicles, sources, streams,
            null!, null!, null!, null!);

        Assert.Equal(2, grid.Tiles.Count);
        foreach (var (tile, index) in grid.Tiles.Select((tile, index) => (tile, index)))
        {
            Assert.Equal($"ghost-camera-{index + 1}", tile.Camera?.Id);
            Assert.Equal("Closed", tile.Status);
            Assert.True(tile.OpenCommand.CanExecute(null));
        }
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

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
