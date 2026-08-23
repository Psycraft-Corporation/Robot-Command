using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RobotCommand.Core;
using RobotCommand.Models;

namespace RobotCommand.Controls;

/// <summary>Lightweight presentation-only preview of a saved mission.</summary>
public sealed class FlightMissionPreviewControl : Control
{
    public static readonly StyledProperty<FlightMissionSnapshot?> MissionProperty =
        AvaloniaProperty.Register<FlightMissionPreviewControl, FlightMissionSnapshot?>(nameof(Mission));
    public static readonly StyledProperty<FlightMissionCompilationPreview?> PreviewProperty =
        AvaloniaProperty.Register<FlightMissionPreviewControl, FlightMissionCompilationPreview?>(nameof(Preview));
    public static readonly StyledProperty<UnitObservationSnapshot?> UnitProperty =
        AvaloniaProperty.Register<FlightMissionPreviewControl, UnitObservationSnapshot?>(nameof(Unit));
    public static readonly StyledProperty<OperatorLocationSnapshot?> OperatorLocationProperty =
        AvaloniaProperty.Register<FlightMissionPreviewControl, OperatorLocationSnapshot?>(nameof(OperatorLocation));

    static FlightMissionPreviewControl() => AffectsRender<FlightMissionPreviewControl>(MissionProperty, PreviewProperty, UnitProperty, OperatorLocationProperty);

