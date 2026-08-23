using RobotCommand.Core;

namespace RobotCommand.Rendering;

public readonly record struct ProjectedPoint(double X, double Y, double Depth, bool Visible);

/// <summary>Small renderer-neutral camera projection used by the software backend and tests.</summary>
public static class ThreeDProjection
{
    public const double DefaultOrbitPitchDegrees = 70;

    public static ProjectedPoint Project(ThreeDVector3 world, ThreeDCameraSnapshot camera, double width, double height)
    {
        var cameraPoint = ToCameraSpace(world, camera);
        var x = cameraPoint.X;
        var y = cameraPoint.Y;
        var z = cameraPoint.Z;

        if (!double.IsFinite(z) || z <= camera.NearClip || z > camera.FarClip)
            return new(0, 0, z, false);

        var focal = height / (2d * Math.Tan(camera.FieldOfViewDegrees * Math.PI / 360d));
        return new(width / 2d + x * focal / z, height / 2d - y * focal / z, z, true);
    }

    /// <summary>
    /// Projects a line after clipping it to the camera's near and far planes.
    /// This keeps grid and axis lines visible while the camera moves through
    /// their endpoints.
    /// </summary>
    public static bool TryProjectSegment(
        ThreeDVector3 from,
        ThreeDVector3 to,
        ThreeDCameraSnapshot camera,
        double width,
        double height,
        out ProjectedPoint projectedFrom,
        out ProjectedPoint projectedTo)
    {
        var a = ToCameraSpace(from, camera);
        var b = ToCameraSpace(to, camera);
        if (!ClipSegmentDepth(ref a, ref b, EffectiveNearClip(camera), camera.FarClip))
        {
            projectedFrom = default;
            projectedTo = default;
            return false;
        }

        projectedFrom = ProjectCameraPoint(a, camera, width, height);
        projectedTo = ProjectCameraPoint(b, camera, width, height);
        return projectedFrom.Visible && projectedTo.Visible;
    }

    /// <summary>
    /// Projects a convex polygon after clipping it to the camera's depth
    /// range. The software renderer uses this for ground planes, so a plane
    /// remains filled when the camera is close enough that some corners are
    /// behind the near clip plane.
    /// </summary>
    public static IReadOnlyList<ProjectedPoint> ProjectPolygon(
        IReadOnlyList<ThreeDVector3> polygon,
        ThreeDCameraSnapshot camera,
        double width,
        double height)
    {
        if (polygon.Count < 3) return [];

        var clipped = polygon.Select(point => ToCameraSpace(point, camera)).ToList();
        clipped = ClipPolygonDepth(clipped, EffectiveNearClip(camera), keepGreater: true);
        clipped = ClipPolygonDepth(clipped, camera.FarClip, keepGreater: false);
        if (clipped.Count < 3) return [];

        return clipped.Select(point => ProjectCameraPoint(point, camera, width, height)).ToArray();
    }

    /// <summary>
    /// Returns the intersections of the viewport corner rays with the
    /// horizontal ground plane. Grid generation uses this footprint instead
    /// of a fixed world rectangle, so both grid directions remain present at
    /// oblique camera positions and angles.
    /// </summary>
    public static IReadOnlyList<ThreeDVector3> GroundFootprint(
        ThreeDCameraSnapshot camera,
        double width,
        double height)
    {
        if (width <= 0 || height <= 0) return [];
        var corners = new[]
        {
            new Point2(0, 0),
            new Point2(width, 0),
            new Point2(width, height),
            new Point2(0, height)
        };
        var points = new List<ThreeDVector3>(corners.Length);
        foreach (var corner in corners)
        {
            var ray = ScreenRay(corner.X, corner.Y, camera, width, height);
            if (ray.Y >= -1e-9) continue;
            var distance = -camera.Position.Y / ray.Y;
            if (!double.IsFinite(distance) || distance <= 0 || distance > camera.FarClip) continue;
            points.Add(Add(camera.Position, Scale(ray, distance)) with { Y = 0 });
        }
        return points;
    }

    public static ThreeDVector3 OrbitPosition(ThreeDVector3 target, double yawDegrees, double pitchDegrees, double distance)
    {
        var elevation = pitchDegrees * Math.PI / 180d;
        var heading = yawDegrees * Math.PI / 180d;
        return new(target.X + Math.Sin(heading) * Math.Cos(elevation) * distance,
            target.Y + Math.Sin(elevation) * distance,
            target.Z - Math.Cos(heading) * Math.Cos(elevation) * distance);
    }

    private static ThreeDVector3 ToCameraSpace(ThreeDVector3 world, ThreeDCameraSnapshot camera)
    {
        var translated = new ThreeDVector3(
            world.X - camera.Position.X,
            world.Y - camera.Position.Y,
            world.Z - camera.Position.Z);

        var yaw = -camera.YawDegrees * Math.PI / 180d;
        var pitch = -camera.PitchDegrees * Math.PI / 180d;
        var x = translated.X * Math.Cos(yaw) - translated.Z * Math.Sin(yaw);
        var z = translated.X * Math.Sin(yaw) + translated.Z * Math.Cos(yaw);
        var y = translated.Y * Math.Cos(pitch) - z * Math.Sin(pitch);
        z = translated.Y * Math.Sin(pitch) + z * Math.Cos(pitch);
        return new(x, y, z);
    }

