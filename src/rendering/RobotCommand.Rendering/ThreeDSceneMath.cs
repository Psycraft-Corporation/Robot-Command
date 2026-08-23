using RobotCommand.Core;

namespace RobotCommand.Rendering;

public static class ThreeDSceneMath
{
    public const double EarthRadiusMetres = 6378137d;

    public static ThreeDVector3 ToLocal(double latitude, double longitude, double altitude, ThreeDOriginSnapshot origin)
    {
        var latitudeRadians = origin.LatitudeDegrees * Math.PI / 180d;
        var east = (longitude - origin.LongitudeDegrees) * Math.PI / 180d * EarthRadiusMetres * Math.Cos(latitudeRadians);
        var north = (latitude - origin.LatitudeDegrees) * Math.PI / 180d * EarthRadiusMetres;
        return new(east, altitude - origin.AltitudeMetres, north);
    }

    public static ThreeDCameraSnapshot CreateWorldCamera(
        ThreeDVector3 target,
        double resolutionMetresPerPixel,
        double rotationDegrees = 0)
    {
        var distance = Math.Clamp(Math.Max(80d, resolutionMetresPerPixel * 140d), 80d, 5000d);
        const double pitch = 24d;
        return new(
            ThreeDProjection.OrbitPosition(target, rotationDegrees, pitch, distance),
            rotationDegrees,
            pitch,
            0,
            60,
            0.5,
            Math.Max(10000, distance * 4),
            Math.Clamp(distance / 30d, 1, 100),
            true,
            target,
            distance);
    }

    public static double Distance(ThreeDVector3 a, ThreeDVector3 b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
}
