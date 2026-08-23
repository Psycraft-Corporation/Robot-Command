using System.Globalization;
using RobotCommand.Core;
using RobotCommand.Rendering;

namespace RobotCommand.Rendering.Meshes;

public sealed record MeshTriangle(ThreeDVector3 A, ThreeDVector3 B, ThreeDVector3 C, string Color);

public static class MeshSceneConversion
{
    public static IReadOnlyList<MeshTriangle> ToTriangles(MeshAssetSnapshot asset)
    {
        var triangles = new List<MeshTriangle>();
        foreach (var primitive in asset.Primitives)
        {
            var color = primitive.Material?.BaseColorHex ?? "#B8C7D9";
            for (var i = 0; i + 2 < primitive.Indices.Count; i += 3)
            {
                var a = MeshMath.Transform(primitive.Vertices[primitive.Indices[i]].Position, primitive.Transform);
                var b = MeshMath.Transform(primitive.Vertices[primitive.Indices[i + 1]].Position, primitive.Transform);
                var c = MeshMath.Transform(primitive.Vertices[primitive.Indices[i + 2]].Position, primitive.Transform);
                triangles.Add(new(a, b, c, color));
            }
        }
        return triangles;
    }

    public static ThreeDCameraSnapshot FitCamera(MeshAssetSnapshot asset, int width = 640, int height = 480)
    {
        var center = asset.Bounds.Center;
        var size = asset.Bounds.Size;
        var radius = Math.Max(1, Math.Sqrt(size.X * size.X + size.Y * size.Y + size.Z * size.Z) / 2);
        return new(
            new(center.X, center.Y + Math.Max(radius * 0.9, radius), center.Z - Math.Max(radius * 2.8, 10)),
            0,
            -18,
            0,
            60,
            Math.Max(0.01, radius / 100),
            Math.Max(1000, radius * 20),
            Math.Clamp(radius / 12, 0.2, 100));
    }
}

public static class MeshSoftwareRenderer
{
    public static MeshRenderImage Render(MeshAssetSnapshot asset, MeshRenderOptions? options = null)
    {
        options ??= new MeshRenderOptions();
        if (options.Width <= 0 || options.Height <= 0 || options.Width > 4096 || options.Height > 4096)
            throw new ArgumentOutOfRangeException(nameof(options), "Mesh render dimensions must be between 1 and 4096 pixels.");

        var pixels = new byte[checked(options.Width * options.Height * 3)];
        var background = ParseColor(options.BackgroundHex);
        for (var i = 0; i < pixels.Length; i += 3) { pixels[i] = background.r; pixels[i + 1] = background.g; pixels[i + 2] = background.b; }
        var depth = Enumerable.Repeat(float.PositiveInfinity, options.Width * options.Height).ToArray();
        var camera = MeshSceneConversion.FitCamera(asset, options.Width, options.Height);
        foreach (var triangle in MeshSceneConversion.ToTriangles(asset))
        {
            var a = ThreeDProjection.Project(triangle.A, camera, options.Width, options.Height);
            var b = ThreeDProjection.Project(triangle.B, camera, options.Width, options.Height);
            var c = ThreeDProjection.Project(triangle.C, camera, options.Width, options.Height);
            if (!a.Visible || !b.Visible || !c.Visible) continue;
            var color = ParseColor(triangle.Color);
            if (options.Mode == MeshRenderMode.Wireframe)
            {
                DrawLine(pixels, options.Width, options.Height, a.X, a.Y, b.X, b.Y, color);
                DrawLine(pixels, options.Width, options.Height, b.X, b.Y, c.X, c.Y, color);
                DrawLine(pixels, options.Width, options.Height, c.X, c.Y, a.X, a.Y, color);
                continue;
            }
            FillTriangle(pixels, depth, options.Width, options.Height, a, b, c, color);
        }
        return new(options.Width, options.Height, pixels);
    }

    private static void FillTriangle(byte[] pixels, float[] depth, int width, int height, ProjectedPoint a, ProjectedPoint b, ProjectedPoint c, (byte r, byte g, byte b) color)
    {
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        var area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (Math.Abs(area) < 1e-6) return;
        for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
        {
            var px = x + 0.5;
            var py = y + 0.5;
            var w0 = Edge(b.X, b.Y, c.X, c.Y, px, py) / area;
            var w1 = Edge(c.X, c.Y, a.X, a.Y, px, py) / area;
            var w2 = Edge(a.X, a.Y, b.X, b.Y, px, py) / area;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            var z = (float)(w0 * a.Depth + w1 * b.Depth + w2 * c.Depth);
            var index = y * width + x;
            if (z >= depth[index]) continue;
            depth[index] = z;
            var offset = index * 3;
            pixels[offset] = color.r; pixels[offset + 1] = color.g; pixels[offset + 2] = color.b;
        }
    }

    private static void DrawLine(byte[] pixels, int width, int height, double x0, double y0, double x1, double y1, (byte r, byte g, byte b) color)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))));
        for (var i = 0; i <= steps; i++)
        {
            var x = (int)Math.Round(x0 + dx * i / steps);
            var y = (int)Math.Round(y0 + dy * i / steps);
            if (x < 0 || y < 0 || x >= width || y >= height) continue;
            var offset = (y * width + x) * 3;
            pixels[offset] = color.r; pixels[offset + 1] = color.g; pixels[offset + 2] = color.b;
        }
    }

    private static double Edge(double ax, double ay, double bx, double by, double cx, double cy)
        => (cx - ax) * (by - ay) - (cy - ay) * (bx - ax);

    private static (byte r, byte g, byte b) ParseColor(string value)
    {
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return (184, 199, 217);
        return ((byte)(rgb >> 16), (byte)((rgb >> 8) & 255), (byte)(rgb & 255));
    }
}
