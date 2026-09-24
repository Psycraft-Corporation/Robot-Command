using RobotCommand.Core;

namespace RobotCommand.Rendering;

/// <summary>Rasterizes the common 3D scene render plan into portable BGRA video frames.</summary>
public static class ThreeDSceneFrameRenderer
{
    public static byte[] Render(ThreeDSceneSnapshot scene, int width, int height, string? excludedPrimitiveId = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var plan = ThreeDSceneRenderPlan.Build(scene, width, height, excludedPrimitiveId);
        var pixels = new byte[checked(width * height * 4)];
        Fill(pixels, width, height, plan.Background);
        foreach (var plane in plan.GroundPlanes)
            FillPolygon(pixels, width, height, plane.Points, plane.Color);
        foreach (var line in plan.Lines)
            DrawLine(pixels, width, height, line.Start, line.End, line.Color, (int)Math.Round(line.Width));
        foreach (var primitive in plan.Primitives)
        {
            if (primitive.Kind == ThreeDPrimitiveKind.Arrow)
            {
                if (primitive.ArrowEnd is { } end)
                    DrawArrow(pixels, width, height, primitive.Center, end, primitive.Color);
                continue;
            }
            DrawDisc(pixels, width, height, primitive.Center.X, primitive.Center.Y,
                (int)Math.Round(primitive.Radius), primitive.Color);
        }
        return pixels;
    }

    private static void Fill(byte[] pixels, int width, int height, ThreeDColor color)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = color.Blue;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Red;
            pixels[offset + 3] = 255;
        }
    }

    private static void FillPolygon(byte[] pixels, int width, int height, IReadOnlyList<ProjectedPoint> polygon, ThreeDColor color)
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
            {
                var start = Math.Max(0, (int)Math.Ceiling(intersections[i]));
                var end = Math.Min(width - 1, (int)Math.Ceiling(intersections[i + 1]));
                for (var x = start; x <= end; x++) SetPixel(pixels, width, height, x, y, color);
            }
        }
    }

    private static void DrawArrow(byte[] pixels, int width, int height, ProjectedPoint from, ProjectedPoint to, ThreeDColor color)
    {
        DrawLine(pixels, width, height, from, to, color, 4);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0.1) return;
        var ux = dx / length;
        var uy = dy / length;
        var headX = to.X - ux * 14;
        var headY = to.Y - uy * 14;
        var perpendicularX = -uy * 7;
        var perpendicularY = ux * 7;
        DrawLine(pixels, width, height, to, new(headX + perpendicularX, headY + perpendicularY, to.Depth, to.Visible), color, 4);
        DrawLine(pixels, width, height, to, new(headX - perpendicularX, headY - perpendicularY, to.Depth, to.Visible), color, 4);
    }

    private static void DrawLine(byte[] pixels, int width, int height, ProjectedPoint from, ProjectedPoint to, ThreeDColor color, int lineWidth)
    {
        var x0 = from.X;
        var y0 = from.Y;
        var dxClip = to.X - from.X;
        var dyClip = to.Y - from.Y;
        var start = 0d;
        var end = 1d;
        if (!Clip(-dxClip, x0, ref start, ref end) ||
            !Clip(dxClip, width - 1 - x0, ref start, ref end) ||
            !Clip(-dyClip, y0, ref start, ref end) ||
            !Clip(dyClip, height - 1 - y0, ref start, ref end)) return;

        var x1 = from.X + end * dxClip;
        var y1 = from.Y + end * dyClip;
        x0 += start * dxClip;
        y0 += start * dyClip;
        var roundedX0 = (int)Math.Round(x0); var roundedY0 = (int)Math.Round(y0);
        var roundedX1 = (int)Math.Round(x1); var roundedY1 = (int)Math.Round(y1);
        var dx = Math.Abs(roundedX1 - roundedX0); var sx = roundedX0 < roundedX1 ? 1 : -1;
        var dy = -Math.Abs(roundedY1 - roundedY0); var sy = roundedY0 < roundedY1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            DrawDisc(pixels, width, height, roundedX0, roundedY0, Math.Max(0, (lineWidth - 1) / 2), color);
            if (roundedX0 == roundedX1 && roundedY0 == roundedY1) break;
            var twice = 2 * error;
            if (twice >= dy) { error += dy; roundedX0 += sx; }
            if (twice <= dx) { error += dx; roundedY0 += sy; }
        }
    }

    private static bool Clip(double p, double q, ref double start, ref double end)
    {
        if (Math.Abs(p) < 1e-12) return q >= 0;
        var ratio = q / p;
        if (p < 0)
        {
            if (ratio > end) return false;
            if (ratio > start) start = ratio;
        }
        else
        {
            if (ratio < start) return false;
            if (ratio < end) end = ratio;
        }
        return start <= end;
    }

    private static void DrawDisc(byte[] pixels, int width, int height, double centerX, double centerY, int radius, ThreeDColor color)
    {
        var cx = (int)Math.Round(centerX); var cy = (int)Math.Round(centerY);
        for (var y = -radius; y <= radius; y++)
            for (var x = -radius; x <= radius; x++)
                if (x * x + y * y <= radius * radius) SetPixel(pixels, width, height, cx + x, cy + y, color);
    }

    private static void SetPixel(byte[] pixels, int width, int height, int x, int y, ThreeDColor color)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
        var offset = (y * width + x) * 4;
        pixels[offset] = color.Blue;
        pixels[offset + 1] = color.Green;
        pixels[offset + 2] = color.Red;
        pixels[offset + 3] = 255;
    }
}
