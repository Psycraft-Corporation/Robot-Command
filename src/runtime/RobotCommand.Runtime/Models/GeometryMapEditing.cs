
namespace RobotCommand.Models;

public enum MapInteractionMode
{
    Navigate,
    Select,
    CreatePoint,
    CreatePath,
    CreateZone,
    EditGeometry
}

public enum GeometryEditSessionStage
{
    Idle,
    Editing,
    Completed
}

public enum GeometryMapEditAction
{
    AddVertex,
    SelectVertex,
    MoveVertex,
    InsertVertex,
    RemoveVertex
}

public sealed record GeometryMapEditRequest(
    GeometryMapEditAction Action,
    GeometryDocumentPoint? Point = null,
    int? VertexIndex = null);

public sealed record GeometryEditHandle(
    int VertexIndex,
    GeometryDocumentPoint Point,
    bool Selected);

public sealed record GeometryEditSnapshot(
    GeometryEditSessionStage Stage,
    MapInteractionMode InteractionMode,
    GeometryEditorState Editor,
    int? SelectedVertexIndex,
    bool CanUndo,
    bool CanRedo,
    bool CanComplete,
    string Status)
{
    public static GeometryEditSnapshot Empty { get; } = new(
        GeometryEditSessionStage.Idle,
        MapInteractionMode.Navigate,
        GeometryEditorState.Empty,
        null,
        false,
        false,
        false,
        "No geometry edit is active.");

    public GeometryDocument? Draft => Editor.Draft;

    public bool HasDraft => Draft is not null;

    public bool IsEditing => Stage == GeometryEditSessionStage.Editing;

    public bool IsCompleted => Stage == GeometryEditSessionStage.Completed;

    public IReadOnlyList<GeometryDocumentPoint> Vertices => GeometryEditShape.GetVertices(Draft);

    public IReadOnlyList<GeometryEditHandle> Handles => Vertices
        .Select((point, index) => new GeometryEditHandle(index, point, SelectedVertexIndex == index))
        .ToArray();
}

public static class GeometryEditShape
{
    public static IReadOnlyList<GeometryDocumentPoint> GetVertices(GeometryDocument? document)
    {
        if (document is null)
        {
            return [];
        }

        if (document.Kind != GeometryDocumentKind.Zone)
        {
            return document.Points?.ToArray() ?? [];
        }

        var points = document.Rings?.FirstOrDefault()?.Points?.ToArray() ?? [];
        if (points.Length > 1 && SamePosition(points[0], points[^1]))
        {
            return points[..^1];
        }

        return points;
    }

    public static GeometryDocument WithVertices(
        GeometryDocument document,
        IReadOnlyList<GeometryDocumentPoint> vertices,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(vertices);
        var copied = vertices.ToArray();
        var timestamp = updatedAt ?? DateTimeOffset.UtcNow;
        return document.Kind switch
        {
            GeometryDocumentKind.PointOfInterest or GeometryDocumentKind.WaypointSequence => document with
            {
                Points = copied,
                Rings = [],
                UpdatedAt = timestamp,
                ContentSha256 = string.Empty,
                IsDirty = true
            },
            GeometryDocumentKind.Zone => document with
            {
                Points = [],
                Rings = copied.Length == 0
                    ? []
                    : [new GeometryDocumentRing { Points = copied }],
                UpdatedAt = timestamp,
                ContentSha256 = string.Empty,
                IsDirty = true
            },
            _ => throw new InvalidOperationException(
                $"Geometry kind '{document.Kind}' is not authorable.")
        };
    }

    public static GeometryDocument NormalizeCompleted(GeometryDocument document, DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var vertices = GeometryTopology.RemoveConsecutiveDuplicates(GetVertices(document)).ToArray();
        if (document.Kind != GeometryDocumentKind.Zone)
        {
            return document with
            {
                Points = vertices,
                Rings = [],
                UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
                ContentSha256 = string.Empty,
                IsDirty = true
            };
        }

        if (vertices.Length >= 3 && !GeometryTopology.SameHorizontalPosition(vertices[0], vertices[^1]))
        {
            vertices = [.. vertices, vertices[0]];
        }

        return document with
        {
            Points = [],
            Rings = vertices.Length == 0
                ? []
                : [new GeometryDocumentRing { Points = vertices }],
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
            ContentSha256 = string.Empty,
            IsDirty = true
        };
    }

    public static bool CanComplete(GeometryDocument? document)
    {
        if (document is null)
        {
            return false;
        }

        var vertices = GetVertices(document);
        return document.Kind switch
        {
            GeometryDocumentKind.PointOfInterest => vertices.Count == 1,
            GeometryDocumentKind.WaypointSequence =>
                vertices.Count >= 2 &&
                !GeometryTopology.HasConsecutiveDuplicateVertices(vertices),
            GeometryDocumentKind.Zone =>
                vertices.Count >= 3 &&
                !GeometryTopology.HasConsecutiveDuplicateVertices(vertices) &&
                !GeometryTopology.HasRepeatedVertex(vertices) &&
                GeometryTopology.HasNonZeroArea(vertices) &&
                !GeometryTopology.HasSelfIntersection(vertices),
            _ => false
        };
    }

    private static bool SamePosition(GeometryDocumentPoint left, GeometryDocumentPoint right)
        => left.X.Equals(right.X) && left.Y.Equals(right.Y) && left.Z.Equals(right.Z);
}
