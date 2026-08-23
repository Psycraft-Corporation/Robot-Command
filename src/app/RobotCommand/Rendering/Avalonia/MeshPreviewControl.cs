using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RobotCommand.Core;
using RobotCommand.Rendering;
using RobotCommand.Rendering.Meshes;

namespace RobotCommand.Rendering.Avalonia;

/// <summary>Small, reusable CPU mesh preview. It intentionally owns no asset loading or storage.</summary>
public sealed class MeshPreviewControl : Control
{
    public static readonly StyledProperty<MeshAssetSnapshot?> AssetProperty =
        AvaloniaProperty.Register<MeshPreviewControl, MeshAssetSnapshot?>(nameof(Asset));

    private Point? _last;
    private double _yaw;
    private double _pitch = -15;

    static MeshPreviewControl()
    {
        AffectsRender<MeshPreviewControl>(AssetProperty);
        FocusableProperty.OverrideDefaultValue<MeshPreviewControl>(true);
    }

    public MeshAssetSnapshot? Asset
    {
        get => GetValue(AssetProperty);
        set => SetValue(AssetProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        _last = e.GetPosition(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_last is not { } last || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var current = e.GetPosition(this);
        _yaw += (current.X - last.X) * 0.6;
        _pitch = Math.Clamp(_pitch + (current.Y - last.Y) * 0.4, -89, 89);
        _last = current;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _last = null; base.OnPointerReleased(e); }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta.Y > 0 ? 0.88 : 1.14), 0.2, 1000);
        InvalidateVisual();
        e.Handled = true;
    }

    private double _distance = 3;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.R)
        {
            _yaw = 0; _pitch = -15; _distance = 3; InvalidateVisual(); e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0B1118")), Bounds);
        var asset = Asset;
        if (asset is null) return;
        var size = asset.Bounds.Size;
        var radius = Math.Max(0.01, Math.Sqrt(size.X * size.X + size.Y * size.Y + size.Z * size.Z) / 2);
        var camera = new ThreeDCameraSnapshot(new(0, radius * 0.7, -Math.Max(radius * _distance, 1)), _yaw, _pitch, 0, 55, 0.01, 10000, 1);
        var rotation = Matrix4x4.CreateRotationY((float)(_yaw * Math.PI / 180)) * Matrix4x4.CreateRotationX((float)(_pitch * Math.PI / 180));
        var triangles = MeshSceneConversion.ToTriangles(asset)
            .Select(triangle => new MeshTriangle(
                Transform(triangle.A, rotation, asset.Bounds.Center),
                Transform(triangle.B, rotation, asset.Bounds.Center),
                Transform(triangle.C, rotation, asset.Bounds.Center), triangle.Color))
            .OrderByDescending(triangle => AverageDepth(triangle, camera))
            .ToArray();
        foreach (var triangle in triangles)
        {
            var a = ThreeDProjection.Project(triangle.A, camera, Bounds.Width, Bounds.Height);
            var b = ThreeDProjection.Project(triangle.B, camera, Bounds.Width, Bounds.Height);
            var c = ThreeDProjection.Project(triangle.C, camera, Bounds.Width, Bounds.Height);
            if (!a.Visible || !b.Visible || !c.Visible) continue;
            var geometry = new StreamGeometry();
            using (var builder = geometry.Open())
            {
                builder.BeginFigure(new Point(a.X, a.Y), true);
                builder.LineTo(new Point(b.X, b.Y)); builder.LineTo(new Point(c.X, c.Y)); builder.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse(triangle.Color)), new Pen(Brushes.Gray, 0.5), geometry);
        }
    }

    private static ThreeDVector3 Transform(ThreeDVector3 value, Matrix4x4 rotation, ThreeDVector3 center)
    {
        var centered = new Vector3((float)(value.X - center.X), (float)(value.Y - center.Y), (float)(value.Z - center.Z));
        var transformed = Vector3.Transform(centered, rotation);
        return new(transformed.X, transformed.Y, transformed.Z);
    }

    private static double AverageDepth(MeshTriangle triangle, ThreeDCameraSnapshot camera)
        => (Depth(triangle.A, camera) + Depth(triangle.B, camera) + Depth(triangle.C, camera)) / 3;

    private static double Depth(ThreeDVector3 value, ThreeDCameraSnapshot camera)
        => Math.Sqrt(Math.Pow(value.X - camera.Position.X, 2) + Math.Pow(value.Y - camera.Position.Y, 2) + Math.Pow(value.Z - camera.Position.Z, 2));
}
