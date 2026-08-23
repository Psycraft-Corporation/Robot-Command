namespace RobotCommand.Models;

public enum GeometryEditorMode
{
    None,
    CreatePointOfInterest,
    CreateWaypointSequence,
    CreateZone,
    EditExisting
}

public sealed record GeometryEditorState(
    GeometryEditorMode Mode,
    GeometryDocument? Baseline,
    GeometryDocument? Draft,
    GeometryValidationResult? Validation)
{
    public static GeometryEditorState Empty { get; } = new(
        GeometryEditorMode.None,
        null,
        null,
        null);

    public bool IsActive => Mode != GeometryEditorMode.None && Draft is not null;

    public bool IsDirty => Draft?.IsDirty == true;

    public static GeometryEditorState BeginNew(
        string geometryId,
        string displayName,
        GeometryDocumentKind kind,
        DateTimeOffset? now = null)
    {
        var mode = kind switch
        {
            GeometryDocumentKind.PointOfInterest => GeometryEditorMode.CreatePointOfInterest,
            GeometryDocumentKind.WaypointSequence => GeometryEditorMode.CreateWaypointSequence,
            GeometryDocumentKind.Zone => GeometryEditorMode.CreateZone,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var draft = GeometryDocument.Create(geometryId, displayName, kind, now);
        return new GeometryEditorState(mode, null, draft, null);
    }

    public static GeometryEditorState BeginEdit(GeometryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureAuthorable(document);
        return new GeometryEditorState(
            GeometryEditorMode.EditExisting,
            document with { IsDirty = false },
            document with { IsDirty = false },
            null);
    }

    public static void EnsureAuthorable(GeometryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureAuthorableFrame(document.Frame);
        if (document.Kind == GeometryDocumentKind.Zone && document.Rings.Count > 1)
        {
            throw new NotSupportedException(
                "The current zone editor supports one outer ring only. Zones with holes remain read-only.");
        }
    }

    public static void EnsureAuthorableFrame(GeometryCoordinateFrame frame)
    {
        if (frame != GeometryCoordinateFrame.GlobalWgs84)
        {
            throw new NotSupportedException(
                $"Geometry authoring currently supports only {GeometryCoordinateFrame.GlobalWgs84}; '{frame}' remains read-only.");
        }
    }
}
