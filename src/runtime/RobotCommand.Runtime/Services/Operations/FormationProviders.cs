using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public sealed record FormationDestination(
    string VehicleId,
    double LatitudeDegrees,
    double LongitudeDegrees);

public sealed record FormationCalculation(
    string FormationId,
    string DisplayName,
    IReadOnlyList<FormationDestination> Destinations,
    IReadOnlyList<FormationPreviewPath> PreviewPaths);

public interface IFormationProvider
{
    string Id { get; }
    string DisplayName { get; }

    bool CanCalculate(IReadOnlyList<VehicleRecord> units);

    FormationCalculation Calculate(
        IReadOnlyList<VehicleRecord> units,
        MapCommandTarget start,
        MapCommandTarget end);
}

public sealed class LineFormationProvider : IFormationProvider
{
    public string Id => "line";
    public string DisplayName => "Line";

    public bool CanCalculate(IReadOnlyList<VehicleRecord> units)
        => units.Count >= 2;

    public FormationCalculation Calculate(
        IReadOnlyList<VehicleRecord> units,
        MapCommandTarget start,
        MapCommandTarget end)
    {
        if (!CanCalculate(units))
            throw new ArgumentException("A line formation requires at least two units.", nameof(units));
        if (!IsValid(start) || !IsValid(end))
            throw new ArgumentException("The formation points must contain finite WGS84 coordinates.");

        var last = units.Count - 1;
        var destinations = units
            .Select((unit, index) => new FormationDestination(
                unit.Id,
                start.LatitudeDegrees + ((end.LatitudeDegrees - start.LatitudeDegrees) * index / last),
                start.LongitudeDegrees + ((end.LongitudeDegrees - start.LongitudeDegrees) * index / last)))
            .ToArray();

        return new FormationCalculation(
            Id,
            DisplayName,
            destinations,
            [new FormationPreviewPath([start, end])]);
    }

    private static bool IsValid(MapCommandTarget point)
        => double.IsFinite(point.LatitudeDegrees) &&
           double.IsFinite(point.LongitudeDegrees) &&
           point.LatitudeDegrees is >= -90 and <= 90 &&
           point.LongitudeDegrees is >= -180 and <= 180;
}

public sealed class CircleFormationProvider : IFormationProvider
{
    public string Id => "circle";
    public string DisplayName => "Circle";

    public bool CanCalculate(IReadOnlyList<VehicleRecord> units)
        => units.Count >= 2;

    public FormationCalculation Calculate(
        IReadOnlyList<VehicleRecord> units,
        MapCommandTarget start,
        MapCommandTarget end)
    {
        if (!CanCalculate(units))
            throw new ArgumentException("A circle formation requires at least two units.", nameof(units));
        if (!IsValid(start) || !IsValid(end))
            throw new ArgumentException("The formation points must contain finite WGS84 coordinates.");

        // Use a local east/north approximation. This keeps the clicked points as
        // true opposite points on the circle, including the two-unit case, while
        // remaining accurate at the operational map scales used by the console.
        var centerLatitude = (start.LatitudeDegrees + end.LatitudeDegrees) / 2;
        var centerLongitude = NormalizeLongitude(
            start.LongitudeDegrees +
            (NormalizeLongitudeDelta(end.LongitudeDegrees - start.LongitudeDegrees) / 2));
        var longitudeScale = Math.Cos(DegreesToRadians(centerLatitude));
        if (longitudeScale <= 0)
            throw new ArgumentException("The formation points are too close to a geographic pole.");

        var startX = NormalizeLongitudeDelta(start.LongitudeDegrees - centerLongitude) * longitudeScale;
        var startY = start.LatitudeDegrees - centerLatitude;
        var radius = Math.Sqrt((startX * startX) + (startY * startY));
        if (radius <= double.Epsilon)
            throw new ArgumentException("Circle formation points must define a non-zero diameter.");

        var startAngle = Math.Atan2(startY, startX);
        const int previewPointCount = 72;
        var previewPoints = Enumerable.Range(0, previewPointCount + 1)
            .Select(index =>
            {
                var angle = startAngle + (Math.Tau * index / previewPointCount);
                var x = radius * Math.Cos(angle);
                var y = radius * Math.Sin(angle);
                return new MapCommandTarget(
                    centerLatitude + y,
                    NormalizeLongitude(centerLongitude + (x / longitudeScale)));
            })
            .ToArray();
        var destinations = units
            .Select((unit, index) =>
            {
                if (index == 0)
                {
                    return new FormationDestination(
                        unit.Id,
                        start.LatitudeDegrees,
                        start.LongitudeDegrees);
                }

                var angle = startAngle + (Math.Tau * index / units.Count);
                var x = radius * Math.Cos(angle);
                var y = radius * Math.Sin(angle);
                return new FormationDestination(
                    unit.Id,
                    centerLatitude + y,
                    NormalizeLongitude(centerLongitude + (x / longitudeScale)));
            })
            .ToArray();

        return new FormationCalculation(
            Id,
            DisplayName,
            destinations,
            [new FormationPreviewPath(previewPoints, Closed: true)]);
    }

    private static double NormalizeLongitude(double longitude)
    {
        while (longitude > 180)
            longitude -= 360;
        while (longitude < -180)
            longitude += 360;
        return longitude;
    }

    private static double NormalizeLongitudeDelta(double longitude)
    {
        while (longitude > 180)
            longitude -= 360;
        while (longitude < -180)
            longitude += 360;
        return longitude;
    }

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180;

