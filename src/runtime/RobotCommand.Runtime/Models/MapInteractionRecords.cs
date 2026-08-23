namespace RobotCommand.Models;

public sealed record MapCursorLocation(
    double LongitudeDegrees,
    double LatitudeDegrees,
    double ResolutionMetresPerPixel)
{
    public string CoordinateText => $"{LatitudeDegrees:0.000000}, {LongitudeDegrees:0.000000}";

    public string ResolutionText => ResolutionMetresPerPixel switch
    {
        < 1 => $"{ResolutionMetresPerPixel * 100:0} cm/px",
        < 1000 => $"{ResolutionMetresPerPixel:0.#} m/px",
        _ => $"{ResolutionMetresPerPixel / 1000:0.#} km/px"
    };
}

public sealed record VehicleTrackSample(
    string VehicleId,
    string ConnectionId,
    double LongitudeDegrees,
    double LatitudeDegrees,
    AvailabilityState State,
    DateTimeOffset ObservedAt);
