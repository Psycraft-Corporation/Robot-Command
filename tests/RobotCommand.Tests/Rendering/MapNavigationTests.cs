using RobotCommand.Models;
using RobotCommand.Services.Maps;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MapNavigationTests
{
    [Fact]
    public void MeanNavigationCoordinate_CentresAGroup()
    {
        var ok = MapNavigationMath.TryMean(
            [
                new MapNavigationCoordinate(-79.40, 43.60),
                new MapNavigationCoordinate(-79.38, 43.64),
                new MapNavigationCoordinate(-79.36, 43.62)
            ],
            out var mean);

        Assert.True(ok);
        Assert.Equal(-79.38, mean.LongitudeDegrees, 6);
        Assert.Equal(43.62, mean.LatitudeDegrees, 6);
    }

    [Fact]
    public void SingleTargetNavigation_UsesBlockScaleResolution()
    {
        Assert.Equal(3, MapNavigationMath.SingleTargetResolution);
    }

    [Fact]
    public void FollowState_CapturesUnitIdsIndependentlyFromLaterSelection()
    {
        var follow = new MapFollowState(["unit-a", "unit-b"], Revision: 7);

        Assert.True(follow.IsGroup);
        Assert.Equal("2 units", follow.Label);
        Assert.Equal(["unit-a", "unit-b"], follow.VehicleIds);
    }

    [Fact]
    public void NavigationRequest_RepresentsGroupFitAndMeanCentre()
    {
        var request = new MapNavigationRequest(
            MapNavigationRequestKind.FitVehicles,
            new MapViewportSnapshot(-79.38, 43.62, 20, 0),
            ["unit-a", "unit-b"]);

        Assert.Equal(MapNavigationRequestKind.FitVehicles, request.Kind);
        Assert.Equal(-79.38, request.Viewport!.LongitudeDegrees, 6);
        Assert.Equal(["unit-a", "unit-b"], request.VehicleIds);
    }

    [Fact]
    public void NavigationSurface_ExposesIndependentFollowAndOperatorActions()
    {
        var type = typeof(IMapNavigationController);
        Assert.NotNull(type.GetProperty(nameof(IMapNavigationController.JumpToSelectedCommand)));
        Assert.NotNull(type.GetProperty(nameof(IMapNavigationController.FollowSelectedCommand)));
        Assert.NotNull(type.GetProperty(nameof(IMapNavigationController.StopFollowingCommand)));
        Assert.NotNull(type.GetProperty(nameof(IMapNavigationController.JumpToOperatorCommand)));
    }

    [Fact]
    public void FollowFilter_IgnoresTelemetryNoiseInsideGeographicDeadband()
    {
        var filter = new MapFollowCameraFilter();
        var start = DateTimeOffset.UtcNow;

        Assert.False(filter.TryUpdate(1, 1000, 2000, 3, start, out _));
        Assert.False(filter.TryUpdate(1, 1001, 2000.5, 3, start.AddMilliseconds(100), out _));
    }

    [Fact]
    public void FollowFilter_UsesHarderCadenceAtWideZoomLevels()
    {
        var filter = new MapFollowCameraFilter();
        var start = DateTimeOffset.UtcNow;

        Assert.False(filter.TryUpdate(1, 0, 0, 100, start, out _));
        Assert.False(filter.TryUpdate(1, 1000, 0, 100, start.AddMilliseconds(250), out _));
        Assert.True(filter.TryUpdate(1, 1000, 0, 100, start.AddMilliseconds(500), out _));

        Assert.True(MapFollowCameraFilter.UpdateInterval(100) >
                    MapFollowCameraFilter.UpdateInterval(3));
        Assert.True(MapFollowCameraFilter.TranslationDeadband(100) >
                    MapFollowCameraFilter.TranslationDeadband(3));
    }

    [Fact]
    public void FollowFilter_TracksSustainedTranslationWithFilteredPrediction()
    {
        var filter = new MapFollowCameraFilter();
        var start = DateTimeOffset.UtcNow;

        Assert.False(filter.TryUpdate(1, 0, 0, 3, start, out _));
        Assert.True(filter.TryUpdate(1, 12, 0, 3, start.AddMilliseconds(50), out var first));
        Assert.True(filter.TryUpdate(1, 24, 0, 3, start.AddMilliseconds(100), out var second));

        Assert.True(first.CenterX > 0);
        Assert.True(second.CenterX > first.CenterX);
        Assert.Equal(0, second.CenterY, 6);
    }

    [Fact]
    public void MapAndUnitsSurfacesExposeNavigationControls()
    {
        var root = FindRepositoryRoot();
        var operate = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml"));
        var units = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Shell", "UnitsPanelView.axaml"));
        var mapControl = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "NativeOperationalMapControl.cs"));

        Assert.Contains("MapStopFollowing", operate, StringComparison.Ordinal);
        Assert.Contains("JumpToOperatorCommand", operate, StringComparison.Ordinal);
        Assert.Contains("OperatorNavigationLabel", operate, StringComparison.Ordinal);
        Assert.DoesNotContain("_operatorLocationStatus", mapControl, StringComparison.Ordinal);
        Assert.Contains("UserPannedCommand", operate, StringComparison.Ordinal);
        Assert.Contains("JumpToSelectedCommand", units, StringComparison.Ordinal);
        Assert.Contains("FollowSelectedCommand", units, StringComparison.Ordinal);
    }

    [Fact]
    public void CompassControls_AreOpaqueAndDoNotBecomeMapGestures()
    {
        var root = FindRepositoryRoot();
        var mapControl = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "NativeOperationalMapControl.cs"));

        Assert.Contains("_compassPanel.ZIndex = 100", mapControl, StringComparison.Ordinal);
        Assert.Contains("FromArgb(245, 42, 48, 58)", mapControl, StringComparison.Ordinal);
        Assert.Contains("if (IsCompassSource(e.Source))", mapControl, StringComparison.Ordinal);
        Assert.Contains("PublishViewportSnapshot();", mapControl, StringComparison.Ordinal);
        Assert.Contains("rotateLeftButton.Click += (_, _) => RotateBy(-15)", mapControl, StringComparison.Ordinal);
        Assert.Contains("rotateRightButton.Click += (_, _) => RotateBy(15)", mapControl, StringComparison.Ordinal);
        Assert.Contains("mapCompassButton", mapControl, StringComparison.Ordinal);
        Assert.Contains("mapRotationButton", mapControl, StringComparison.Ordinal);
        Assert.Contains("Children.Add(scaleBadge)", mapControl, StringComparison.Ordinal);
        Assert.Contains("CornerRadius = new CornerRadius(56)", mapControl, StringComparison.Ordinal);
        Assert.Contains("ClipToBounds = true", mapControl, StringComparison.Ordinal);
        Assert.Contains("ConfigureRotationButtonPointerStates", mapControl, StringComparison.Ordinal);

        var styles = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Styles", "AppStyles.axaml"));
        Assert.Contains("Button.mapCompassButton:pointerover", styles, StringComparison.Ordinal);
        Assert.Contains("Button.mapRotationButton:pointerover", styles, StringComparison.Ordinal);
        Assert.Contains("Property=\"Width\" Value=\"24\"", styles, StringComparison.Ordinal);
        Assert.Contains("Property=\"Height\" Value=\"24\"", styles, StringComparison.Ordinal);

        Assert.Contains("Margin=\"0,10,0,0\"", File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorContextMenuDoesNotRebuildItemsForUnchangedState()
    {
        var root = FindRepositoryRoot();
        var mapControl = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "NativeOperationalMapControl.cs"));

        Assert.Contains("_mapContextMenuSignature", mapControl, StringComparison.Ordinal);
        Assert.Contains("if (string.Equals(signature, _mapContextMenuSignature", mapControl, StringComparison.Ordinal);
        Assert.Contains("_mapContextMenu.Items.Clear();", mapControl, StringComparison.Ordinal);
        Assert.Contains("Header = \"Queue Go to here\"", mapControl, StringComparison.Ordinal);
        Assert.Contains("Header = \"Queue Set heading here\"", mapControl, StringComparison.Ordinal);
    }

    [Fact]
    public void RecreatedMapRestoresSharedViewportInsteadOfWorldPlaceholder()
    {
        var root = FindRepositoryRoot();
        var mapControl = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "NativeOperationalMapControl.cs"));

        Assert.Contains("var startupViewportPending = !_hasAppliedInitialNavigation", mapControl, StringComparison.Ordinal);
        Assert.Contains("var viewportBeforeMapRecreation = startupViewportPending", mapControl, StringComparison.Ordinal);
        Assert.Contains("CurrentViewport ??", mapControl, StringComparison.Ordinal);
        Assert.Contains("change.Property == CurrentViewportProperty", mapControl, StringComparison.Ordinal);
        Assert.Contains("RestoreViewport(viewport);", mapControl, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayRenderingWaitsForAUsableViewportDuringWorkspaceReattachment()
    {
        var root = FindRepositoryRoot();
        var mapControl = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "NativeOperationalMapControl.cs"));

        Assert.Contains("HasUsableNativeViewport()", mapControl, StringComparison.Ordinal);
        Assert.Contains("TryPositionOnCanvas", mapControl, StringComparison.Ordinal);
        Assert.Contains("WorldToScreen is allowed to return an", mapControl, StringComparison.Ordinal);
        Assert.Contains("marker.IsVisible = false", mapControl, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeGeometryUsesDirectionalAndLabelledPresentation()
    {
        var root = FindRepositoryRoot();
        var mapControl = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "NativeOperationalMapControl.cs"));

        Assert.Contains("IsWaypointSequence(geometry)", mapControl, StringComparison.Ordinal);
        Assert.Contains("PenStyle.Dash", mapControl, StringComparison.Ordinal);
        Assert.Contains("AddWaypointDirectionArrows", mapControl, StringComparison.Ordinal);
        Assert.Contains("AvaloniaPolyline", mapControl, StringComparison.Ordinal);
        Assert.Contains("open chevrons", mapControl, StringComparison.Ordinal);
        Assert.Contains("TryGetGeometryLabelAnchor", mapControl, StringComparison.Ordinal);
        Assert.Contains("Text = geometry.Name", mapControl, StringComparison.Ordinal);
        Assert.Contains("SymbolScale = captureMarker ? 0.82 : geometry.Highlighted || IsFlightMissionPreview(geometry) ? 0.58 : 0.42", mapControl, StringComparison.Ordinal);
        Assert.Contains("#6FAFC9", mapControl, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailsUsePersistentNonInteractiveOverlayAndMarkerOnlyBoxSelection()
    {
        var root = FindRepositoryRoot();
        var mapControl = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "NativeOperationalMapControl.cs"));
        var trailOverlay = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "VehicleTrailOverlayControl.cs"));

        Assert.Contains("VehicleTrailOverlayControl _trailOverlay", mapControl, StringComparison.Ordinal);
        Assert.DoesNotContain("_trailLayer", mapControl, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceTrailFeatures", mapControl, StringComparison.Ordinal);
        Assert.Contains("TryGetVehicleScreenPoint(vehicle.VehicleId, vehicle", mapControl, StringComparison.Ordinal);
        Assert.Contains("_vehicleAnimations.TryGetValue(vehicleId", mapControl, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible = false", trailOverlay, StringComparison.Ordinal);
        Assert.Contains("_paths = next", trailOverlay, StringComparison.Ordinal);
        Assert.Contains("new DashStyle([5, 4], 0)", trailOverlay, StringComparison.Ordinal);
        Assert.Contains("Color.FromArgb(225, 139, 30, 45)", trailOverlay, StringComparison.Ordinal);
        Assert.Contains("Color.FromArgb(255, 255, 77, 94)", trailOverlay, StringComparison.Ordinal);
        Assert.Contains("forceReproject", trailOverlay, StringComparison.Ordinal);
    }

    [Fact]
    public void SchematicTrailsUseSubduedDashedThemePens()
    {
        var root = FindRepositoryRoot();
        var schematic = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Controls", "OperationalMapControl.cs"));

        Assert.Contains("private static readonly IPen TrailPen", schematic, StringComparison.Ordinal);
        Assert.Contains("private static readonly IPen SelectedTrailPen", schematic, StringComparison.Ordinal);
        Assert.Contains("new DashStyle([5, 4], 0)", schematic, StringComparison.Ordinal);
        Assert.Contains("Color.FromArgb(225, 139, 30, 45)", schematic, StringComparison.Ordinal);
        Assert.Contains("Color.FromArgb(255, 255, 77, 94)", schematic, StringComparison.Ordinal);
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
