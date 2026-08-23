using Google.Protobuf.WellKnownTypes;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using Xunit;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Tests;

public sealed class GeometryProtoMapperTests
{
    private readonly GeometryDocumentCodec _codec = new();
    private readonly GeometryProtoMapper _mapper;

    public GeometryProtoMapperTests()
    {
        _mapper = new GeometryProtoMapper(_codec);
    }

    [Fact]
    public void ToProto_MapsGlobalPointCoordinatesAndMetadata()
    {
        var document = GeometryDocument.Create(
            "poi-alpha",
            "POI Alpha",
            GeometryDocumentKind.PointOfInterest,
            DateTimeOffset.Parse("2026-07-27T12:00:00Z")) with
        {
            Description = "A test point.",
            Points = [GeometryDocumentPoint.GlobalWgs84(-79.3832, 43.6532, 55)],
            SourceRevision = "revision-7"
        };

        var result = _mapper.ToProto(document);

        Assert.Equal(V1.GeometryKind.Poi, result.Kind);
        Assert.Equal(V1.GeometryFrameScope.GlobalWgs84, result.FrameScope);
        Assert.Equal("wgs84", result.FrameId);
        Assert.Equal("revision-7", result.Metadata.Revision);
        var point = Assert.Single(result.Points);
        Assert.Equal(-79.3832, point.X, 6);
        Assert.Equal(43.6532, point.Y, 6);
        Assert.Equal(55, point.Z, 6);
        Assert.Empty(result.Rings);
    }

