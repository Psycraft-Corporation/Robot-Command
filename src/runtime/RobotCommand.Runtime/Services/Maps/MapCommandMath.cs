namespace RobotCommand.Services.Maps;

public static class MapCommandMath
{
    public static double BearingDegrees(
        double? originLatitude,
        double? originLongitude,
        double targetLatitude,
        double targetLongitude)
    {
        if (originLatitude is not double latitude || originLongitude is not double longitude ||
            !double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            return 0;
        }

        var lat1 = latitude * Math.PI / 180d;
        var lat2 = targetLatitude * Math.PI / 180d;
        var deltaLongitude = (targetLongitude - longitude) * Math.PI / 180d;
        var y = Math.Sin(deltaLongitude) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) -
                Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLongitude);
        return (Math.Atan2(y, x) * 180d / Math.PI + 360d) % 360d;
    }

    public static double RelativeBearingDegrees(
        double? originLatitude,
        double? originLongitude,
        double? vehicleHeadingDegrees,
        double targetLatitude,
        double targetLongitude)
    {
        var bearing = BearingDegrees(originLatitude, originLongitude, targetLatitude, targetLongitude);
        var heading = vehicleHeadingDegrees is double value && double.IsFinite(value) ? value : 0;
        return NormalizeSigned(bearing - heading);
    }

    private static double NormalizeSigned(double degrees)
        => (degrees + 540d) % 360d - 180d;
}
