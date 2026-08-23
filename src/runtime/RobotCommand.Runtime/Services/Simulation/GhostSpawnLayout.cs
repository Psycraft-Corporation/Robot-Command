using RobotCommand.Models;

namespace RobotCommand.Services.Simulation;

/// <summary>Provides deterministic spacing for Ghost batches created around a map viewport.</summary>
public static class GhostSpawnLayout
{
    private const double EarthRadiusMetres = 6_378_137d;
    private const double SpacingPixels = 48d;
    private const double MinimumSpacingMetres = 2d;
    // Keep the batch visible even at world-scale map resolutions while still
    // bounding pathological or corrupt viewport values.
    private const double MaximumSpacingMetres = 20_000_000d;

    public static MapViewportSnapshot? ForBatch(
        MapViewportSnapshot? center,
        int index,
        int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        if (center is null || count == 1)
            return center;

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
        var rows = (int)Math.Ceiling(count / (double)columns);
        var column = index % columns;
        var row = index / columns;
        var spacingMetres = double.IsFinite(center.Resolution) && center.Resolution > 0
            ? Math.Clamp(center.Resolution * SpacingPixels, MinimumSpacingMetres, MaximumSpacingMetres)
            : MinimumSpacingMetres;
        var eastOffsetMetres = (column - (columns - 1) / 2d) * spacingMetres;
        var northOffsetMetres = (row - (rows - 1) / 2d) * spacingMetres;
        var latitudeRadians = center.LatitudeDegrees * Math.PI / 180d;
        var metresPerDegreeLatitude = Math.PI * EarthRadiusMetres / 180d;
        var metresPerDegreeLongitude = Math.Max(
            1d,
            metresPerDegreeLatitude * Math.Abs(Math.Cos(latitudeRadians)));

        return center with
        {
            LatitudeDegrees = center.LatitudeDegrees + northOffsetMetres / metresPerDegreeLatitude,
            LongitudeDegrees = center.LongitudeDegrees + eastOffsetMetres / metresPerDegreeLongitude
        };
    }
}
