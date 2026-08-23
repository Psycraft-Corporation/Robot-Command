using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RobotCommand.Models;
using RobotCommand.Services.Maps;

namespace RobotCommand.Controls;

/// <summary>
/// Display-only vehicle trails. This overlay is deliberately not hit-testable:
/// vehicle selection is performed against the vehicle marker layer, never
/// against individual trail geometry.
/// </summary>
public sealed class VehicleTrailOverlayControl : Control
{
    private static readonly IPen NormalPen = new Pen(
        new SolidColorBrush(Color.FromArgb(225, 139, 30, 45)),
        1.1,
        new DashStyle([5, 4], 0));

    private static readonly IPen SelectedPen = new Pen(
        new SolidColorBrush(Color.FromArgb(255, 255, 77, 94)),
        1.1,
        new DashStyle([5, 4], 0));

    private IReadOnlyDictionary<string, TrailPath> _paths =
        new Dictionary<string, TrailPath>(StringComparer.Ordinal);
    private IReadOnlyList<MapVehicleTrailVisual> _trails = [];
    private Func<double, double, Point>? _worldToScreen;
    private bool _visible;
    private string _lastFingerprint = string.Empty;
    private MapFrameKind _frame = MapFrameKind.Unknown;

    public VehicleTrailOverlayControl()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
    }

    public void UpdateTrails(
        IReadOnlyList<MapVehicleTrailVisual> trails,
        MapFrameKind frame,
        Func<double, double, Point> worldToScreen,
        bool visible,
        bool forceReproject = false)
    {
        _worldToScreen = worldToScreen;
        _visible = visible && frame != MapFrameKind.Unknown;

        if (!_visible)
        {
            if (_paths.Count == 0 && !IsVisible)
            {
                return;
            }

            _paths = new Dictionary<string, TrailPath>(StringComparer.Ordinal);
            _trails = [];
            _lastFingerprint = string.Empty;
            _frame = frame;
            IsVisible = false;
            InvalidateVisual();
            return;
        }

        var fingerprint = CreateFingerprint(trails, frame);
        if (!forceReproject &&
            string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal) &&
            _frame == frame)
        {
            IsVisible = true;
            return;
        }

        _trails = trails;
        _frame = frame;
        _lastFingerprint = fingerprint;
        Reproject(worldToScreen);
    }

    /// <summary>
    /// Reprojects the cached world-space trails against the current map
    /// viewport. This is deliberately separate from trail-data publication:
    /// panning and zooming must never depend on a new telemetry sample.
    /// </summary>
    public void Reproject(Func<double, double, Point> worldToScreen)
    {
        _worldToScreen = worldToScreen;
        if (!_visible || _frame == MapFrameKind.Unknown)
        {
            return;
        }

        var next = new Dictionary<string, TrailPath>(StringComparer.Ordinal);
        foreach (var trail in _trails)
        {
            if (trail.Points.Count < 2)
            {
                continue;
            }

            var points = new List<Point>(trail.Points.Count);
            foreach (var source in trail.Points)
            {
                if (_frame == MapFrameKind.GlobalWgs84)
                {
                    if (!MapCoordinateProjector.TryProject(source.X, source.Y, out var projected))
                    {
                        continue;
                    }

                    points.Add(worldToScreen(projected.X, projected.Y));
                }
                else
                {
                    points.Add(worldToScreen(source.X, source.Y));
                }
            }

            if (points.Count < 2 || points.Any(point =>
                    !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            {
                continue;
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], false);
                foreach (var point in points.Skip(1))
                {
                    context.LineTo(point);
                }

                context.EndFigure(false);
            }

            next[trail.VehicleId] = new TrailPath(geometry, trail.Selected);
        }

        // Replace the immutable path map in one operation. Render never sees
        // a partially updated collection while a telemetry batch is applied.
        _paths = next;
        IsVisible = next.Count > 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        foreach (var path in _paths.Values)
        {
            context.DrawGeometry(null, path.Selected ? SelectedPen : NormalPen, path.Geometry);
        }
    }

    private static string CreateFingerprint(
        IReadOnlyList<MapVehicleTrailVisual> trails,
        MapFrameKind frame)
    {
        var hash = new HashCode();
        hash.Add(frame);
        foreach (var trail in trails.OrderBy(item => item.VehicleId, StringComparer.Ordinal))
        {
            hash.Add(trail.VehicleId, StringComparer.Ordinal);
            hash.Add(trail.Selected);
            hash.Add(trail.Points.Count);
            foreach (var point in trail.Points)
            {
                hash.Add(point.X);
                hash.Add(point.Y);
            }
        }

        return hash.ToHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record TrailPath(StreamGeometry Geometry, bool Selected);
}
