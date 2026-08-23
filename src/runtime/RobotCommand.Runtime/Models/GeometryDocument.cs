using System.Text.Json.Serialization;

namespace RobotCommand.Models;

public enum GeometryDocumentKind
{
    Unknown,
    PointOfInterest,
    WaypointSequence,
    Zone
}

public enum GeometryCoordinateFrame
{
    Unknown,
    GlobalWgs84,
    LocalEnu,
    LocalNed
}

/// <summary>Vertical datum for geometry vertex altitude.</summary>
public enum GeometryAltitudeReference
{
    Unknown,
    AboveGroundLevel,
    MeanSeaLevel
}

public enum GeometryDocumentOrigin
{
    LocalDraft,
    Imported,
    PulledFromLogos,
    Generated
}

public readonly record struct GeometryDocumentPoint(
    double X,
    double Y,
    double Z = 0)
{
    public static GeometryDocumentPoint GlobalWgs84(
        double longitudeDegrees,
        double latitudeDegrees,
        double altitudeMetres = 0)
        => new(longitudeDegrees, latitudeDegrees, altitudeMetres);

    [JsonIgnore]
    public double LongitudeDegrees => X;

    [JsonIgnore]
    public double LatitudeDegrees => Y;

    [JsonIgnore]
    public double AltitudeMetres => Z;
}

public sealed record GeometryDocumentRing
{
    public IReadOnlyList<GeometryDocumentPoint> Points { get; init; } = [];
}

public sealed record GeometryPolicyAnnotation
{
    public static GeometryPolicyAnnotation None { get; } = new();

    public string Kind { get; init; } = "none";

    public string Constraint { get; init; } = "none";

    public IReadOnlyList<string> Operations { get; init; } = [];

    public string Decision { get; init; } = "none";

    public string Code { get; init; } = string.Empty;

    public string RecommendedAction { get; init; } = "none";

    public double? MinimumAltitudeMetres { get; init; }

    public double? MaximumAltitudeMetres { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonIgnore]
    public bool IsConfigured =>
        !string.Equals(Kind, "none", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Constraint, "none", StringComparison.OrdinalIgnoreCase) ||
        Operations.Count > 0 ||
        !string.Equals(Decision, "none", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(Code) ||
        !string.Equals(RecommendedAction, "none", StringComparison.OrdinalIgnoreCase) ||
        MinimumAltitudeMetres is not null ||
        MaximumAltitudeMetres is not null ||
        Tags.Count > 0 ||
        Attributes.Count > 0;
}

public sealed record GeometryDocument
{
    public const string CurrentSchemaVersion = "robotcommand.geometry.v1";
    public const string LegacyLogosSchemaVersion = "logos.geometry-document.v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string GeometryId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public GeometryDocumentKind Kind { get; init; } = GeometryDocumentKind.Unknown;

    public GeometryCoordinateFrame Frame { get; init; } = GeometryCoordinateFrame.GlobalWgs84;

    public GeometryAltitudeReference AltitudeReference { get; init; } =
        GeometryAltitudeReference.AboveGroundLevel;

    public IReadOnlyList<GeometryDocumentPoint> Points { get; init; } = [];

    public IReadOnlyList<GeometryDocumentRing> Rings { get; init; } = [];

    public GeometryPolicyAnnotation Policy { get; init; } = GeometryPolicyAnnotation.None;

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public GeometryDocumentOrigin Origin { get; init; } = GeometryDocumentOrigin.LocalDraft;

    public string? SourceConnectionId { get; init; }

    public string? SourceRevision { get; init; }

    public string? SourceSha256 { get; init; }

    public string? SourceSummary { get; init; }

    public string ContentSha256 { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsDirty { get; init; } = true;

    [JsonIgnore]
    public bool IsClosed => Kind == GeometryDocumentKind.Zone;

    public static GeometryDocument Create(
        string geometryId,
        string displayName,
        GeometryDocumentKind kind,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new GeometryDocument
        {
            GeometryId = geometryId,
            DisplayName = displayName,
            Kind = kind,
            Frame = GeometryCoordinateFrame.GlobalWgs84,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            IsDirty = true
        };
    }
}
