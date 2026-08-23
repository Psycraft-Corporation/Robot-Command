using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RobotCommand.Models;

namespace RobotCommand.Controls;

public sealed class OperationalMapControl : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0D151D"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#24313C")), 1);
    private static readonly IPen PathPen = new Pen(new SolidColorBrush(Color.Parse("#62A8D3")), 2);
    private static readonly IPen ZonePen = new Pen(new SolidColorBrush(Color.Parse("#8492A6")), 1.5);
    private static readonly IPen PolicyPen = new Pen(new SolidColorBrush(Color.Parse("#F87171")), 2.5);
    private static readonly IPen HighlightPen = new Pen(new SolidColorBrush(Color.Parse("#F6C453")), 3);
    private static readonly IPen TrailPen = new Pen(
        new SolidColorBrush(Color.FromArgb(225, 139, 30, 45)),
        1.1,
        new DashStyle([5, 4], 0));
    private static readonly IPen SelectedTrailPen = new Pen(
        new SolidColorBrush(Color.FromArgb(255, 255, 77, 94)),
        1.1,
        new DashStyle([5, 4], 0));
    private static readonly IBrush ZoneBrush = new SolidColorBrush(Color.FromArgb(42, 104, 122, 138));
    private static readonly IBrush ExclusionBrush = new SolidColorBrush(Color.FromArgb(60, 185, 61, 51));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(48, 246, 196, 83));
    private static readonly IBrush VehicleBrush = new SolidColorBrush(Color.Parse("#8CC8E8"));
    private static readonly IBrush SelectedVehicleBrush = new SolidColorBrush(Color.Parse("#E4B860"));
    private static readonly IBrush DegradedVehicleBrush = new SolidColorBrush(Color.Parse("#E09B55"));

    public static readonly StyledProperty<OperationalMapScene?> SceneProperty =
        AvaloniaProperty.Register<OperationalMapControl, OperationalMapScene?>(nameof(Scene));

    static OperationalMapControl()
    {
        AffectsRender<OperationalMapControl>(SceneProperty);
    }

    public OperationalMapScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.DrawRectangle(BackgroundBrush, null, bounds);
        DrawGrid(context, bounds);

        var scene = Scene;
        if (scene is null || !scene.HasData || bounds.Width < 40 || bounds.Height < 40)
        {
            return;
        }

        var target = Inset(bounds, 24);
        if (target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        var projection = Projection.Create(scene, target);
        if (scene.TrailsVisible)
        {
            foreach (var trail in scene.Trails)
            {
                DrawTrail(context, trail, projection);
            }
        }

        foreach (var geometry in scene.Geometries)
        {
            DrawGeometry(context, geometry, projection);
        }

        foreach (var vehicle in scene.Vehicles)
        {
            DrawVehicle(context, vehicle, projection);
        }
    }


    private static Rect Inset(Rect bounds, double amount)
    {
        var width = Math.Max(0, bounds.Width - (amount * 2));
        var height = Math.Max(0, bounds.Height - (amount * 2));
        return new Rect(bounds.X + amount, bounds.Y + amount, width, height);
    }

    private static void DrawGrid(DrawingContext context, Rect bounds)
    {
        const int divisions = 10;
        for (var index = 1; index < divisions; index++)
        {
            var x = bounds.Left + (bounds.Width * index / divisions);
            var y = bounds.Top + (bounds.Height * index / divisions);
            context.DrawLine(GridPen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
            context.DrawLine(GridPen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
    }

    private static void DrawTrail(
        DrawingContext context,
        MapVehicleTrailVisual trail,
        Projection projection)
    {
        if (trail.Points.Count < 2)
        {
            return;
        }

        DrawPath(
            context,
            trail.Points,
            projection,
            closed: false,
            fill: null,
            trail.Selected ? SelectedTrailPen : TrailPen);
    }

    private static void DrawGeometry(
        DrawingContext context,
        MapGeometryVisual geometry,
        Projection projection)
    {
        var pen = geometry.Highlighted
            ? HighlightPen
            : geometry.IsPolicy
                ? PolicyPen
                : ZonePen;
        var fill = geometry.Highlighted
            ? HighlightBrush
            : geometry.IsPolicy || geometry.PolicyConstraint.Contains("exclusion", StringComparison.OrdinalIgnoreCase)
                ? ExclusionBrush
                : ZoneBrush;

        if (geometry.Rings.Count > 0)
        {
            foreach (var ring in geometry.Rings.Where(item => item.Count >= 3))
            {
                DrawPath(context, ring, projection, closed: true, fill, pen);
            }

            return;
        }

        if (geometry.Points.Count == 1)
        {
            var point = projection.Project(geometry.Points[0]);
            context.DrawEllipse(fill, pen, point, geometry.Highlighted ? 7 : 5, geometry.Highlighted ? 7 : 5);
            return;
        }

        if (geometry.Points.Count > 1)
        {
            DrawPath(
                context,
                geometry.Points,
                projection,
                geometry.Closed,
                geometry.Closed ? fill : null,
                geometry.Highlighted || geometry.IsPolicy ? pen : PathPen);
        }
    }

    private static void DrawPath(
        DrawingContext context,
        IReadOnlyList<OperationalPoint> points,
        Projection projection,
        bool closed,
        IBrush? fill,
        IPen pen)
    {
        if (points.Count == 0)
        {
            return;
        }

        var path = new StreamGeometry();
        using (var geometryContext = path.Open())
        {
            geometryContext.BeginFigure(projection.Project(points[0]), fill is not null);
            foreach (var point in points.Skip(1))
            {
                geometryContext.LineTo(projection.Project(point));
            }

            geometryContext.EndFigure(closed);
        }

        context.DrawGeometry(fill, pen, path);
    }

    private static void DrawVehicle(
        DrawingContext context,
        MapVehicleVisual vehicle,
        Projection projection)
    {
        var center = projection.Project(new OperationalPoint(vehicle.X, vehicle.Y));
        var heading = (vehicle.HeadingDegrees ?? 0) * Math.PI / 180d;
        var size = vehicle.Selected || vehicle.TeamSelected ? 14d : 11d;
        var markerWidth = size * 1.53;
        var markerHeight = size * 2;
        var points = UnitMarkerGeometry.CreateCentered(markerWidth, markerHeight)
            .Select(point => Rotate(point, heading, center))
            .ToArray();

        var shape = new StreamGeometry();
        using (var geometryContext = shape.Open())
        {
            geometryContext.BeginFigure(points[0], true);
            foreach (var point in points.Skip(1))
            {
                geometryContext.LineTo(point);
            }

            geometryContext.EndFigure(true);
        }

        var baseColor = vehicle.Selected
            ? Color.Parse("#E4B860")
            : vehicle.TeamSelected
                ? Color.Parse("#60C4E8")
            : vehicle.State is AvailabilityState.Degraded or AvailabilityState.Stale
                ? Color.Parse("#E09B55")
                : Color.Parse("#8CC8E8");
        var brush = new SolidColorBrush(vehicle.IsGhost
            ? Color.FromArgb(135, baseColor.R, baseColor.G, baseColor.B)
            : baseColor);
        context.DrawGeometry(brush, new Pen(Brushes.Black, 1), shape);
        if (vehicle.Selected || vehicle.TeamSelected)
        {
            var selectionBrush = vehicle.TeamSelected
                ? new SolidColorBrush(Color.Parse("#60C4E8"))
                : SelectedVehicleBrush;
            context.DrawEllipse(null, new Pen(selectionBrush, 1.5), center, 17, 17);
        }
    }

    private static Point Rotate(Point local, double radians, Point center)
    {
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new Point(
            center.X + (local.X * cosine) - (local.Y * sine),
            center.Y + (local.X * sine) + (local.Y * cosine));
    }

    private sealed record Projection(double MinX, double MaxX, double MinY, double MaxY, Rect Target)
    {
        public Point Project(OperationalPoint point)
        {
            var x = Target.Left + ((point.X - MinX) / Math.Max(MaxX - MinX, double.Epsilon) * Target.Width);
            var y = Target.Bottom - ((point.Y - MinY) / Math.Max(MaxY - MinY, double.Epsilon) * Target.Height);
            return new Point(x, y);
        }

        public static Projection Create(OperationalMapScene scene, Rect target)
        {
            var points = new List<OperationalPoint>();
            points.AddRange(scene.Vehicles.Select(item => new OperationalPoint(item.X, item.Y)));
            foreach (var geometry in scene.Geometries)
            {
                points.AddRange(geometry.Points);
                foreach (var ring in geometry.Rings)
                {
                    points.AddRange(ring);
                }
            }

            if (scene.TrailsVisible)
            {
                foreach (var trail in scene.Trails)
                {
                    points.AddRange(trail.Points);
                }
            }

            var minX = points.Min(item => item.X);
            var maxX = points.Max(item => item.X);
            var minY = points.Min(item => item.Y);
            var maxY = points.Max(item => item.Y);

            if (scene.ViewportMode == MapViewportMode.FollowSelected)
            {
                var selected = scene.Vehicles.FirstOrDefault(item => item.Selected);
                if (selected is not null)
                {
                    var halfSpan = scene.Frame == MapFrameKind.GlobalWgs84 ? 0.0015 : 125d;
                    minX = selected.X - halfSpan;
                    maxX = selected.X + halfSpan;
                    minY = selected.Y - halfSpan;
                    maxY = selected.Y + halfSpan;
                }
            }

            ExpandDegenerate(ref minX, ref maxX, scene.Frame == MapFrameKind.GlobalWgs84 ? 0.0005 : 25d);
            ExpandDegenerate(ref minY, ref maxY, scene.Frame == MapFrameKind.GlobalWgs84 ? 0.0005 : 25d);

            var xPadding = (maxX - minX) * 0.10;
            var yPadding = (maxY - minY) * 0.10;
            return new Projection(minX - xPadding, maxX + xPadding, minY - yPadding, maxY + yPadding, target);
        }

        private static void ExpandDegenerate(ref double min, ref double max, double amount)
        {
            if (Math.Abs(max - min) >= double.Epsilon)
            {
                return;
            }

            min -= amount;
            max += amount;
        }
    }
}