    private static ThreeDVector3 ScreenRay(double x, double y, ThreeDCameraSnapshot camera, double width, double height)
    {
        var focal = height / (2d * Math.Tan(camera.FieldOfViewDegrees * Math.PI / 360d));
        var cameraX = (x - width / 2d) / focal;
        var cameraY = -(y - height / 2d) / focal;
        var cameraZ = 1d;

        var pitch = camera.PitchDegrees * Math.PI / 180d;
        var yaw = camera.YawDegrees * Math.PI / 180d;
        var firstY = cameraY * Math.Cos(pitch) - cameraZ * Math.Sin(pitch);
        var firstZ = cameraY * Math.Sin(pitch) + cameraZ * Math.Cos(pitch);
        return new(
            cameraX * Math.Cos(yaw) - firstZ * Math.Sin(yaw),
            firstY,
            cameraX * Math.Sin(yaw) + firstZ * Math.Cos(yaw));
    }

    private static ProjectedPoint ProjectCameraPoint(ThreeDVector3 point, ThreeDCameraSnapshot camera, double width, double height)
    {
        var focal = height / (2d * Math.Tan(camera.FieldOfViewDegrees * Math.PI / 360d));
        return new(width / 2d + point.X * focal / point.Z,
            height / 2d - point.Y * focal / point.Z,
            point.Z,
            double.IsFinite(point.Z) && point.Z > camera.NearClip && point.Z <= camera.FarClip);
    }

    private static double EffectiveNearClip(ThreeDCameraSnapshot camera)
        => camera.NearClip + Math.Max(1e-6, camera.NearClip * 1e-6);

    private static bool ClipSegmentDepth(ref ThreeDVector3 from, ref ThreeDVector3 to, double near, double far)
    {
        var delta = Subtract(to, from);
        var start = 0d;
        var end = 1d;
        if (!ClipSegmentBoundary(from.Z, delta.Z, near, keepGreater: true, ref start, ref end) ||
            !ClipSegmentBoundary(from.Z, delta.Z, far, keepGreater: false, ref start, ref end))
            return false;

        from = Add(from, Scale(delta, start));
        to = Add(from, Scale(delta, end - start));
        return true;
    }

    private static bool ClipSegmentBoundary(double startDepth, double deltaDepth, double boundary, bool keepGreater, ref double start, ref double end)
    {
        var startValue = startDepth + deltaDepth * start - boundary;
        var endValue = startDepth + deltaDepth * end - boundary;
        if (!keepGreater)
        {
            startValue = -startValue;
            endValue = -endValue;
        }

        var startInside = startValue >= 0;
        var endInside = endValue >= 0;
        if (startInside && endInside) return true;
        if (!startInside && !endInside) return false;

        var denominator = startValue - endValue;
        if (Math.Abs(denominator) < 1e-12) return false;
        var intersection = startValue / denominator;
        if (!startInside) start = start + (end - start) * intersection;
        else end = start + (end - start) * intersection;
        return start <= end;
    }

    private static List<ThreeDVector3> ClipPolygonDepth(List<ThreeDVector3> polygon, double boundary, bool keepGreater)
    {
        if (polygon.Count == 0) return polygon;
        var result = new List<ThreeDVector3>(polygon.Count + 2);
        var previous = polygon[^1];
        var previousInside = keepGreater ? previous.Z >= boundary : previous.Z <= boundary;

        foreach (var current in polygon)
        {
            var currentInside = keepGreater ? current.Z >= boundary : current.Z <= boundary;
            if (currentInside != previousInside)
            {
                var denominator = current.Z - previous.Z;
                if (Math.Abs(denominator) > 1e-12)
                {
                    var fraction = (boundary - previous.Z) / denominator;
                    result.Add(Add(previous, Scale(Subtract(current, previous), fraction)));
                }
            }
            if (currentInside) result.Add(current);
            previous = current;
            previousInside = currentInside;
        }

        return result;
    }

    private static ThreeDVector3 Add(ThreeDVector3 a, ThreeDVector3 b)
        => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static ThreeDVector3 Subtract(ThreeDVector3 a, ThreeDVector3 b)
        => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static ThreeDVector3 Scale(ThreeDVector3 value, double scale)
        => new(value.X * scale, value.Y * scale, value.Z * scale);

    private readonly record struct Point2(double X, double Y);
}

public sealed class NullThreeDRenderer : IThreeDRenderer
{
    private ThreeDRendererStatus _status = ThreeDRendererStatus.Uninitialized with { Backend = "None" };
    public ThreeDRendererStatus Status => _status;

    public Task InitializeAsync(ThreeDRenderBackendPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _status = new("None", false, true, false, "No renderer was requested.", 0, 0);
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RenderAsync(ThreeDSceneSnapshot scene, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
