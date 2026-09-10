using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Workflows;
using Xunit;

namespace RobotCommand.Tests;

public sealed class FlightMissionGeometryBindingTests
{
    [Fact]
    public void TimedLoiterBinding_ChangesOnlyTheSelectedStep()
    {
        var original = Point("poi-original", "Original", 43.6500, -79.3800, "hash-original");
        var replacement = Point("poi-replacement", "Replacement", 43.6600, -79.3900, "hash-replacement");
        var first = new FlightMissionStep(
            "loiter-one", FlightMissionStepKind.TimedLoiter, original.GeometryId, original.DisplayName,
            original.ContentSha256, [new(43.6500, -79.3800)], RelativeAltitudeMetres: 35, LoiterDurationSeconds: 30);
        var second = new FlightMissionStep(
            "loiter-two", FlightMissionStepKind.TimedLoiter, original.GeometryId, original.DisplayName,
            original.ContentSha256, [new(43.6500, -79.3800)], RelativeAltitudeMetres: 40, LoiterDurationSeconds: 90);

        var rebound = FlightMissionGeometryBinding.Bind(first, replacement);

        Assert.Equal("poi-replacement", rebound.SourceGeometryId);
        Assert.Equal("Replacement", rebound.SourceGeometryName);
        Assert.Equal("hash-replacement", rebound.SourceGeometryHash);
        Assert.Equal(new FlightMissionCoordinate(43.6600, -79.3900), Assert.Single(rebound.FrozenCoordinates));
        Assert.Equal(30, rebound.LoiterDurationSeconds);
        Assert.Equal(35, rebound.RelativeAltitudeMetres);
        Assert.Equal("poi-original", second.SourceGeometryId);
        Assert.Equal(new FlightMissionCoordinate(43.6500, -79.3800), Assert.Single(second.FrozenCoordinates));
        Assert.Equal(90, second.LoiterDurationSeconds);
    }

    [Fact]
    public void Binding_RejectsMissingOrIncompatibleGeometry()
    {
        var loiter = new FlightMissionStep("loiter", FlightMissionStepKind.TimedLoiter);
        var emptyPoint = GeometryDocument.Create("empty", "Empty", GeometryDocumentKind.PointOfInterest);
        var route = GeometryDocument.Create("route", "Route", GeometryDocumentKind.WaypointSequence) with
        {
            Points = [GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65), GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66)]
        };

        var emptyException = Assert.Throws<InvalidOperationException>(() => FlightMissionGeometryBinding.Bind(loiter, emptyPoint));
        var incompatibleException = Assert.Throws<InvalidOperationException>(() => FlightMissionGeometryBinding.Bind(loiter, route));

        Assert.Contains("no coordinates", emptyException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not compatible", incompatibleException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Binding_RejectsNonGeometrySteps()
    {
        var takeoff = new FlightMissionStep("takeoff", FlightMissionStepKind.Takeoff);
        var point = Point("poi", "Point", 43.65, -79.38, "hash");

        Assert.Throws<InvalidOperationException>(() => FlightMissionGeometryBinding.Bind(takeoff, point));
    }

    private static GeometryDocument Point(string id, string name, double latitude, double longitude, string hash)
        => GeometryDocument.Create(id, name, GeometryDocumentKind.PointOfInterest) with
        {
            Points = [GeometryDocumentPoint.GlobalWgs84(longitude, latitude)],
            ContentSha256 = hash
        };
}
