using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public static class MapOrientationResolver
{
    public static double ResolveRotation(OperationalMapScene scene)
    {
        if (scene.OrientationMode == MapOrientationMode.NorthUp)
        {
            return 0;
        }

        var selected = scene.Vehicles.FirstOrDefault(item => item.Selected);
        if (selected?.HeadingDegrees is not { } heading || !double.IsFinite(heading))
        {
            return 0;
        }

        return Normalize(-heading);
    }

    private static double Normalize(double value)
    {
        var normalized = value % 360d;
        return normalized < -180d
            ? normalized + 360d
            : normalized > 180d
                ? normalized - 360d
                : normalized;
    }
}
