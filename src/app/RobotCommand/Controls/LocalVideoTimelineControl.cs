using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using RobotCommand.Models;

namespace RobotCommand.Controls;

public sealed class LocalVideoTimelineControl : Control
{
    public static readonly StyledProperty<UnifiedVideoTimelineSnapshot?> TimelineProperty =
        AvaloniaProperty.Register<LocalVideoTimelineControl, UnifiedVideoTimelineSnapshot?>(nameof(Timeline));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<LocalVideoTimelineControl, double>(
            nameof(Position),
            1,
            defaultBindingMode: BindingMode.TwoWay,
            validate: value => double.IsFinite(value));

    static LocalVideoTimelineControl()
    {
        AffectsRender<LocalVideoTimelineControl>(TimelineProperty, PositionProperty);
    }

    public UnifiedVideoTimelineSnapshot? Timeline
    {
        get => GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, Math.Clamp(value, 0, 1));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var laneHeight = Math.Max(6, Math.Min(10, (Bounds.Height - 8) / 2));
        var vehicleTrack = new Rect(0, 2, Bounds.Width, laneHeight);
        var consoleTrack = new Rect(0, vehicleTrack.Bottom + 4, Bounds.Width, laneHeight);
        var background = new SolidColorBrush(Color.Parse("#252A31"));
        context.FillRectangle(background, vehicleTrack, 3);
        context.FillRectangle(background, consoleTrack, 3);

        var timeline = Timeline;
        if (timeline?.RangeStart is not { } rangeStart ||
            timeline.RangeEnd is not { } rangeEnd ||
            rangeEnd <= rangeStart || Bounds.Width <= 0)
        {
            DrawCursor(context, vehicleTrack, consoleTrack);
            return;
        }

        var rangeTicks = (rangeEnd - rangeStart).Ticks;
        foreach (var item in timeline.Items)
        {
            var track = item.IsVehicleSide ? vehicleTrack : consoleTrack;
            var start = Math.Clamp((item.StartedAt - rangeStart).Ticks / (double)rangeTicks, 0, 1);
            var end = Math.Clamp((item.EndedAt - rangeStart).Ticks / (double)rangeTicks, start, 1);
            var width = Math.Max(2, (end - start) * track.Width);
            var rectangle = new Rect(track.X + start * track.Width, track.Y, width, track.Height);
            context.FillRectangle(BrushFor(item), rectangle, 2);
            if (item.GapBefore)
            {
                var gapX = rectangle.X;
                context.DrawLine(
                    new Pen(new SolidColorBrush(Color.Parse("#EAB308")), 2),
                    new Point(gapX, track.Y - 2),
                    new Point(gapX, track.Bottom + 2));
            }
        }

        context.DrawLine(
            new Pen(new SolidColorBrush(Color.Parse("#EF4444")), 2),
            new Point(consoleTrack.Right, vehicleTrack.Y - 2),
            new Point(consoleTrack.Right, consoleTrack.Bottom + 2));
        DrawCursor(context, vehicleTrack, consoleTrack);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        if (Bounds.Width > 0)
        {
            Position = Math.Clamp(point.X / Bounds.Width, 0, 1);
            e.Handled = true;
        }
    }

    private static SolidColorBrush BrushFor(VideoTimelineItem item)
    {
        if (item.Retained)
        {
            return new SolidColorBrush(Color.Parse("#C89B3C"));
        }
        if (item.Availability == VideoTimelineItemAvailability.Unavailable)
        {
            return new SolidColorBrush(Color.Parse("#7F1D1D"));
        }
        return item.Origin switch
        {
            VideoTimelineItemOrigin.VehicleCached => new SolidColorBrush(Color.Parse("#2563EB")),
            VideoTimelineItemOrigin.VehicleRecording => new SolidColorBrush(Color.Parse("#0F766E")),
            VideoTimelineItemOrigin.ConsoleRetained => new SolidColorBrush(Color.Parse("#C89B3C")),
            _ => new SolidColorBrush(Color.Parse("#64748B"))
        };
    }

    private void DrawCursor(DrawingContext context, Rect vehicleTrack, Rect consoleTrack)
    {
        var x = vehicleTrack.X + Math.Clamp(Position, 0, 1) * vehicleTrack.Width;
        context.DrawLine(
            new Pen(Brushes.White, 2),
            new Point(x, vehicleTrack.Y - 4),
            new Point(x, consoleTrack.Bottom + 4));
    }
}
