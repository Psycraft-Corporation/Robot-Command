namespace RobotCommand.Services.Maps;

public readonly record struct MapNavigationCoordinate(
    double LongitudeDegrees,
    double LatitudeDegrees);

public static class MapNavigationMath
{
    // Web Mercator metres-per-pixel. This keeps a single unit at a useful
    // city-block scale instead of fitting the whole surrounding city.
    public const double SingleTargetResolution = 3;

    public static bool TryMean(
        IEnumerable<MapNavigationCoordinate> coordinates,
        out MapNavigationCoordinate mean)
    {
        var values = coordinates
            .Where(item => double.IsFinite(item.LongitudeDegrees) &&
                           double.IsFinite(item.LatitudeDegrees))
            .ToArray();
        if (values.Length == 0)
        {
            mean = default;
            return false;
        }

        var latitude = values.Average(item => item.LatitudeDegrees);
        var longitude = values.Average(item => item.LongitudeDegrees);
        while (longitude > 180) longitude -= 360;
        while (longitude < -180) longitude += 360;
        mean = new MapNavigationCoordinate(longitude, latitude);
        return true;
    }
}
