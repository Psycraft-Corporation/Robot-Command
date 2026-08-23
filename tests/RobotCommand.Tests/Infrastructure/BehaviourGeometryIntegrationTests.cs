using RobotCommand.Models;
using RobotCommand.Services.Missions;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourGeometryIntegrationTests
{
    [Theory]
    [InlineData("point", GeometryDocumentKind.PointOfInterest, true)]
    [InlineData("route", GeometryDocumentKind.WaypointSequence, true)]
    [InlineData("polygon", GeometryDocumentKind.Zone, true)]
    [InlineData("zone", GeometryDocumentKind.WaypointSequence, false)]
    public void KindCompatibility_MapsManifestHints(
        string hint,
        GeometryDocumentKind kind,
        bool expected)
        => Assert.Equal(expected, BehaviourGeometryCompatibility.KindMatches(hint, kind));

    [Fact]
    public void PolicyCompatibility_AcceptsKindOrConstraint()
    {
        var policy = new GeometryPolicyAnnotation
        {
            Kind = "safety",
            Constraint = "search_area"
        };

        Assert.True(BehaviourGeometryCompatibility.PolicyMatches("search-area", policy));
        Assert.True(BehaviourGeometryCompatibility.PolicyMatches("safety", policy));
        Assert.False(BehaviourGeometryCompatibility.PolicyMatches("no_land_zone", policy));
    }

    [Fact]
    public void TaskProtoRoundTrip_PreservesGeometryReferences()
    {
        var task = new OperationalTaskRecord(
            "task-1",
            "Search sector",
            "Draft",
            MissionId: "mission-1",
            Objective: "Search sector alpha",
            TaskType: "search",
            BehaviourId: "search",
            BehaviourVersion: "1.0.0",
            PackageId: "search",
            GeometryIds: ["search-area", "staging-point"]);

        var proto = MissionTaskProtoMapper.ToProto(task);
        var mapped = MissionTaskProtoMapper.ToModel(proto, "connection-1");

        Assert.Equal(["search-area", "staging-point"], proto.GeometryIds);
        Assert.Equal(["search-area", "staging-point"], mapped.GeometryIds);
    }

    [Fact]
    public void GeometrySelection_ProvidesOperationalContext()
    {
        var selection = SelectionFactory.From(new GeometrySelectionContext(
            "zone-alpha",
            "Zone Alpha",
            GeometryDocumentKind.Zone,
            GeometryCoordinateFrame.GlobalWgs84,
            "connection-1",
            "Matching",
            "safety/exclusion",
            "Geometry library"));

        Assert.Equal(SelectionKind.Geometry, selection.Kind);
        Assert.Equal("zone-alpha", selection.Id);
        Assert.Contains(selection.Fields, item => item.Label == "Deployment" && item.Value == "Matching");
    }
}