    public FlightMissionSnapshot? Mission { get => GetValue(MissionProperty); set => SetValue(MissionProperty, value); }
    public FlightMissionCompilationPreview? Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public UnitObservationSnapshot? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public OperatorLocationSnapshot? OperatorLocation { get => GetValue(OperatorLocationProperty); set => SetValue(OperatorLocationProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#101820")), null, bounds);
        if (bounds.Width < 80 || bounds.Height < 80 || Mission is null)
        {
            return;
        }

        var stepPoints = Mission.Steps.SelectMany(step => step.FrozenCoordinates.Select(point => (Step: step, Point: point))).ToArray();
        var route = Preview?.Route ?? [];
        var unitPoint = Unit?.Telemetry is { LatitudeDegrees: { } unitLatitude, LongitudeDegrees: { } unitLongitude }
            ? new FlightMissionCoordinate(unitLatitude, unitLongitude)
            : null;
        var operatorPoint = OperatorLocation is { IsAvailable: true, LatitudeDegrees: { } operatorLatitude, LongitudeDegrees: { } operatorLongitude }
            ? new FlightMissionCoordinate(operatorLatitude, operatorLongitude)
            : null;
        var all = stepPoints.Select(item => item.Point).Concat(route)
            .Concat(unitPoint is null ? [] : [unitPoint])
            .Concat(operatorPoint is null ? [] : [operatorPoint])
            .ToArray();
        if (all.Length == 0)
        {
            MapDrawingPrimitives.DrawText(context, "No navigable geometry in this mission.", new Point(16, 16), Brushes.LightGray);
            return;
        }

        var projection = Projection.Create(all, bounds.Deflate(28));
        DrawGrid(context, projection.Bounds);
        foreach (var group in stepPoints.GroupBy(item => item.Step.Id))
        {
            var step = group.First().Step;
            var points = group.Select(item => projection.Project(item.Point)).ToArray();
            var pen = new Pen(StepBrush(step), 2);
            if (step.Kind == FlightMissionStepKind.SurveyZone && points.Length >= 3)
            {
                var polygon = new StreamGeometry();
                using (var builder = polygon.Open())
                {
                    builder.BeginFigure(points[0], true);
                    foreach (var point in points.Skip(1)) builder.LineTo(point);
                    builder.EndFigure(true);
                }
                context.DrawGeometry(new SolidColorBrush(Color.FromArgb(42, 75, 175, 140)), pen, polygon);
            }
            else if (points.Length >= 2)
            {
                MapDrawingPrimitives.DrawPath(context, points, false, null, step.Kind == FlightMissionStepKind.WaypointSequence ? Dashed(pen) : pen);
                MapDrawingPrimitives.DrawDirectionArrows(context, points, pen.Brush ?? Brushes.White);
            }
            else if (points.Length == 1)
            {
                context.DrawEllipse(StepBrush(step), pen, points[0], 5, 5);
            }

            var label = step.SourceGeometryName ?? step.DisplayName;
            if (points.Length == 0)
                continue;
            var labelPoint = points.FirstOrDefault();
            if (points.Length > 1)
                labelPoint = points[points.Length / 2];
            MapDrawingPrimitives.DrawLabel(context, label, new Point(labelPoint.X + 6, labelPoint.Y + 6), Brushes.White);
        }

        if (route.Count >= 2)
        {
            var routePoints = route.Select(projection.Project).ToArray();
            var routePen = new Pen(new SolidColorBrush(Color.Parse("#F6C453")), 3);
            MapDrawingPrimitives.DrawPath(context, routePoints, false, null, routePen);
            MapDrawingPrimitives.DrawDirectionArrows(context, routePoints, routePen.Brush ?? Brushes.White, 48);
            foreach (var point in routePoints)
                context.DrawEllipse(new SolidColorBrush(Color.Parse("#F6C453")), new Pen(Brushes.Black, 1), point, 3.5, 3.5);
        }

        if (unitPoint is not null)
        {
            var unit = projection.Project(unitPoint);
            var firstStep = route.Count > 0 ? route[0] : null;
            if (firstStep is not null && !NearlyEqual(unitPoint, firstStep))
            {
                var first = projection.Project(firstStep);
                var approachPen = new Pen(new SolidColorBrush(Color.Parse("#5ED6D1")), 2, new DashStyle([8, 5], 0));
                MapDrawingPrimitives.DrawPath(context, [unit, first], false, null, approachPen);
                MapDrawingPrimitives.DrawDirectionArrows(context, [unit, first], approachPen.Brush ?? Brushes.White, 56);
            }

            if (Mission.Steps.Any(step => step.Kind == FlightMissionStepKind.ReturnToLaunch) && route.Count > 0)
            {
                var lastStep = route[^1];
                if (!NearlyEqual(unitPoint, lastStep))
                {
                    var last = projection.Project(lastStep);
                    var rtlPen = new Pen(new SolidColorBrush(Color.Parse("#FF9F5E")), 2, new DashStyle([8, 5], 0));
                    MapDrawingPrimitives.DrawPath(context, [last, unit], false, null, rtlPen);
                    MapDrawingPrimitives.DrawDirectionArrows(context, [last, unit], rtlPen.Brush ?? Brushes.White, 56);
                    MapDrawingPrimitives.DrawLabel(context, "RTL", new Point(last.X + 8, last.Y - 18), rtlPen.Brush ?? Brushes.White);
                }
            }

            var unitBrush = new SolidColorBrush(Color.Parse("#5ED6D1"));
            context.DrawEllipse(unitBrush, new Pen(Brushes.Black, 2), unit, 7, 7);
            context.DrawEllipse(null, new Pen(unitBrush, 2), unit, 11, 11);
            MapDrawingPrimitives.DrawLabel(context, $"Unit: {Unit?.Name ?? "assigned"}", new Point(unit.X + 10, unit.Y - 18), unitBrush);
        }

        if (operatorPoint is not null)
        {
            var point = projection.Project(operatorPoint);
            var operatorBrush = new SolidColorBrush(Color.Parse("#D98CFF"));
            context.DrawEllipse(operatorBrush, new Pen(Brushes.Black, 2), point, 6, 6);
            context.DrawLine(new Pen(operatorBrush, 2), new Point(point.X - 10, point.Y), new Point(point.X + 10, point.Y));
            context.DrawLine(new Pen(operatorBrush, 2), new Point(point.X, point.Y - 10), new Point(point.X, point.Y + 10));
            MapDrawingPrimitives.DrawLabel(context, "Operator", new Point(point.X + 10, point.Y + 8), operatorBrush);
        }
    }

    private static bool NearlyEqual(FlightMissionCoordinate left, FlightMissionCoordinate right)
        => Math.Abs(left.LatitudeDegrees - right.LatitudeDegrees) < 0.0000001 &&
           Math.Abs(left.LongitudeDegrees - right.LongitudeDegrees) < 0.0000001;

    private static void DrawGrid(DrawingContext context, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#263644")), 1);
        for (var index = 1; index < 10; index++)
        {
            var x = bounds.Left + bounds.Width * index / 10;
            var y = bounds.Top + bounds.Height * index / 10;
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
            context.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
    }

    private static Pen Dashed(Pen pen) => new(pen.Brush, pen.Thickness, new DashStyle([6, 4], 0));

    private static SolidColorBrush StepBrush(FlightMissionStep step) => step.Kind switch
    {
        FlightMissionStepKind.SurveyZone => new SolidColorBrush(Color.Parse("#5EA6C9")),
        FlightMissionStepKind.CorridorScan => new SolidColorBrush(Color.Parse("#8CC8E8")),
        FlightMissionStepKind.WaypointSequence => new SolidColorBrush(Color.Parse("#9BD4A5")),
        _ => new SolidColorBrush(Color.Parse("#E4B860"))
    };

    private sealed record Projection(double MinX, double MaxX, double MinY, double MaxY, Rect Bounds)
    {
        public Point Project(FlightMissionCoordinate point)
        {
            var x = Bounds.Left + (point.LongitudeDegrees - MinX) / Math.Max(MaxX - MinX, 0.000001) * Bounds.Width;
            var y = Bounds.Bottom - (point.LatitudeDegrees - MinY) / Math.Max(MaxY - MinY, 0.000001) * Bounds.Height;
            return new Point(x, y);
        }

        public static Projection Create(IReadOnlyList<FlightMissionCoordinate> points, Rect bounds)
        {
            var minX = points.Min(point => point.LongitudeDegrees); var maxX = points.Max(point => point.LongitudeDegrees);
            var minY = points.Min(point => point.LatitudeDegrees); var maxY = points.Max(point => point.LatitudeDegrees);
            var padX = Math.Max((maxX - minX) * 0.08, 0.00005); var padY = Math.Max((maxY - minY) * 0.08, 0.00005);
            minX -= padX; maxX += padX; minY -= padY; maxY += padY;
            var longitudeSpan = Math.Max((maxX - minX) * Math.Cos(((minY + maxY) / 2) * Math.PI / 180d), 0.000001);
            var latitudeSpan = Math.Max(maxY - minY, 0.000001);
            var sourceAspect = longitudeSpan / latitudeSpan;
            var targetAspect = Math.Max(bounds.Width / Math.Max(bounds.Height, 1), 0.000001);
            Rect plot;
            if (targetAspect > sourceAspect)
            {
                var width = bounds.Height * sourceAspect;
                plot = new Rect(bounds.Left + (bounds.Width - width) / 2, bounds.Top, width, bounds.Height);
            }
            else
            {
                var height = bounds.Width / sourceAspect;
                plot = new Rect(bounds.Left, bounds.Top + (bounds.Height - height) / 2, bounds.Width, height);
            }
            return new(minX, maxX, minY, maxY, plot);
        }
    }
}
