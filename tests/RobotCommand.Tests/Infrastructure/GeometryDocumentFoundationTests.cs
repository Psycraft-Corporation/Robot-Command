using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GeometryDocumentFoundationTests
{
    private readonly GeometryDocumentCodec _codec = new();
    private readonly GeometryDocumentValidator _validator = new();

    [Fact]
    public void SupportedKinds_ArePointWaypointSequenceAndZoneOnly()
    {
        Assert.Equal(
            ["PointOfInterest", "WaypointSequence", "Zone"],
            Enum.GetValues<GeometryDocumentKind>()
                .Where(item => item != GeometryDocumentKind.Unknown)
                .Select(item => item.ToString())
                .ToArray());
    }

    [Fact]
    public void PrepareForSave_ComputesStableHashIndependentOfMetadataAndAttributeOrder()
    {
        var first = Route() with
        {
            Attributes = new Dictionary<string, string>
            {
                ["zeta"] = "last",
                ["alpha"] = "first"
            },
            Origin = GeometryDocumentOrigin.Imported,
            SourceConnectionId = "connection-1",
            SourceRevision = "revision-1",
            SourceSha256 = new string('a', 64),
            UpdatedAt = DateTimeOffset.Parse("2026-07-25T12:00:00Z")
        };
        var second = Route() with
        {
            Attributes = new Dictionary<string, string>
            {
                ["alpha"] = "first",
                ["zeta"] = "last"
            },
            Origin = GeometryDocumentOrigin.PulledFromLogos,
            SourceConnectionId = "connection-2",
            SourceRevision = "revision-2",
            SourceSha256 = new string('b', 64),
            UpdatedAt = DateTimeOffset.Parse("2026-07-26T12:00:00Z")
        };

        var savedFirst = _codec.PrepareForSave(first);
        var savedSecond = _codec.PrepareForSave(second);

        Assert.Equal(savedFirst.ContentSha256, savedSecond.ContentSha256);
        Assert.False(savedFirst.IsDirty);
        Assert.Matches("^[a-f0-9]{64}$", savedFirst.ContentSha256);
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsCanonicalGeometryDocument()
    {
        var saved = _codec.PrepareForSave(Route());

        var json = _codec.Serialize(saved);
        var loaded = _codec.Deserialize(json);

        Assert.Equal(GeometryDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("cameron-patrol-route", loaded.GeometryId);
        Assert.Equal(GeometryDocumentKind.WaypointSequence, loaded.Kind);
        Assert.Equal(2, loaded.Points.Count);
        Assert.Equal(saved.ContentSha256, loaded.ContentSha256);
        Assert.False(loaded.IsDirty);
        Assert.Contains("\"kind\": \"WaypointSequence\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_LegacyLogosDocument_UpgradesToRobotCommandSchema()
    {
        var legacy = _codec.Serialize(_codec.PrepareForSave(Route()))
            .Replace(GeometryDocument.CurrentSchemaVersion, GeometryDocument.LegacyLogosSchemaVersion, StringComparison.Ordinal);

        var imported = _codec.Deserialize(legacy);

        Assert.Equal(GeometryDocument.CurrentSchemaVersion, imported.SchemaVersion);
        Assert.Equal(GeometryDocumentOrigin.Imported, imported.Origin);
        Assert.Contains("legacy", imported.SourceSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GeometryAltitudeReference.AboveGroundLevel, imported.AltitudeReference);
        Assert.DoesNotContain(_validator.Validate(imported, requireAuthorableFrame: false).Issues,
            issue => issue.Code is "GEOMETRY_HASH_MISSING" or "GEOMETRY_HASH_INVALID" or "GEOMETRY_HASH_MISMATCH");
    }

    [Fact]
    public void Validate_PointOfInterestRequiresExactlyOnePoint()
    {
        var invalid = GeometryDocument.Create(
            "poi-alpha",
            "POI Alpha",
            GeometryDocumentKind.PointOfInterest);

        var result = _validator.Validate(invalid, requireAuthorableFrame: true);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, item => item.Code == "GEOMETRY_POI_POINT_COUNT");
    }

    [Fact]
    public void Validate_WaypointSequenceRequiresAtLeastTwoPoints()
    {
        var invalid = GeometryDocument.Create(
            "route-alpha",
            "Route Alpha",
            GeometryDocumentKind.WaypointSequence) with
        {
            Points = [GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65, 50)]
        };

        var result = _validator.Validate(invalid, requireAuthorableFrame: true);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, item => item.Code == "GEOMETRY_ROUTE_POINT_COUNT");
    }

    [Fact]
    public void Validate_AcceptsClosedZoneOuterRingAfterHashing()
    {
        var zone = GeometryDocument.Create(
            "zone-alpha",
            "Zone Alpha",
            GeometryDocumentKind.Zone) with
        {
            Rings =
            [
                new GeometryDocumentRing
                {
                    Points =
                    [
                        GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64),
                        GeometryDocumentPoint.GlobalWgs84(-79.37, 43.64),
                        GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66),
                        GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64)
                    ]
                }
            ]
        };
        zone = _codec.PrepareForSave(zone);

        var result = _validator.Validate(zone, requireAuthorableFrame: true);

        Assert.True(result.IsValid);
        Assert.Equal(GeometryValidationState.Valid, result.State);
    }


    [Fact]
    public void Validate_RejectsConsecutiveDuplicateWaypoints()
    {
        var point = GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65, 20);
        var route = GeometryDocument.Create(
            "route-duplicate",
            "Duplicate route",
            GeometryDocumentKind.WaypointSequence) with
        {
            Points = [point, point]
        };

        var result = _validator.Validate(route, requireAuthorableFrame: true);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "GEOMETRY_ROUTE_CONSECUTIVE_DUPLICATE");
    }

    [Fact]
    public void Validate_RejectsSelfIntersectingZone()
    {
        var zone = GeometryDocument.Create(
            "zone-crossed",
            "Crossed zone",
            GeometryDocumentKind.Zone) with
        {
            Rings =
            [
                new GeometryDocumentRing
                {
                    Points =
                    [
                        GeometryDocumentPoint.GlobalWgs84(-79.40, 43.64),
                        GeometryDocumentPoint.GlobalWgs84(-79.36, 43.68),
                        GeometryDocumentPoint.GlobalWgs84(-79.40, 43.68),
                        GeometryDocumentPoint.GlobalWgs84(-79.36, 43.64),
                        GeometryDocumentPoint.GlobalWgs84(-79.40, 43.64)
                    ]
                }
            ]
        };

        var result = _validator.Validate(zone, requireAuthorableFrame: true);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "GEOMETRY_ZONE_SELF_INTERSECTION");
    }

    [Fact]
    public void Validate_RejectsInvertedPolicyAltitudeBand()
    {
        var route = Route() with
        {
            Policy = new GeometryPolicyAnnotation
            {
                Kind = "safety",
                Constraint = "avoid",
                MinimumAltitudeMetres = 120,
                MaximumAltitudeMetres = 80
            }
        };

        var result = _validator.Validate(route, requireAuthorableFrame: true);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "GEOMETRY_POLICY_ALTITUDE_RANGE_INVALID");
    }

    [Fact]
    public void Validate_DetectsContentHashMismatch()
    {
        var saved = _codec.PrepareForSave(Route());
        var changed = saved with { DisplayName = "Changed route" };

        var result = _validator.Validate(changed);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, item => item.Code == "GEOMETRY_HASH_MISMATCH");
    }

    [Fact]
    public void BeginEdit_RejectsNonGlobalFrame()
    {
        var document = Route() with { Frame = GeometryCoordinateFrame.LocalNed };

        var exception = Assert.Throws<NotSupportedException>(() =>
            GeometryEditorState.BeginEdit(document));

        Assert.Contains("GlobalWgs84", exception.Message, StringComparison.Ordinal);
    }

    private static GeometryDocument Route()
        => GeometryDocument.Create(
            "cameron-patrol-route",
            "Cameron Patrol Route",
            GeometryDocumentKind.WaypointSequence,
            DateTimeOffset.Parse("2026-07-25T12:00:00Z")) with
        {
            Description = "A short WGS84 patrol route.",
            Points =
            [
                GeometryDocumentPoint.GlobalWgs84(-79.3832, 43.6532, 50),
                GeometryDocumentPoint.GlobalWgs84(-79.3820, 43.6540, 50)
            ],
            Policy = new GeometryPolicyAnnotation
            {
                Kind = "route",
                Operations = ["flight_path"],
                Tags = ["patrol"]
            }
        };
}