    [Fact]
    public void ToProto_ZoneUsesCanonicalRingsAndCompatibilityPoints()
    {
        var ring = new GeometryDocumentRing
        {
            Points =
            [
                GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64),
                GeometryDocumentPoint.GlobalWgs84(-79.37, 43.64),
                GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66),
                GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64)
            ]
        };
        var document = GeometryDocument.Create(
            "zone-alpha",
            "Zone Alpha",
            GeometryDocumentKind.Zone) with
        {
            Rings = [ring]
        };

        var result = _mapper.ToProto(document);

        Assert.Equal(V1.GeometryKind.Zone, result.Kind);
        Assert.True(result.Closed);
        Assert.Equal(4, Assert.Single(result.Rings).Points.Count);
        Assert.Equal(4, result.Points.Count);
    }

    [Fact]
    public void RoundTrip_PreservesExplicitZeroMetrePolicyAltitude()
    {
        var document = GeometryDocument.Create(
            "route-alpha",
            "Route Alpha",
            GeometryDocumentKind.WaypointSequence) with
        {
            Points =
            [
                GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65, 0),
                GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66, 0)
            ],
            Policy = new GeometryPolicyAnnotation
            {
                Kind = "route",
                Constraint = "inclusion",
                MinimumAltitudeMetres = 0,
                MaximumAltitudeMetres = 100
            }
        };

        var proto = _mapper.ToProto(document);
        var mapped = _mapper.ToDocument(proto, "connection-1");

        Assert.Equal(0d, mapped.Policy.MinimumAltitudeMetres!.Value);
        Assert.Equal(100d, mapped.Policy.MaximumAltitudeMetres!.Value);
        Assert.DoesNotContain(
            mapped.Policy.Attributes.Keys,
            key => key.StartsWith("robot_command.", StringComparison.Ordinal));
        Assert.False(mapped.IsDirty);
        Assert.Matches("^[a-f0-9]{64}$", mapped.ContentSha256);
    }

    [Fact]
    public void ToDocument_UsesZonePointsWhenOlderObjectOmitsRings()
    {
        var proto = new V1.GeometryObject
        {
            GeometryId = "zone-legacy",
            DisplayName = "Legacy Zone",
            Kind = V1.GeometryKind.Zone,
            FrameId = "wgs84",
            FrameScope = V1.GeometryFrameScope.GlobalWgs84,
            Closed = true
        };
        proto.Points.Add(new[]
        {
            new V1.GeometryPoint { X = -79.39, Y = 43.64 },
            new V1.GeometryPoint { X = -79.37, Y = 43.64 },
            new V1.GeometryPoint { X = -79.37, Y = 43.66 },
            new V1.GeometryPoint { X = -79.39, Y = 43.64 }
        });

        var result = _mapper.ToDocument(proto, "connection-1");

        Assert.Empty(result.Points);
        Assert.Equal(4, Assert.Single(result.Rings).Points.Count);
    }

    [Fact]
    public void ToValidationResult_MapsAuthorizationDenialAndStructuredIssues()
    {
        var authorization = new V1.AuthorizationDecision
        {
            Allowed = false,
            DeniedReasons = "geometry.write is required",
            Status = new V1.DomainStatus
            {
                Ok = false,
                Code = V1.DomainCode.PermissionDenied
            }
        };
        authorization.Status.Issues.Add(new V1.Issue
        {
            Code = "GEOMETRY_WRITE_DENIED",
            Severity = V1.Severity.Error,
            Message = "The caller cannot write geometry.",
            FieldPath = "object.geometry_id"
        });

        var result = GeometryProtoMapper.ToValidationResult(null, null, authorization);

        Assert.Equal(GeometryValidationState.Invalid, result.State);
        Assert.Contains("geometry.write", result.Summary, StringComparison.Ordinal);
        Assert.Contains(result.Issues, item =>
            item.Code == "GEOMETRY_WRITE_DENIED" &&
            item.FieldPath == "object.geometry_id");
    }

    [Fact]
    public void ToUpdateResult_MapsRevisionIssueToConflict()
    {
        var response = new V1.UpdateGeometryObjectResponse
        {
            Authorization = new V1.AuthorizationDecision { Allowed = true },
            Status = new V1.DomainStatus
            {
                Ok = false,
                Code = V1.DomainCode.FailedPrecondition,
                Message = "The expected revision is stale."
            }
        };
        response.Status.Issues.Add(new V1.Issue
        {
            Code = "GEOMETRY_REVISION_CONFLICT",
            Severity = V1.Severity.Error,
            Message = "Refresh before updating."
        });

        var result = _mapper.ToUpdateResult(response, "connection-1");

        Assert.False(result.Accepted);
        Assert.Equal(GeometryCommandState.Conflict, result.State);
        Assert.NotNull(result.Validation);
    }

    [Fact]
    public void ToRegistryEvent_MapsRecordStatusAndObservationTime()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-27T12:30:00Z");
        var response = new V1.WatchGeometryRegistryResponse
        {
            EventType = V1.WatchGeometryRegistryEventType.Updated,
            Event = new V1.EventEnvelope
            {
                EventId = "event-1",
                ObservedAt = Timestamp.FromDateTime(observedAt.UtcDateTime)
            },
            RegistryStatus = new V1.GeometryRegistryStatus
            {
                State = V1.GeometryRegistryState.Ready,
                Health = V1.HealthLevel.Healthy,
                Readiness = V1.ReadinessLevel.Ready,
                ObjectCount = 4,
                CheckedAt = Timestamp.FromDateTime(observedAt.UtcDateTime)
            },
            Record = new V1.GeometryRecord
            {
                GeometryId = "route-alpha",
                DisplayName = "Route Alpha",
                Kind = V1.GeometryKind.WaypointSequence,
                FrameId = "wgs84",
                FrameScope = V1.GeometryFrameScope.GlobalWgs84,
                PointCount = 3,
                Sha256 = "remote-sha"
            }
        };

        var result = _mapper.ToRegistryEvent(response, "connection-1");

        Assert.Equal(GeometryRegistryEventKind.Updated, result.Kind);
        Assert.Equal(observedAt, result.ObservedAt);
        Assert.Equal(GeometryRegistryState.Ready, result.Registry!.State);
        Assert.Equal("route-alpha", result.Record!.GeometryId);
    }
}
