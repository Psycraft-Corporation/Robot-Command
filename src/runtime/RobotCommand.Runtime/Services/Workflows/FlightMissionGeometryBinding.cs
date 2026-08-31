using RobotCommand.Core;
using RobotCommand.Models;

namespace RobotCommand.Services.Workflows;

/// <summary>Applies one saved geometry to one compatible mission step.</summary>
public static class FlightMissionGeometryBinding
{
    public static FlightMissionStep Bind(FlightMissionStep step, GeometryDocument geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var compatible = step.Kind switch
        {
            FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.TimedLoiter
                => geometry.Kind == GeometryDocumentKind.PointOfInterest,
            FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.CorridorScan
                => geometry.Kind == GeometryDocumentKind.WaypointSequence,
            FlightMissionStepKind.SurveyZone
                => geometry.Kind == GeometryDocumentKind.Zone,
            _ => false
        };

        if (!compatible)
            throw new InvalidOperationException($"Geometry '{geometry.DisplayName}' is not compatible with the {step.DisplayName} step.");

        var coordinates = step.Kind switch
        {
            FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.TimedLoiter
                => geometry.Points.Take(1).Select(ToCoordinate).ToArray(),
            FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.CorridorScan
                => geometry.Points.Select(ToCoordinate).ToArray(),
            FlightMissionStepKind.SurveyZone
                => (geometry.Rings.Count > 0 ? geometry.Rings[0].Points : geometry.Points).Select(ToCoordinate).ToArray(),
            _ => Array.Empty<FlightMissionCoordinate>()
        };

        if (coordinates.Length == 0)
            throw new InvalidOperationException($"Geometry '{geometry.DisplayName}' has no coordinates.");

        return step with
        {
            SourceGeometryId = geometry.GeometryId,
            SourceGeometryName = geometry.DisplayName,
            SourceGeometryHash = geometry.ContentSha256,
            Coordinates = coordinates
        };
    }

    private static FlightMissionCoordinate ToCoordinate(GeometryDocumentPoint point)
        => new(point.LatitudeDegrees, point.LongitudeDegrees);
}
