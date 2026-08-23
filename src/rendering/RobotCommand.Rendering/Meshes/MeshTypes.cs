using System.Numerics;
using RobotCommand.Core;

namespace RobotCommand.Rendering.Meshes;

public enum MeshDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record MeshDiagnostic(
    MeshDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Source = null,
    int? Line = null);

public sealed record MeshLoadLimits(
    int MaxFileBytes = 256 * 1024 * 1024,
    int MaxVertices = 2_000_000,
    int MaxIndices = 6_000_000,
    int MaxTriangles = 2_000_000,
    int MaxNodes = 10_000)
{
    public void Validate()
    {
        if (MaxFileBytes <= 0 || MaxVertices <= 0 || MaxIndices <= 0 || MaxTriangles <= 0 || MaxNodes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFileBytes), "Mesh load limits must be positive.");
    }
}

public sealed record MeshLoadOptions(
    MeshLoadLimits? Limits = null,
    bool GenerateMissingNormals = true,
    bool PreserveMaterials = true)
{
    public MeshLoadLimits EffectiveLimits => Limits ?? new();
}

public readonly record struct MeshVector2(float X, float Y)
{
    public static MeshVector2 Zero => new(0, 0);
}

public sealed record MeshVertex(
    ThreeDVector3 Position,
    ThreeDVector3 Normal,
    MeshVector2 TexCoord,
    bool HasNormal = true);

public sealed record MeshMaterialSnapshot(
    string Name,
    string BaseColorHex = "#B8C7D9",
    float Opacity = 1f);

public sealed record MeshPrimitiveSnapshot(
    string Id,
    string NodePath,
    IReadOnlyList<MeshVertex> Vertices,
    IReadOnlyList<int> Indices,
    MeshMaterialSnapshot? Material,
    Matrix4x4 Transform);

public sealed record MeshNodeSnapshot(
    string Id,
    string Name,
    int? ParentIndex,
    Matrix4x4 LocalTransform,
    IReadOnlyList<int> PrimitiveIndices);

public sealed record MeshBounds(ThreeDVector3 Minimum, ThreeDVector3 Maximum)
{
    public ThreeDVector3 Center => new(
        (Minimum.X + Maximum.X) / 2,
        (Minimum.Y + Maximum.Y) / 2,
        (Minimum.Z + Maximum.Z) / 2);

    public ThreeDVector3 Size => new(
        Maximum.X - Minimum.X,
        Maximum.Y - Minimum.Y,
        Maximum.Z - Minimum.Z);

    public static MeshBounds Empty => new(
        new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));
}

public sealed record MeshAssetSnapshot(
    string SourceName,
    string Format,
    string CoordinateSystem,
    IReadOnlyList<MeshPrimitiveSnapshot> Primitives,
    IReadOnlyList<MeshNodeSnapshot> Nodes,
    IReadOnlyList<MeshMaterialSnapshot> Materials,
    MeshBounds Bounds,
    IReadOnlyList<MeshDiagnostic> Diagnostics)
{
    public int VertexCount => Primitives.Sum(item => item.Vertices.Count);
    public int IndexCount => Primitives.Sum(item => item.Indices.Count);
    public int TriangleCount => Primitives.Sum(item => item.Indices.Count / 3);
}

public sealed record MeshLoadResult(
    MeshAssetSnapshot? Asset,
    IReadOnlyList<MeshDiagnostic> Diagnostics)
{
    public bool Success => Asset is not null && Diagnostics.All(item => item.Severity != MeshDiagnosticSeverity.Error);
}

public enum MeshRenderMode
{
    Solid,
    Wireframe
}

public sealed record MeshRenderOptions(
    int Width = 640,
    int Height = 480,
    MeshRenderMode Mode = MeshRenderMode.Solid,
    string BackgroundHex = "#0B1118");

public sealed record MeshRenderImage(int Width, int Height, byte[] Rgb24)
{
    public void SavePpm(string path)
    {
        if (Rgb24.Length != Width * Height * 3) throw new InvalidOperationException("The image buffer is invalid.");
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write($"P6\n{Width} {Height}\n255\n");
        writer.Flush();
        stream.Write(Rgb24);
    }
}

internal static class MeshMath
{
    public static ThreeDVector3 ToThreeD(Vector3 value) => new(value.X, value.Y, value.Z);

    public static Vector3 ToNumerics(ThreeDVector3 value) => new((float)value.X, (float)value.Y, (float)value.Z);

    public static ThreeDVector3 Normalize(ThreeDVector3 value)
    {
        var length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        return length > 1e-9 && double.IsFinite(length)
            ? new(value.X / length, value.Y / length, value.Z / length)
            : new(0, 1, 0);
    }

    public static ThreeDVector3 Cross(ThreeDVector3 a, ThreeDVector3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static ThreeDVector3 Subtract(ThreeDVector3 a, ThreeDVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static ThreeDVector3 Add(ThreeDVector3 a, ThreeDVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static ThreeDVector3 Transform(ThreeDVector3 value, Matrix4x4 matrix)
        => ToThreeD(Vector3.Transform(ToNumerics(value), matrix));

    public static string ColorHex(float r, float g, float b)
        => $"#{Math.Clamp((int)Math.Round(r * 255), 0, 255):X2}{Math.Clamp((int)Math.Round(g * 255), 0, 255):X2}{Math.Clamp((int)Math.Round(b * 255), 0, 255):X2}";
}
