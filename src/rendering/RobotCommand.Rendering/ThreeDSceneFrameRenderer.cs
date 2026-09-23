using RobotCommand.Core;

namespace RobotCommand.Rendering;

/// <summary>Renders world-scene snapshots into portable BGRA video frames.</summary>
public static class ThreeDSceneFrameRenderer
{
    public static byte[] Render(ThreeDSceneSnapshot scene, int width, int height, string? excludedPrimitiveId = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var pixels = new byte[checked(width * height * 4)];
        FillBackground(pixels, width, height);
        foreach (var primitive in scene.Primitives)
        {
            if (string.Equals(primitive.Id, excludedPrimitiveId, StringComparison.Ordinal)) continue;
            if (primitive.Kind == ThreeDPrimitiveKind.GroundPlane)
                DrawGround(pixels, width, height, scene.Camera, primitive);
        }

        foreach (var line in scene.Lines)
        {
            var color = ParseColor(line.Color);
            for (var index = 1; index < line.Points.Count; index++)
            {
                if (ThreeDProjection.TryProjectSegment(line.Points[index - 1], line.Points[index], scene.Camera,
                        width, height, out var from, out var to))
                    DrawLine(pixels, width, height, from, to, color);
            }
            if (line.Closed && line.Points.Count > 2 &&
                ThreeDProjection.TryProjectSegment(line.Points[^1], line.Points[0], scene.Camera,
                    width, height, out var last, out var first))
                DrawLine(pixels, width, height, last, first, color);
        }

        foreach (var primitive in scene.Primitives)
        {
            if (primitive.Id == excludedPrimitiveId || primitive.Kind == ThreeDPrimitiveKind.GroundPlane) continue;
            var center = ThreeDProjection.Project(primitive.Transform.Position, scene.Camera, width, height);
            if (!center.Visible) continue;
            var color = ParseColor(primitive.Color);
            if (primitive.Kind is ThreeDPrimitiveKind.Point or ThreeDPrimitiveKind.Marker)
            {
                DrawDisc(pixels, width, height, center.X, center.Y, primitive.Kind == ThreeDPrimitiveKind.Marker ? 4 : 2, color);
                continue;
            }

            var radiusWorld = Math.Max(0.25, Math.Max(primitive.Transform.Scale.X,
                Math.Max(primitive.Transform.Scale.Y, primitive.Transform.Scale.Z)) * 0.5);
            var edge = ThreeDProjection.Project(new(primitive.Transform.Position.X + radiusWorld,
                primitive.Transform.Position.Y, primitive.Transform.Position.Z), scene.Camera, width, height);
            var radius = edge.Visible ? (int)Math.Clamp(Math.Abs(edge.X - center.X), 2, 48) : 3;
            DrawDisc(pixels, width, height, center.X, center.Y, radius, color);
        }
        return pixels;
    }

    private static void DrawGround(byte[] pixels, int width, int height, ThreeDCameraSnapshot camera, ThreeDPrimitiveSnapshot primitive)
    {
        var sx = Math.Max(1, primitive.Transform.Scale.X / 2);
        var sz = Math.Max(1, primitive.Transform.Scale.Z / 2);
        var p = primitive.Transform.Position;
        var polygon = ThreeDProjection.ProjectPolygon(
        [new(p.X - sx, p.Y, p.Z - sz), new(p.X + sx, p.Y, p.Z - sz),
         new(p.X + sx, p.Y, p.Z + sz), new(p.X - sx, p.Y, p.Z + sz)], camera, width, height);
        if (polygon.Count >= 3) FillPolygon(pixels, width, height, polygon, ParseColor(primitive.Color));
    }

    private static void FillBackground(byte[] pixels, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var t = y / (double)Math.Max(1, height - 1);
            var r = (byte)(18 + 16 * t);
            var g = (byte)(42 + 12 * t);
            var b = (byte)(72 + 4 * t);
            for (var x = 0; x < width; x++) SetPixel(pixels, width, height, x, y, (b, g, r));
        }
    }

    private static void FillPolygon(byte[] pixels, int width, int height, IReadOnlyList<ProjectedPoint> polygon, (byte B, byte G, byte R) color)
    {
        var minY = Math.Max(0, (int)Math.Floor(polygon.Min(point => point.Y)));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(polygon.Max(point => point.Y)));
        for (var y = minY; y <= maxY; y++)
        {
            var intersections = new List<double>();
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                    intersections.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            intersections.Sort();
            for (var i = 0; i + 1 < intersections.Count; i += 2)
                for (var x = Math.Max(0, (int)Math.Ceiling(intersections[i])); x <= Math.Min(width - 1, (int)intersections[i + 1]); x++)
                    SetPixel(pixels, width, height, x, y, color);
        }
    }

    private static void DrawLine(byte[] pixels, int width, int height, ProjectedPoint from, ProjectedPoint to, (byte B, byte G, byte R) color)
    {
        var x0 = (int)Math.Round(from.X); var y0 = (int)Math.Round(from.Y);
        var x1 = (int)Math.Round(to.X); var y1 = (int)Math.Round(to.Y);
        var dx = Math.Abs(x1 - x0); var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0); var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            SetPixel(pixels, width, height, x0, y0, color);
            if (x0 == x1 && y0 == y1) break;
            var twice = 2 * error;
            if (twice >= dy) { error += dy; x0 += sx; }
            if (twice <= dx) { error += dx; y0 += sy; }
        }
    }

    private static void DrawDisc(byte[] pixels, int width, int height, double centerX, double centerY, int radius, (byte B, byte G, byte R) color)
    {
        var cx = (int)Math.Round(centerX); var cy = (int)Math.Round(centerY);
        for (var y = -radius; y <= radius; y++)
            for (var x = -radius; x <= radius; x++)
                if (x * x + y * y <= radius * radius) SetPixel(pixels, width, height, cx + x, cy + y, color);
    }

    private static void SetPixel(byte[] pixels, int width, int height, int x, int y, (byte B, byte G, byte R) color)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
        var offset = (y * width + x) * 4;
        pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
    }

    private static (byte B, byte G, byte R) ParseColor(string value)
    {
        if (value.Length == 7 && value[0] == '#' &&
            byte.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return (b, g, r);
        return (180, 180, 180);
    }
}