    private static bool IsValid(MapCommandTarget point)
        => double.IsFinite(point.LatitudeDegrees) &&
           double.IsFinite(point.LongitudeDegrees) &&
           point.LatitudeDegrees is >= -90 and <= 90 &&
           point.LongitudeDegrees is >= -180 and <= 180;
}

public sealed class RectangleFormationProvider : IFormationProvider
{
    public string Id => "rectangle";
    public string DisplayName => "Rectangle";

    public bool CanCalculate(IReadOnlyList<VehicleRecord> units)
        => units.Count >= 2;

    public FormationCalculation Calculate(
        IReadOnlyList<VehicleRecord> units,
        MapCommandTarget start,
        MapCommandTarget end)
    {
        if (!CanCalculate(units))
            throw new ArgumentException("A rectangle formation requires at least two units.", nameof(units));
        if (!IsValid(start) || !IsValid(end))
            throw new ArgumentException("The formation points must contain finite WGS84 coordinates.");

        var centerLatitude = (start.LatitudeDegrees + end.LatitudeDegrees) / 2;
        var centerLongitude = NormalizeLongitude(
            start.LongitudeDegrees +
            (NormalizeLongitudeDelta(end.LongitudeDegrees - start.LongitudeDegrees) / 2));
        var longitudeScale = Math.Cos(DegreesToRadians(centerLatitude));
        if (longitudeScale <= 0)
            throw new ArgumentException("The formation points are too close to a geographic pole.");

        var startLocal = (
            X: NormalizeLongitudeDelta(start.LongitudeDegrees - centerLongitude) * longitudeScale,
            Y: start.LatitudeDegrees - centerLatitude);
        var endLocal = (
            X: NormalizeLongitudeDelta(end.LongitudeDegrees - centerLongitude) * longitudeScale,
            Y: end.LatitudeDegrees - centerLatitude);
        if (Math.Abs(endLocal.X - startLocal.X) <= double.Epsilon ||
            Math.Abs(endLocal.Y - startLocal.Y) <= double.Epsilon)
        {
            throw new ArgumentException("Rectangle formation points must define a non-zero rectangle.");
        }

        var corners = new[]
        {
            startLocal,
            (endLocal.X, startLocal.Y),
            endLocal,
            (startLocal.X, endLocal.Y),
            startLocal
        };
        var previewPoints = new[]
        {
            start,
            ToMapPoint(corners[1], centerLatitude, centerLongitude, longitudeScale),
            end,
            ToMapPoint(corners[3], centerLatitude, centerLongitude, longitudeScale),
            start
        };
        var segmentLengths = corners
            .Zip(corners.Skip(1), (first, second) => Distance(first, second))
            .ToArray();
        var perimeter = segmentLengths.Sum();
        var destinations = units
            .Select((unit, index) =>
            {
                if (index == 0)
                    return new FormationDestination(unit.Id, start.LatitudeDegrees, start.LongitudeDegrees);

                var point = PointAtDistance(
                    corners,
                    segmentLengths,
                    perimeter * index / units.Count);
                var mapPoint = ToMapPoint(point, centerLatitude, centerLongitude, longitudeScale);
                return new FormationDestination(unit.Id, mapPoint.LatitudeDegrees, mapPoint.LongitudeDegrees);
            })
            .ToArray();

        return new FormationCalculation(
            Id,
            DisplayName,
            destinations,
            [new FormationPreviewPath(previewPoints, Closed: true)]);
    }

    private static (double X, double Y) PointAtDistance(
        IReadOnlyList<(double X, double Y)> corners,
        IReadOnlyList<double> segmentLengths,
        double distance)
    {
        for (var index = 0; index < segmentLengths.Count; index++)
        {
            var segmentLength = segmentLengths[index];
            if (distance <= segmentLength || index == segmentLengths.Count - 1)
            {
                var ratio = segmentLength <= double.Epsilon ? 0 : distance / segmentLength;
                var first = corners[index];
                var second = corners[index + 1];
                return (
                    first.X + ((second.X - first.X) * ratio),
                    first.Y + ((second.Y - first.Y) * ratio));
            }

            distance -= segmentLength;
        }

        return corners[^1];
    }

    private static double Distance((double X, double Y) first, (double X, double Y) second)
        => Math.Sqrt(Math.Pow(second.X - first.X, 2) + Math.Pow(second.Y - first.Y, 2));

    private static MapCommandTarget ToMapPoint(
        (double X, double Y) point,
        double centerLatitude,
        double centerLongitude,
        double longitudeScale)
        => new(
            centerLatitude + point.Y,
            NormalizeLongitude(centerLongitude + (point.X / longitudeScale)));

    private static double NormalizeLongitude(double longitude)
    {
        while (longitude > 180)
            longitude -= 360;
        while (longitude < -180)
            longitude += 360;
        return longitude;
    }

    private static double NormalizeLongitudeDelta(double longitude)
    {
        while (longitude > 180)
            longitude -= 360;
        while (longitude < -180)
            longitude += 360;
        return longitude;
    }

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180;

    private static bool IsValid(MapCommandTarget point)
        => double.IsFinite(point.LatitudeDegrees) &&
           double.IsFinite(point.LongitudeDegrees) &&
           point.LatitudeDegrees is >= -90 and <= 90 &&
           point.LongitudeDegrees is >= -180 and <= 180;
}
