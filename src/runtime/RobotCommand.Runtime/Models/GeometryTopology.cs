
namespace RobotCommand.Models;

public static class GeometryTopology
{
    private const double Epsilon = 1e-12;

    public static IReadOnlyList<GeometryDocumentPoint> OpenVertices(
        IReadOnlyList<GeometryDocumentPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count > 1 && SameHorizontalPosition(points[0], points[^1]))
        {
            return points.Take(points.Count - 1).ToArray();
        }

        return points.ToArray();
    }

    public static IReadOnlyList<GeometryDocumentPoint> RemoveConsecutiveDuplicates(
        IReadOnlyList<GeometryDocumentPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var open = OpenVertices(points);
        if (open.Count < 2)
        {
            return open;
        }

        var result = new List<GeometryDocumentPoint>(open.Count);
        foreach (var point in open)
        {
            if (result.Count == 0 || !SameHorizontalPosition(result[^1], point))
            {
                result.Add(point);
            }
        }

        return result;
    }

    public static bool HasConsecutiveDuplicateVertices(
        IReadOnlyList<GeometryDocumentPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        for (var index = 1; index < points.Count; index++)
        {
            if (SameHorizontalPosition(points[index - 1], points[index]))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasRepeatedVertex(IReadOnlyList<GeometryDocumentPoint> points)
    {
        var open = OpenVertices(points);
        for (var left = 0; left < open.Count; left++)
        {
            for (var right = left + 1; right < open.Count; right++)
            {
                if (SameHorizontalPosition(open[left], open[right]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool HasNonZeroArea(IReadOnlyList<GeometryDocumentPoint> points)
    {
        var open = OpenVertices(points);
        if (open.Count < 3)
        {
            return false;
        }

        double twiceArea = 0;
        for (var index = 0; index < open.Count; index++)
        {
            var next = (index + 1) % open.Count;
            twiceArea += open[index].X * open[next].Y - open[next].X * open[index].Y;
        }

        return Math.Abs(twiceArea) > Epsilon;
    }

    public static bool HasSelfIntersection(IReadOnlyList<GeometryDocumentPoint> points)
    {
        var open = OpenVertices(points);
        if (open.Count < 4)
        {
            return false;
        }

        for (var first = 0; first < open.Count; first++)
        {
            var firstNext = (first + 1) % open.Count;
            for (var second = first + 1; second < open.Count; second++)
            {
                var secondNext = (second + 1) % open.Count;
                if (first == second || firstNext == second || secondNext == first)
                {
                    continue;
                }

                if (SegmentsIntersect(
                        open[first],
                        open[firstNext],
                        open[second],
                        open[secondNext]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool SameHorizontalPosition(
        GeometryDocumentPoint left,
        GeometryDocumentPoint right)
        => Math.Abs(left.X - right.X) <= Epsilon &&
           Math.Abs(left.Y - right.Y) <= Epsilon;

    private static bool SegmentsIntersect(
        GeometryDocumentPoint a,
        GeometryDocumentPoint b,
        GeometryDocumentPoint c,
        GeometryDocumentPoint d)
    {
        var o1 = Orientation(a, b, c);
        var o2 = Orientation(a, b, d);
        var o3 = Orientation(c, d, a);
        var o4 = Orientation(c, d, b);

        if (o1 != o2 && o3 != o4)
        {
            return true;
        }

        return o1 == 0 && OnSegment(a, c, b) ||
               o2 == 0 && OnSegment(a, d, b) ||
               o3 == 0 && OnSegment(c, a, d) ||
               o4 == 0 && OnSegment(c, b, d);
    }

    private static int Orientation(
        GeometryDocumentPoint a,
        GeometryDocumentPoint b,
        GeometryDocumentPoint c)
    {
        var value = (b.Y - a.Y) * (c.X - b.X) -
                    (b.X - a.X) * (c.Y - b.Y);
        if (Math.Abs(value) <= Epsilon)
        {
            return 0;
        }

        return value > 0 ? 1 : 2;
    }

    private static bool OnSegment(
        GeometryDocumentPoint a,
        GeometryDocumentPoint point,
        GeometryDocumentPoint b)
        => point.X <= Math.Max(a.X, b.X) + Epsilon &&
           point.X + Epsilon >= Math.Min(a.X, b.X) &&
           point.Y <= Math.Max(a.Y, b.Y) + Epsilon &&
           point.Y + Epsilon >= Math.Min(a.Y, b.Y);
}
