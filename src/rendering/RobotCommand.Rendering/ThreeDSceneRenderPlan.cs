using RobotCommand.Core;

namespace RobotCommand.Rendering;

/// <summary>
/// Surface-independent projected content for a 3D scene. Interactive viewports
/// and offscreen camera feeds consume this same plan so their world geometry,
/// clipping, ground coverage, and grid placement stay aligned.
/// </summary>
public sealed record ThreeDSceneRenderPlan(
    ThreeDColor Background,
    IReadOnlyList<ThreeDPolygonRenderItem> GroundPlanes,
    IReadOnlyList<ThreeDLineRenderItem> Lines,
    IReadOnlyList<ThreeDPrimitiveRenderItem> Primitives)
{
    private const string BackgroundHex = "#0B1118";

    public static ThreeDSceneRenderPlan Build(
        ThreeDSceneSnapshot scene,
        double width,
        double height,
        string? excludedPrimitiveId = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!double.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height < 0) throw new ArgumentOutOfRangeException(nameof(height));

        var background = ThreeDColor.Parse(BackgroundHex);
        if (width == 0 || height == 0)
            return new(background, [], [], []);

        var ground = scene.Primitives
            .Where(item => item.Kind == ThreeDPrimitiveKind.GroundPlane && item.Id != excludedPrimitiveId)
            .Select(item =>
            {
                var center = GroundCenter(scene.Camera) with { Y = item.Transform.Position.Y };
                var half = StableCoverageHalfExtent(scene.Camera,
                    Math.Max(Math.Abs(item.Transform.Scale.X), Math.Abs(item.Transform.Scale.Z)) / 2);
                var corners = new[]
                {
                    new ThreeDVector3(center.X - half, center.Y, center.Z - half),
                    new ThreeDVector3(center.X + half, center.Y, center.Z - half),
                    new ThreeDVector3(center.X + half, center.Y, center.Z + half),
                    new ThreeDVector3(center.X - half, center.Y, center.Z + half)
                };
                return new ThreeDPolygonRenderItem(
                    ThreeDProjection.ProjectPolygon(corners, scene.Camera, width, height),
                    ThreeDColor.Parse(item.Color));
            })
            .Where(item => item.Points.Count >= 3)
            .ToArray();

        var lines = new List<ThreeDLineRenderItem>();
        if (scene.Grid.Visible)
        {
            var penColor = ThreeDColor.Parse(scene.Grid.Color);
            var footprint = ThreeDProjection.GroundFootprint(scene.Camera, width, height);
            var center = GroundCenter(scene.Camera);
            var halfExtent = StableCoverageHalfExtent(scene.Camera, scene.Grid.SizeMetres / 2);
            var minX = center.X - halfExtent;
            var maxX = center.X + halfExtent;
            var minZ = center.Z - halfExtent;
            var maxZ = center.Z + halfExtent;
            if (footprint.Count >= 2)
            {
                minX = Math.Min(minX, footprint.Min(point => point.X));
                maxX = Math.Max(maxX, footprint.Max(point => point.X));
                minZ = Math.Min(minZ, footprint.Min(point => point.Z));
                maxZ = Math.Max(maxZ, footprint.Max(point => point.Z));
            }

            var requestedExtent = Math.Max(maxX - minX, maxZ - minZ);
            var spacing = AdaptiveGridSpacing(
                Math.Max(0.1, scene.Grid.SpacingMetres),
                Math.Max(scene.Grid.SizeMetres, requestedExtent) / 2);
            var margin = spacing * 2;
            minX = Math.Floor((minX - margin) / spacing) * spacing;
            maxX = Math.Ceiling((maxX + margin) / spacing) * spacing;
            minZ = Math.Floor((minZ - margin) / spacing) * spacing;
            maxZ = Math.Ceiling((maxZ + margin) / spacing) * spacing;
            for (var x = minX; x <= maxX; x += spacing)
                AddProjectedLine(lines, new(x, 0, minZ), new(x, 0, maxZ), penColor, 1, scene.Camera, width, height);
            for (var z = minZ; z <= maxZ; z += spacing)
                AddProjectedLine(lines, new(minX, 0, z), new(maxX, 0, z), penColor, 1, scene.Camera, width, height);
        }

        if (scene.Axes.Visible)
        {
            AddProjectedLine(lines, ThreeDVector3.Zero, new(scene.Axes.LengthMetres, 0, 0), ThreeDColor.Parse("#6495ED"), 2, scene.Camera, width, height);
            AddProjectedLine(lines, ThreeDVector3.Zero, new(0, scene.Axes.LengthMetres, 0), ThreeDColor.Parse("#32CD32"), 2, scene.Camera, width, height);
            AddProjectedLine(lines, ThreeDVector3.Zero, new(0, 0, scene.Axes.LengthMetres), ThreeDColor.Parse("#FFA500"), 2, scene.Camera, width, height);
        }

        foreach (var line in scene.Lines)
        {
            if (line.Points.Count < 2) continue;
            var color = ThreeDColor.Parse(line.Color);
            for (var index = 1; index < line.Points.Count; index++)
                AddProjectedLine(lines, line.Points[index - 1], line.Points[index], color,
                    Math.Clamp(line.Width, 1, 6), scene.Camera, width, height);
            if (line.Closed)
                AddProjectedLine(lines, line.Points[^1], line.Points[0], color,
                    Math.Clamp(line.Width, 1, 6), scene.Camera, width, height);
        }

        var primitives = new List<ThreeDPrimitiveRenderItem>();
        foreach (var primitive in scene.Primitives
                     .Where(item => item.Id != excludedPrimitiveId && item.Kind != ThreeDPrimitiveKind.GroundPlane)
                     .OrderByDescending(item => ThreeDProjection.Project(item.Transform.Position, scene.Camera, width, height).Depth))
        {
            var center = ThreeDProjection.Project(primitive.Transform.Position, scene.Camera, width, height);
            if (!center.Visible) continue;
            var color = ThreeDColor.Parse(primitive.Selected || primitive.TeamSelected ? "#FFD166" : primitive.Color);
            if (primitive.Kind == ThreeDPrimitiveKind.Arrow)
            {
                var endpoint = new ThreeDVector3(primitive.Transform.Position.X,
                    primitive.Transform.Position.Y,
                    primitive.Transform.Position.Z + Math.Max(4, primitive.Transform.Scale.Z));
                var projectedEnd = ThreeDProjection.Project(endpoint, scene.Camera, width, height);
                primitives.Add(new(primitive.Kind, center, color,
                    primitive.Selected || primitive.TeamSelected ? 11 : 9,
                    projectedEnd.Visible ? projectedEnd : null,
                    primitive.Label));
                continue;
            }

            primitives.Add(new(primitive.Kind, center, color,
                primitive.Kind == ThreeDPrimitiveKind.Point ? 6 : (primitive.Selected || primitive.TeamSelected ? 11 : 9),
                null, primitive.Label));
        }

        return new(background, ground, lines, primitives);
    }

    private static void AddProjectedLine(
        List<ThreeDLineRenderItem> target,
        ThreeDVector3 from,
        ThreeDVector3 to,
        ThreeDColor color,
        double width,
        ThreeDCameraSnapshot camera,
        double viewportWidth,
        double viewportHeight)
    {
        if (ThreeDProjection.TryProjectSegment(from, to, camera, viewportWidth, viewportHeight, out var start, out var end))
            target.Add(new(start, end, color, width));
    }

    private static ThreeDVector3 GroundCenter(ThreeDCameraSnapshot camera)
        => camera.OrbitTarget is { } focus
            ? new(focus.X, 0, focus.Z)
            : new(camera.Position.X, 0, camera.Position.Z);

    private static double StableCoverageHalfExtent(ThreeDCameraSnapshot camera, double minimum)
    {
        var distance = Math.Sqrt(camera.Position.X * camera.Position.X +
                                 camera.Position.Z * camera.Position.Z +
                                 camera.Position.Y * camera.Position.Y);
        var viewRange = camera.OrbitMode ? (camera.OrbitDistance ?? distance) * 8 : camera.FarClip * 0.5;
        return Math.Clamp(Math.Max(500, Math.Max(minimum, viewRange)), 500, 100000);
    }

    private static double AdaptiveGridSpacing(double requestedSpacing, double halfExtent)
    {
        var spacing = Math.Max(0.1, requestedSpacing);
        var lineCount = halfExtent * 2 / spacing;
        if (lineCount > 180) spacing *= Math.Ceiling(lineCount / 180);
        return spacing;
    }
}

public sealed record ThreeDPolygonRenderItem(IReadOnlyList<ProjectedPoint> Points, ThreeDColor Color);

public sealed record ThreeDLineRenderItem(ProjectedPoint Start, ProjectedPoint End, ThreeDColor Color, double Width);

public sealed record ThreeDPrimitiveRenderItem(
    ThreeDPrimitiveKind Kind,
    ProjectedPoint Center,
    ThreeDColor Color,
    double Radius,
    ProjectedPoint? ArrowEnd,
    string? Label);

public readonly record struct ThreeDColor(byte Red, byte Green, byte Blue)
{
    public static ThreeDColor Parse(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length == 7 && value[0] == '#' &&
            byte.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var red) &&
            byte.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var green) &&
            byte.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
            return new(red, green, blue);
        return new(180, 180, 180);
    }
}
