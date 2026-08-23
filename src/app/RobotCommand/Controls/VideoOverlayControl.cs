using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RobotCommand.Models;

namespace RobotCommand.Controls;

public sealed class VideoOverlayControl : Control
{
    private static readonly IPen SafeFramePen = new Pen(new SolidColorBrush(Color.Parse("#26313A")), 1);
    private static readonly IPen TrackPen = new Pen(new SolidColorBrush(Color.Parse("#63D2A6")), 2);
    private static readonly IPen SelectedTrackPen = new Pen(new SolidColorBrush(Color.Parse("#E4B860")), 3);

    public static readonly StyledProperty<VideoOverlayScene?> SceneProperty =
        AvaloniaProperty.Register<VideoOverlayControl, VideoOverlayScene?>(nameof(Scene));

    static VideoOverlayControl()
    {
        AffectsRender<VideoOverlayControl>(SceneProperty);
    }

    public VideoOverlayScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var target = FitAspect(Inset(Bounds, 12), Scene?.Width ?? 0, Scene?.Height ?? 0);
        context.DrawRectangle(null, SafeFramePen, target);

        var scene = Scene;
        if (scene is null)
        {
            return;
        }

        foreach (var track in scene.Tracks)
        {
            var width = Math.Clamp(track.Width, 0, 1) * target.Width;
            var height = Math.Clamp(track.Height, 0, 1) * target.Height;
            var centerX = target.Left + (Math.Clamp(track.CenterX, 0, 1) * target.Width);
            var centerY = target.Top + (Math.Clamp(track.CenterY, 0, 1) * target.Height);
            var rect = new Rect(centerX - (width / 2), centerY - (height / 2), width, height);
            context.DrawRectangle(null, track.Selected ? SelectedTrackPen : TrackPen, rect);
        }
    }

    private static Rect Inset(Rect bounds, double amount)
    {
        var width = Math.Max(0, bounds.Width - (amount * 2));
        var height = Math.Max(0, bounds.Height - (amount * 2));
        return new Rect(bounds.X + amount, bounds.Y + amount, width, height);
    }

    private static Rect FitAspect(Rect available, uint width, uint height)
    {
        var aspect = width > 0 && height > 0 ? (double)width / height : 16d / 9d;
        var availableAspect = available.Width / Math.Max(available.Height, 1);
        if (availableAspect > aspect)
        {
            var fittedWidth = available.Height * aspect;
            return new Rect(
                available.Left + ((available.Width - fittedWidth) / 2),
                available.Top,
                fittedWidth,
                available.Height);
        }

        var fittedHeight = available.Width / aspect;
        return new Rect(
            available.Left,
            available.Top + ((available.Height - fittedHeight) / 2),
            available.Width,
            fittedHeight);
    }
}
