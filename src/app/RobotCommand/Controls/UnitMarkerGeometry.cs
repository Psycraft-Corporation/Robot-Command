using Avalonia;

namespace RobotCommand.Controls;

/// <summary>
/// The single map marker shape used for every unit backend.
/// </summary>
public static class UnitMarkerGeometry
{
    /// <summary>
    /// Creates the forward-pointing arrow polygon in top-left-local coordinates.
    /// The center notch keeps the tail visually distinct from the nose while the
    /// long axis makes heading unambiguous at a glance.
    /// </summary>
    public static Point[] Create(double width = 26, double height = 34)
    {
        if (!double.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        var center = width / 2;
        return
        [
            new Point(center, 1),
            new Point(width - 2, height - 2),
            new Point(center, height * 0.74),
            new Point(2, height - 2)
        ];
    }

    public static Point[] CreateCentered(double width = 26, double height = 34)
    {
        var points = Create(width, height);
        var center = new Point(width / 2, height / 2);
        return points
            .Select(point => new Point(point.X - center.X, point.Y - center.Y))
            .ToArray();
    }
}
