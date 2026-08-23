using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using RobotCommand.Core;
using RobotCommand.Rendering;

namespace RobotCommand.Rendering.Avalonia;

public sealed class ThreeDViewportControl : Control
{
    public static readonly StyledProperty<ThreeDSceneSnapshot?> SceneProperty =
        AvaloniaProperty.Register<ThreeDViewportControl, ThreeDSceneSnapshot?>(nameof(Scene));

    public static readonly StyledProperty<bool> SoftwareModeProperty =
        AvaloniaProperty.Register<ThreeDViewportControl, bool>(nameof(SoftwareMode), true);

    public static readonly StyledProperty<bool> OrbitControlsProperty =
        AvaloniaProperty.Register<ThreeDViewportControl, bool>(nameof(OrbitControls));

    private readonly DispatcherTimer _timer;
    private Point? _lastPointer;
    private ThreeDCameraSnapshot? _camera;
    private bool _rightDrag;
    private bool _attached;

    static ThreeDViewportControl()
    {
        AffectsRender<ThreeDViewportControl>(SceneProperty);
        FocusableProperty.OverrideDefaultValue<ThreeDViewportControl>(true);
    }

    public ThreeDViewportControl()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
        if (IsVisible) _timer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _timer.Stop();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            if (IsVisible) _timer.Start();
        };
    }

    public ThreeDSceneSnapshot? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public bool SoftwareMode
    {
        get => GetValue(SoftwareModeProperty);
        set => SetValue(SoftwareModeProperty, value);
    }

    public bool OrbitControls
    {
        get => GetValue(OrbitControlsProperty);
        set => SetValue(OrbitControlsProperty, value);
    }

    public event EventHandler<ThreeDCameraSnapshot>? CameraChanged;
    public event EventHandler? FitRequested;

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (IsVisible) _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0B1118")), bounds);
        var scene = Scene;
        if (scene is null) return;
        if (_camera is null || !_camera.Equals(scene.Camera))
            _camera = scene.Camera;

        var renderScene = scene with { Camera = _camera };
        DrawGroundPlanes(context, renderScene, bounds.Size);
        DrawGrid(context, renderScene, bounds.Size);
        DrawLines(context, renderScene, bounds.Size);
        DrawPrimitives(context, renderScene, bounds.Size);
        DrawHud(context, renderScene, bounds.Size);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            _lastPointer = null;
            _rightDrag = false;
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null!, NavigationMethod.Unspecified, KeyModifiers.None);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F)
        {
            FitRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        var scene = Scene;
        if (scene is null) return;
        var camera = _camera ?? scene.Camera;

        if (IsOrbitCamera(camera) && (e.Key == Key.Up || e.Key == Key.Down))
        {
            var target = camera.OrbitTarget ?? ThreeDVector3.Zero;
            var pitch = Math.Clamp(camera.PitchDegrees + (e.Key == Key.Up ? -4 : 4), -80, 80);
            var distance = Math.Clamp(camera.OrbitDistance ?? Distance(camera.Position, target), 2, 10000);
            _camera = camera with
            {
                PitchDegrees = pitch,
                OrbitDistance = distance,
                Position = ThreeDProjection.OrbitPosition(target, camera.YawDegrees, pitch, distance)
            };
            CameraChanged?.Invoke(this, _camera);
            e.Handled = true;
            return;
        }

        var forward = Forward(camera);
        var right = Right(camera);
        var up = new ThreeDVector3(0, 1, 0);
        var speed = Math.Max(0.1, camera.MovementSpeed) * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 3 : 1);
        var delta = e.Key switch
        {
            Key.W => Scale(forward, speed),
            Key.S => Scale(forward, -speed),
            Key.A => Scale(right, -speed),
            Key.D => Scale(right, speed),
            Key.Q => Scale(up, -speed),
            Key.E => Scale(up, speed),
            Key.R => ThreeDVector3.Zero,
            _ => (ThreeDVector3?)null
        };
        if (delta is null) return;
        camera = e.Key == Key.R
            ? ResetCamera(scene.Camera, camera)
            : camera with { Position = Add(camera.Position, delta) };
        _camera = camera;
        CameraChanged?.Invoke(this, camera);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed) return;
        Focus();
        _lastPointer = e.GetPosition(this);
        _rightDrag = point.Properties.IsRightButtonPressed && !point.Properties.IsLeftButtonPressed;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_lastPointer is not { } previous) return;
        var properties = e.GetCurrentPoint(this).Properties;
        var dragging = _rightDrag ? properties.IsRightButtonPressed : properties.IsLeftButtonPressed;
        if (!dragging) return;
        var current = e.GetPosition(this);
        var camera = _camera ?? Scene?.Camera;
        if (camera is null) return;
        if (IsOrbitCamera(camera))
        {
            var target = camera.OrbitTarget ?? ThreeDVector3.Zero;
            var distance = Math.Max(1, camera.OrbitDistance ?? Distance(camera.Position, target));
            var yaw = camera.YawDegrees + (current.X - previous.X) * 0.25;
            var pitch = Math.Clamp(camera.PitchDegrees + (current.Y - previous.Y) * 0.25, -80, 80);
            _camera = camera with
            {
                YawDegrees = yaw,
                PitchDegrees = pitch,
                Position = ThreeDProjection.OrbitPosition(target, yaw, pitch, distance),
                OrbitDistance = distance
            };
        }
        else
        {
            _camera = camera with
            {
                YawDegrees = camera.YawDegrees + (current.X - previous.X) * 0.25,
                PitchDegrees = Math.Clamp(camera.PitchDegrees + (current.Y - previous.Y) * 0.25, -89, 89)
            };
        }
        _lastPointer = current;
        CameraChanged?.Invoke(this, _camera);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _lastPointer = null;
        _rightDrag = false;
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var camera = _camera ?? Scene?.Camera;
        if (camera is null) return;
        if (IsOrbitCamera(camera))
        {
            var target = camera.OrbitTarget ?? ThreeDVector3.Zero;
            var distance = Math.Clamp(camera.OrbitDistance ?? Distance(camera.Position, target), 2, 10000) * (e.Delta.Y > 0 ? 0.87 : 1.15);
            _camera = camera with
            {
                OrbitDistance = distance,
                Position = ThreeDProjection.OrbitPosition(target, camera.YawDegrees, camera.PitchDegrees, distance)
            };
        }
        else
        {
            _camera = camera with { MovementSpeed = Math.Clamp(camera.MovementSpeed * (e.Delta.Y > 0 ? 1.15 : 0.87), 0.2, 500) };
        }
        CameraChanged?.Invoke(this, _camera);
        e.Handled = true;
    }

    private void DrawGrid(DrawingContext context, ThreeDSceneSnapshot scene, Size size)
    {
        if (!scene.Grid.Visible) return;
        var pen = new Pen(new SolidColorBrush(Color.Parse(scene.Grid.Color)), 1);
        var requestedSpacing = Math.Max(0.1, scene.Grid.SpacingMetres);
        var footprint = ThreeDProjection.GroundFootprint(scene.Camera, size.Width, size.Height);
        var center = GroundCenter(scene.Camera);
        var stableHalfExtent = StableGridHalfExtent(scene.Camera, scene.Grid.SizeMetres / 2);
        var minX = center.X - stableHalfExtent;
        var maxX = center.X + stableHalfExtent;
        var minZ = center.Z - stableHalfExtent;
        var maxZ = center.Z + stableHalfExtent;
        if (footprint.Count >= 2)
        {
            minX = Math.Min(minX, footprint.Min(point => point.X));
            maxX = Math.Max(maxX, footprint.Max(point => point.X));
            minZ = Math.Min(minZ, footprint.Min(point => point.Z));
            maxZ = Math.Max(maxZ, footprint.Max(point => point.Z));
        }
        var requestedExtent = Math.Max(maxX - minX, maxZ - minZ);
        var spacing = AdaptiveGridSpacing(requestedSpacing, Math.Max(scene.Grid.SizeMetres, requestedExtent) / 2);
        var margin = spacing * 2;
        minX -= margin;
        maxX += margin;
        minZ -= margin;
        maxZ += margin;
        minX = Math.Floor(minX / spacing) * spacing;
        maxX = Math.Ceiling(maxX / spacing) * spacing;
        minZ = Math.Floor(minZ / spacing) * spacing;
        maxZ = Math.Ceiling(maxZ / spacing) * spacing;
        for (var x = minX; x <= maxX; x += spacing)
        {
            DrawWorldLine(context, pen, new(x, 0, minZ), new(x, 0, maxZ), scene.Camera, size);
        }
        for (var z = minZ; z <= maxZ; z += spacing)
        {
            DrawWorldLine(context, pen, new(minX, 0, z), new(maxX, 0, z), scene.Camera, size);
        }
        if (scene.Axes.Visible)
        {
            DrawWorldLine(context, new Pen(Brushes.CornflowerBlue, 2), ThreeDVector3.Zero, new(scene.Axes.LengthMetres, 0, 0), scene.Camera, size);
            DrawWorldLine(context, new Pen(Brushes.LimeGreen, 2), ThreeDVector3.Zero, new(0, scene.Axes.LengthMetres, 0), scene.Camera, size);
            DrawWorldLine(context, new Pen(Brushes.Orange, 2), ThreeDVector3.Zero, new(0, 0, scene.Axes.LengthMetres), scene.Camera, size);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible && _attached) _timer.Start();
            else _timer.Stop();
        }
    }

    private static void DrawGroundPlanes(DrawingContext context, ThreeDSceneSnapshot scene, Size size)
    {
        foreach (var plane in scene.Primitives.Where(item => item.Kind == ThreeDPrimitiveKind.GroundPlane))
        {
            var center = GroundCenter(scene.Camera) with { Y = plane.Transform.Position.Y };
            var half = InfiniteGroundHalfExtent(scene.Camera,
                Math.Max(Math.Abs(plane.Transform.Scale.X), Math.Abs(plane.Transform.Scale.Z)) / 2);
            var corners = new[]
            {
                new ThreeDVector3(center.X - half, center.Y, center.Z - half),
                new ThreeDVector3(center.X + half, center.Y, center.Z - half),
                new ThreeDVector3(center.X + half, center.Y, center.Z + half),
                new ThreeDVector3(center.X - half, center.Y, center.Z + half)
            };
            var projected = ThreeDProjection.ProjectPolygon(corners, scene.Camera, size.Width, size.Height);
            if (projected.Count < 3) continue;
            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(new Point(projected[0].X, projected[0].Y), true);
                for (var index = 1; index < projected.Count; index++)
                    geometryContext.LineTo(new Point(projected[index].X, projected[index].Y));
                geometryContext.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse(plane.Color)), null, geometry);
        }
    }

    private static ThreeDVector3 GroundCenter(ThreeDCameraSnapshot camera)
    {
        var focus = camera.OrbitTarget;
        return focus is not null
            ? new(focus.X, 0, focus.Z)
            : new(camera.Position.X, 0, camera.Position.Z);
    }

    private static double InfiniteGroundHalfExtent(ThreeDCameraSnapshot camera, double minimum)
        => StableCoverageHalfExtent(camera, minimum);

    private static double StableGridHalfExtent(ThreeDCameraSnapshot camera, double minimum)
        => StableCoverageHalfExtent(camera, minimum);

    private static double StableCoverageHalfExtent(ThreeDCameraSnapshot camera, double minimum)
    {
        var cameraDistance = Math.Sqrt(
            camera.Position.X * camera.Position.X +
            camera.Position.Z * camera.Position.Z +
            camera.Position.Y * camera.Position.Y);
        var viewRange = camera.OrbitMode
            ? (camera.OrbitDistance ?? cameraDistance) * 8
            : camera.FarClip * 0.5;
        return Math.Clamp(Math.Max(500, Math.Max(minimum, viewRange)), 500, 100000);
    }

    private static double AdaptiveGridSpacing(double requestedSpacing, double halfExtent)
    {
        var spacing = Math.Max(0.1, requestedSpacing);
        var lineCount = halfExtent * 2 / spacing;
        if (lineCount > 180) spacing *= Math.Ceiling(lineCount / 180);
        return spacing;
    }

    private static void DrawLines(DrawingContext context, ThreeDSceneSnapshot scene, Size size)
    {
        foreach (var line in scene.Lines)
        {
            if (line.Points.Count < 2) continue;
            var pen = new Pen(new SolidColorBrush(Color.Parse(line.Color)), Math.Clamp(line.Width, 1, 6));
            for (var i = 1; i < line.Points.Count; i++) DrawWorldLine(context, pen, line.Points[i - 1], line.Points[i], scene.Camera, size);
            if (line.Closed) DrawWorldLine(context, pen, line.Points[^1], line.Points[0], scene.Camera, size);
        }
    }

    private static void DrawPrimitives(DrawingContext context, ThreeDSceneSnapshot scene, Size size)
    {
        foreach (var primitive in scene.Primitives.OrderByDescending(item => ThreeDProjection.Project(item.Transform.Position, scene.Camera, size.Width, size.Height).Depth))
        {
            if (primitive.Kind == ThreeDPrimitiveKind.GroundPlane) continue;
            var projected = ThreeDProjection.Project(primitive.Transform.Position, scene.Camera, size.Width, size.Height);
            if (!projected.Visible) continue;
            var color = new SolidColorBrush(Color.Parse(primitive.Selected || primitive.TeamSelected ? "#FFD166" : primitive.Color));
            if (primitive.Kind == ThreeDPrimitiveKind.Arrow)
            {
                var endpoint = new ThreeDVector3(primitive.Transform.Position.X,
                    primitive.Transform.Position.Y,
                    primitive.Transform.Position.Z + Math.Max(4, primitive.Transform.Scale.Z));
                var projectedEnd = ThreeDProjection.Project(endpoint, scene.Camera, size.Width, size.Height);
                if (projectedEnd.Visible)
                {
                    var start = new Point(projected.X, projected.Y);
                    var end = new Point(projectedEnd.X, projectedEnd.Y);
                    var direction = end - start;
                    var magnitude = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
                    context.DrawLine(new Pen(color, 4), start, end);
                    if (magnitude > 0.1)
                    {
                        var unit = direction / magnitude;
                        var perpendicular = new Vector(-unit.Y, unit.X);
                        var head = end - unit * 14;
                        context.DrawLine(new Pen(color, 4), end, head + perpendicular * 7);
                        context.DrawLine(new Pen(color, 4), end, head - perpendicular * 7);
                    }
                }
                if (primitive.Label is { Length: > 0 } arrowLabel)
                    context.DrawText(new FormattedText(arrowLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White), new Point(projected.X + 5, projected.Y - 8));
                continue;
            }
            var radius = primitive.Kind == ThreeDPrimitiveKind.Point ? 6 : (primitive.Selected || primitive.TeamSelected ? 11 : 9);
            context.DrawEllipse(color, null, new Point(projected.X, projected.Y), radius, radius);
            if (primitive.Label is { Length: > 0 } label)
                context.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White), new Point(projected.X + radius + 3, projected.Y - 8));
        }
    }

    private static void DrawHud(DrawingContext context, ThreeDSceneSnapshot scene, Size size)
    {
        if (!string.IsNullOrWhiteSpace(scene.Hud.CameraLabel))
            context.DrawText(new FormattedText(scene.Hud.CameraLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.LightGray), new Point(12, size.Height - 24));
    }

    private static void DrawWorldLine(DrawingContext context, Pen pen, ThreeDVector3 from, ThreeDVector3 to, ThreeDCameraSnapshot camera, Size size)
    {
        if (!ThreeDProjection.TryProjectSegment(from, to, camera, size.Width, size.Height, out var a, out var b)) return;
        context.DrawLine(pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
    }

    private static ThreeDVector3 Forward(ThreeDCameraSnapshot camera)
    {
        var yaw = camera.YawDegrees * Math.PI / 180d;
        var pitch = camera.PitchDegrees * Math.PI / 180d;
        return new(Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch));
    }

    private static ThreeDVector3 Right(ThreeDCameraSnapshot camera)
    {
        var yaw = camera.YawDegrees * Math.PI / 180d;
        return new(Math.Cos(yaw), 0, -Math.Sin(yaw));
    }

    private static ThreeDVector3 Add(ThreeDVector3 a, ThreeDVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static ThreeDVector3 Scale(ThreeDVector3 value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    private static double Distance(ThreeDVector3 a, ThreeDVector3 b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    private static ThreeDCameraSnapshot ResetCamera(ThreeDCameraSnapshot sceneCamera, ThreeDCameraSnapshot currentCamera)
    {
        if (!(currentCamera.OrbitMode || currentCamera.OrbitTarget is not null))
            return sceneCamera with { Position = new ThreeDVector3(0, 60, -120), YawDegrees = 0, PitchDegrees = -18 };

        var target = currentCamera.OrbitTarget ?? ThreeDVector3.Zero;
        var distance = Math.Clamp(currentCamera.OrbitDistance ?? Distance(currentCamera.Position, target), 2, 10000);
        return sceneCamera with
        {
            Position = ThreeDProjection.OrbitPosition(target, 0, ThreeDProjection.DefaultOrbitPitchDegrees, distance),
            YawDegrees = 0,
            PitchDegrees = ThreeDProjection.DefaultOrbitPitchDegrees,
            OrbitMode = true,
            OrbitTarget = target,
            OrbitDistance = distance
        };
    }

    private bool IsOrbitCamera(ThreeDCameraSnapshot camera)
        => OrbitControls || camera.OrbitMode || camera.OrbitTarget is not null;
}
