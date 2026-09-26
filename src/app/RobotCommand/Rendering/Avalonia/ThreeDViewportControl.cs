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
        context.FillRectangle(ToBrush(ThreeDColor.Parse("#0B1118")), bounds);
        if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var scene = Scene;
        if (scene is null) return;
        if (_camera is null || !_camera.Equals(scene.Camera))
            _camera = scene.Camera;

        var renderScene = scene with { Camera = _camera };
        var plan = ThreeDSceneRenderPlan.Build(renderScene, bounds.Width, bounds.Height);
        context.FillRectangle(ToBrush(plan.Background), bounds);
        DrawPlan(context, plan);
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

    private static void DrawPlan(DrawingContext context, ThreeDSceneRenderPlan plan)
    {
        foreach (var ground in plan.GroundPlanes)
        {
            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(new Point(ground.Points[0].X, ground.Points[0].Y), true);
                for (var index = 1; index < ground.Points.Count; index++)
                    geometryContext.LineTo(new Point(ground.Points[index].X, ground.Points[index].Y));
                geometryContext.EndFigure(true);
            }
            context.DrawGeometry(ToBrush(ground.Color), null, geometry);
        }

        foreach (var line in plan.Lines)
            context.DrawLine(new Pen(ToBrush(line.Color), line.Width),
                new Point(line.Start.X, line.Start.Y), new Point(line.End.X, line.End.Y));

        foreach (var primitive in plan.Primitives)
        {
            var brush = ToBrush(primitive.Color);
            var center = new Point(primitive.Center.X, primitive.Center.Y);
            if (primitive.Kind == ThreeDPrimitiveKind.Arrow)
            {
                if (primitive.ArrowEnd is { } arrowEnd)
                {
                    var end = new Point(arrowEnd.X, arrowEnd.Y);
                    var start = center;
                    var direction = end - start;
                    var magnitude = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
                    context.DrawLine(new Pen(brush, 4), start, end);
                    if (magnitude > 0.1)
                    {
                        var unit = direction / magnitude;
                        var perpendicular = new Vector(-unit.Y, unit.X);
                        var head = end - unit * 14;
                        context.DrawLine(new Pen(brush, 4), end, head + perpendicular * 7);
                        context.DrawLine(new Pen(brush, 4), end, head - perpendicular * 7);
                    }
                }
                if (primitive.Label is { Length: > 0 } arrowLabel)
                    DrawLabel(context, arrowLabel, center);
                continue;
            }

            context.DrawEllipse(brush, null, center, primitive.Radius, primitive.Radius);
            if (primitive.Label is { Length: > 0 } label)
                DrawLabel(context, label, center + new Vector(primitive.Radius + 3, -8));
        }
    }

    private static void DrawLabel(DrawingContext context, string label, Point position)
        => context.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, Brushes.White), position);

    private static SolidColorBrush ToBrush(ThreeDColor color)
        => new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible && _attached) _timer.Start();
            else _timer.Stop();
        }
    }

    private static void DrawHud(DrawingContext context, ThreeDSceneSnapshot scene, Size size)
    {
        if (!string.IsNullOrWhiteSpace(scene.Hud.CameraLabel))
            context.DrawText(new FormattedText(scene.Hud.CameraLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.LightGray), new Point(12, size.Height - 24));
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
