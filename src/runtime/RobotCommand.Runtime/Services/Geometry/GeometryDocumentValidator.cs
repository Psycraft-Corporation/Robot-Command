using System.Text.RegularExpressions;
using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public sealed class GeometryDocumentValidator
{
    private static readonly Regex GeometryIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Sha256Pattern = new(
        "^[a-fA-F0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly GeometryDocumentCodec _codec;

    public GeometryDocumentValidator(GeometryDocumentCodec? codec = null)
    {
        _codec = codec ?? new GeometryDocumentCodec();
    }

    public GeometryValidationResult Validate(
        GeometryDocument document,
        bool requireAuthorableFrame = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = GeometryDocumentCodec.Normalize(document);
        var issues = new List<GeometryValidationIssue>();

        if (!string.Equals(
                normalized.SchemaVersion,
                GeometryDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            Error(
                issues,
                "GEOMETRY_SCHEMA_UNSUPPORTED",
                $"schemaVersion must be '{GeometryDocument.CurrentSchemaVersion}'.",
                "schemaVersion");
        }

        if (normalized.AltitudeReference is GeometryAltitudeReference.Unknown ||
            !Enum.IsDefined(normalized.AltitudeReference))
        {
            Error(issues, "GEOMETRY_ALTITUDE_REFERENCE_INVALID",
                "altitudeReference must identify a supported vertical reference.", "altitudeReference");
        }

        if (string.IsNullOrWhiteSpace(normalized.GeometryId))
        {
            Error(issues, "GEOMETRY_ID_REQUIRED", "geometryId is required.", "geometryId");
        }
        else if (!GeometryIdPattern.IsMatch(normalized.GeometryId))
        {
            Error(
                issues,
                "GEOMETRY_ID_INVALID",
                "geometryId must begin with an ASCII letter or digit and contain only letters, digits, '.', '_' or '-'.",
                "geometryId");
        }

        if (string.IsNullOrWhiteSpace(normalized.DisplayName))
        {
            Error(issues, "GEOMETRY_NAME_REQUIRED", "displayName is required.", "displayName");
        }

        if (!Enum.IsDefined(normalized.Kind) || normalized.Kind == GeometryDocumentKind.Unknown)
        {
            Error(issues, "GEOMETRY_KIND_INVALID", "kind must identify a supported geometry kind.", "kind");
        }

        if (!Enum.IsDefined(normalized.Frame) || normalized.Frame == GeometryCoordinateFrame.Unknown)
        {
            Error(issues, "GEOMETRY_FRAME_INVALID", "frame must identify a supported coordinate frame.", "frame");
        }
        else if (requireAuthorableFrame && normalized.Frame != GeometryCoordinateFrame.GlobalWgs84)
        {
            Error(
                issues,
                "GEOMETRY_FRAME_NOT_AUTHORABLE",
                "Robot Command authoring currently supports only GlobalWgs84 geometry.",
                "frame");
        }

        ValidatePoints(normalized.Points, normalized.Frame, "points", issues);
        for (var ringIndex = 0; ringIndex < normalized.Rings.Count; ringIndex++)
        {
            ValidatePoints(
                normalized.Rings[ringIndex].Points,
                normalized.Frame,
                $"rings[{ringIndex}].points",
                issues);
        }

        switch (normalized.Kind)
        {
            case GeometryDocumentKind.PointOfInterest:
                ValidatePointOfInterest(normalized, issues);
                break;
            case GeometryDocumentKind.WaypointSequence:
                ValidateWaypointSequence(normalized, issues);
                break;
            case GeometryDocumentKind.Zone:
                ValidateZone(normalized, issues);
                break;
        }

        ValidatePolicy(normalized.Policy, issues);
        ValidateAttributes(normalized.Attributes, "attributes", issues);
        ValidateAttributes(normalized.Policy.Attributes, "policy.attributes", issues);
        ValidateHash(normalized, issues);

        var state = issues.Any(item => item.Severity == GeometryValidationSeverity.Error)
            ? GeometryValidationState.Invalid
            : issues.Any(item => item.Severity == GeometryValidationSeverity.Warning)
                ? GeometryValidationState.Warning
                : GeometryValidationState.Valid;
        var summary = state switch
        {
            GeometryValidationState.Valid => "Geometry document is structurally valid.",
            GeometryValidationState.Warning => "Geometry document is valid with warnings.",
            _ => $"Geometry document has {issues.Count(item => item.Severity == GeometryValidationSeverity.Error)} error(s)."
        };
        return new GeometryValidationResult(state, summary, issues);
    }

    private static void ValidatePointOfInterest(
        GeometryDocument document,
        ICollection<GeometryValidationIssue> issues)
    {
        if (document.Points.Count != 1)
        {
            Error(
                issues,
                "GEOMETRY_POI_POINT_COUNT",
                "A PointOfInterest must contain exactly one point.",
                "points");
        }
        if (document.Rings.Count != 0)
        {
            Error(
                issues,
                "GEOMETRY_POI_RINGS_NOT_ALLOWED",
                "A PointOfInterest cannot contain rings.",
                "rings");
        }
    }

    private static void ValidateWaypointSequence(
        GeometryDocument document,
        ICollection<GeometryValidationIssue> issues)
    {
        if (document.Points.Count < 2)
        {
            Error(
                issues,
                "GEOMETRY_ROUTE_POINT_COUNT",
                "A WaypointSequence must contain at least two points.",
                "points");
        }
        if (document.Rings.Count != 0)
        {
            Error(
                issues,
                "GEOMETRY_ROUTE_RINGS_NOT_ALLOWED",
                "A WaypointSequence cannot contain rings.",
                "rings");
        }
        if (GeometryTopology.HasConsecutiveDuplicateVertices(document.Points))
        {
            Error(
                issues,
                "GEOMETRY_ROUTE_CONSECUTIVE_DUPLICATE",
                "A WaypointSequence cannot contain consecutive waypoints at the same horizontal position.",
                "points");
        }
    }

    private static void ValidateZone(
        GeometryDocument document,
        ICollection<GeometryValidationIssue> issues)
    {
        if (document.Points.Count != 0)
        {
            Error(
                issues,
                "GEOMETRY_ZONE_POINTS_NOT_CANONICAL",
                "A local Zone document stores its canonical polygon in rings, not points.",
                "points");
        }
        if (document.Rings.Count == 0)
        {
            Error(
                issues,
                "GEOMETRY_ZONE_OUTER_RING_REQUIRED",
                "A Zone must contain an outer ring.",
                "rings");
            return;
        }

        var outer = document.Rings[0].Points;
        if (outer.Count < 4)
        {
            Error(
                issues,
                "GEOMETRY_ZONE_RING_TOO_SHORT",
                "A Zone outer ring must contain at least three vertices plus its closing point.",
                "rings[0].points");
            return;
        }

        if (outer[0] != outer[^1])
        {
            Error(
                issues,
                "GEOMETRY_ZONE_RING_NOT_CLOSED",
                "A Zone outer ring must repeat its first point as its final point.",
                "rings[0].points");
        }

        var open = GeometryTopology.OpenVertices(outer);
        var distinctVertices = open
            .DistinctBy(point => (point.X, point.Y))
            .Count();
        if (distinctVertices < 3)
        {
            Error(
                issues,
                "GEOMETRY_ZONE_VERTEX_COUNT",
                "A Zone outer ring must contain at least three distinct vertices.",
                "rings[0].points");
        }
        if (GeometryTopology.HasConsecutiveDuplicateVertices(open))
        {
            Error(
                issues,
                "GEOMETRY_ZONE_CONSECUTIVE_DUPLICATE",
                "A Zone outer ring cannot contain consecutive vertices at the same horizontal position.",
                "rings[0].points");
        }
        if (GeometryTopology.HasRepeatedVertex(open))
        {
            Error(
                issues,
                "GEOMETRY_ZONE_REPEATED_VERTEX",
                "A Zone outer ring cannot reuse a vertex except for its canonical closing point.",
                "rings[0].points");
        }
        if (!GeometryTopology.HasNonZeroArea(open))
        {
            Error(
                issues,
                "GEOMETRY_ZONE_ZERO_AREA",
                "A Zone outer ring must enclose a non-zero area.",
                "rings[0].points");
        }
        if (GeometryTopology.HasSelfIntersection(open))
        {
            Error(
                issues,
                "GEOMETRY_ZONE_SELF_INTERSECTION",
                "A Zone outer ring cannot cross itself.",
                "rings[0].points");
        }
        if (document.Rings.Count > 1)
        {
            Warning(
                issues,
                "GEOMETRY_ZONE_HOLES_READ_ONLY",
                "Robot Command preserves zone holes but the current visual editor supports one outer ring only.",
                "rings");
        }
    }

    private static void ValidatePoints(
        IReadOnlyList<GeometryDocumentPoint> points,
        GeometryCoordinateFrame frame,
        string fieldPath,
        ICollection<GeometryValidationIssue> issues)
    {
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var path = $"{fieldPath}[{index}]";
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            {
                Error(
                    issues,
                    "GEOMETRY_POINT_NONFINITE",
                    "Geometry coordinates must be finite numbers.",
                    path);
                continue;
            }

            if (frame == GeometryCoordinateFrame.GlobalWgs84)
            {
                if (point.X is < -180 or > 180)
                {
                    Error(
                        issues,
                        "GEOMETRY_LONGITUDE_INVALID",
                        "WGS84 longitude must be between -180 and 180 degrees.",
                        $"{path}.x");
                }
                if (point.Y is < -90 or > 90)
                {
                    Error(
                        issues,
                        "GEOMETRY_LATITUDE_INVALID",
                        "WGS84 latitude must be between -90 and 90 degrees.",
                        $"{path}.y");
                }
            }
        }
    }

    private static void ValidatePolicy(
        GeometryPolicyAnnotation policy,
        ICollection<GeometryValidationIssue> issues)
    {
        if (policy.MinimumAltitudeMetres is { } minimum && !double.IsFinite(minimum))
        {
            Error(
                issues,
                "GEOMETRY_POLICY_MIN_ALTITUDE_NONFINITE",
                "policy.minimumAltitudeMetres must be finite.",
                "policy.minimumAltitudeMetres");
        }
        if (policy.MaximumAltitudeMetres is { } maximum && !double.IsFinite(maximum))
        {
            Error(
                issues,
                "GEOMETRY_POLICY_MAX_ALTITUDE_NONFINITE",
                "policy.maximumAltitudeMetres must be finite.",
                "policy.maximumAltitudeMetres");
        }
        if (policy.MinimumAltitudeMetres is { } min &&
            policy.MaximumAltitudeMetres is { } max &&
            min > max)
        {
            Error(
                issues,
                "GEOMETRY_POLICY_ALTITUDE_RANGE_INVALID",
                "policy.minimumAltitudeMetres cannot exceed policy.maximumAltitudeMetres.",
                "policy");
        }
    }

    private static void ValidateAttributes(
        IReadOnlyDictionary<string, string> attributes,
        string fieldPath,
        ICollection<GeometryValidationIssue> issues)
    {
        foreach (var pair in attributes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                Error(
                    issues,
                    "GEOMETRY_ATTRIBUTE_KEY_REQUIRED",
                    "Attribute keys cannot be empty.",
                    fieldPath);
            }
        }
    }

    private void ValidateHash(
        GeometryDocument document,
        ICollection<GeometryValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(document.ContentSha256))
        {
            Warning(
                issues,
                "GEOMETRY_HASH_MISSING",
                "contentSha256 has not been computed for this draft.",
                "contentSha256");
            return;
        }

        if (!Sha256Pattern.IsMatch(document.ContentSha256))
        {
            Error(
                issues,
                "GEOMETRY_HASH_INVALID",
                "contentSha256 must contain 64 hexadecimal characters.",
                "contentSha256");
            return;
        }

        var expected = GeometryDocumentCodec.ComputeContentSha256(document);
        if (!string.Equals(expected, document.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            Error(
                issues,
                "GEOMETRY_HASH_MISMATCH",
                "contentSha256 does not match the geometry content.",
                "contentSha256");
        }
    }

    private static void Error(
        ICollection<GeometryValidationIssue> issues,
        string code,
        string message,
        string fieldPath)
        => issues.Add(new GeometryValidationIssue(
            code,
            GeometryValidationSeverity.Error,
            message,
            fieldPath));

    private static void Warning(
        ICollection<GeometryValidationIssue> issues,
        string code,
        string message,
        string fieldPath)
        => issues.Add(new GeometryValidationIssue(
            code,
            GeometryValidationSeverity.Warning,
            message,
            fieldPath));
}
