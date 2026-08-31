using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace RobotCommand.Controls;

/// <summary>Small drawing primitives shared by the lightweight map renderers.</summary>
internal static class MapDrawingPrimitives
{
    public const string MissionPreviewAccentHex = "#EF4444";
    public const double DirectionArrowSpacing = 48;
    public const double DirectionArrowLength = 12;
    public const double DirectionArrowHalfWidth = 6;
    public const double DirectionArrowThickness = 2;

    public static void DrawPath(DrawingContext context, IReadOnlyList<Point> points, bool closed, IBrush? fill, IPen pen)
    {
        if (points.Count == 0) return;
        var path = new StreamGeometry();
        using (var builder = path.Open())
        {
            builder.BeginFigure(points[0], fill is not null);
            foreach (var point in points.Skip(1)) builder.LineTo(point);
            builder.EndFigure(closed);
        }
        context.DrawGeometry(fill, pen, path);
    }

    public static void DrawDirectionArrows(DrawingContext context, IReadOnlyList<Point> points, IBrush brush, double spacing = DirectionArrowSpacing)
    {
        var pen = new Pen(brush, DirectionArrowThickness);
        for (var index = 0; index < points.Count - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 18) continue;
            var count = Math.Max(1, (int)(length / spacing));
            var ux = dx / length;
            var uy = dy / length;
            var px = -uy;
            var py = ux;
            for (var arrow = 1; arrow <= count; arrow++)
            {
                var distance = length * arrow / (count + 1d);
                var tip = new Point(start.X + ux * distance, start.Y + uy * distance);
                var back = new Point(tip.X - ux * DirectionArrowLength, tip.Y - uy * DirectionArrowLength);
                context.DrawLine(pen, tip, new Point(back.X + px * DirectionArrowHalfWidth, back.Y + py * DirectionArrowHalfWidth));
                context.DrawLine(pen, tip, new Point(back.X - px * DirectionArrowHalfWidth, back.Y - py * DirectionArrowHalfWidth));
            }
        }
    }

    public static void DrawLabel(DrawingContext context, string text, Point anchor, IBrush foreground)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, foreground);
        var rect = new Rect(anchor.X - 4, anchor.Y, Math.Max(24, text.Length * 6.5 + 8), 20);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 13, 21, 29)), null, rect);
        context.DrawText(formatted, new Point(anchor.X, anchor.Y + 2));
    }

    public static void DrawText(DrawingContext context, string text, Point point, IBrush brush)
        => context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush), point);
}
