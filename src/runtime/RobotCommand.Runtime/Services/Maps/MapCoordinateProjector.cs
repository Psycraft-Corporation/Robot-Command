using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public static class MapCoordinateProjector
{
    public const double MaximumWebMercatorLatitude = 85.05112878;
    private const double EarthRadiusMetres = 6378137d;
    private const double MinimumExtentPaddingMetres = 250d;

    public static bool TryProject(double longitudeDegrees, double latitudeDegrees, out ProjectedMapPoint point)
    {
        point = default;
        if (!double.IsFinite(longitudeDegrees) ||
            !double.IsFinite(latitudeDegrees) ||
            longitudeDegrees is < -180 or > 180 ||
            latitudeDegrees is < -MaximumWebMercatorLatitude or > MaximumWebMercatorLatitude)
        {
            return false;
        }

        var longitudeRadians = DegreesToRadians(longitudeDegrees);
        var latitudeRadians = DegreesToRadians(latitudeDegrees);
        point = new ProjectedMapPoint(
            EarthRadiusMetres * longitudeRadians,
            EarthRadiusMetres * Math.Log(Math.Tan((Math.PI / 4d) + (latitudeRadians / 2d))));
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    public static bool TryUnproject(double x, double y, out double longitudeDegrees, out double latitudeDegrees)
    {
        longitudeDegrees = 0;
        latitudeDegrees = 0;
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        longitudeDegrees = RadiansToDegrees(x / EarthRadiusMetres);
        latitudeDegrees = RadiansToDegrees((2d * Math.Atan(Math.Exp(y / EarthRadiusMetres))) - (Math.PI / 2d));
        return double.IsFinite(longitudeDegrees) &&
               double.IsFinite(latitudeDegrees) &&
               longitudeDegrees is >= -180 and <= 180 &&
               latitudeDegrees is >= -MaximumWebMercatorLatitude and <= MaximumWebMercatorLatitude;
    }

    public static bool TryBuildExtent(
        IEnumerable<ProjectedMapPoint> points,
        out ProjectedMapExtent extent,
        double paddingFraction = 0.12)
    {
        var values = points
            .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .ToArray();
        if (values.Length == 0)
        {
            extent = default;
            return false;
        }

        var minX = values.Min(point => point.X);
        var minY = values.Min(point => point.Y);
        var maxX = values.Max(point => point.X);
        var maxY = values.Max(point => point.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        var padding = Math.Max(
            MinimumExtentPaddingMetres,
            Math.Max(width, height) * Math.Max(0, paddingFraction));
        extent = new ProjectedMapExtent(minX, minY, maxX, maxY).Expand(padding);
        return true;
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;

    private static double RadiansToDegrees(double value) => value * 180d / Math.PI;
}
