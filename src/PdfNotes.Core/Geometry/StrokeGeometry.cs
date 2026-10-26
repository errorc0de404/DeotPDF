using System.Collections.Immutable;

namespace PdfNotes.Core.Geometry;

/// <summary>
/// Freehand stroke math (spec 7.2): light smoothing that keeps end points and sharp turns, simplification below a
/// visible tolerance, bounds and hit distance. All values are in the stroke's own space (canonical page points).
/// </summary>
public static class StrokeGeometry
{
    /// <summary>Turns sharper than this (degrees between consecutive segments) are corners and are never smoothed.</summary>
    public const double CornerAngleDegrees = 60;

    /// <summary>
    /// One 1-2-1 averaging pass over interior points. End points and corners stay exactly where the user put them,
    /// so the result never drifts more than half the local jitter. The live preview uses the same function so the
    /// committed shape matches what was shown while drawing.
    /// </summary>
    public static ImmutableArray<Point2> Smooth(IReadOnlyList<Point2> points)
    {
        int n = points.Count;
        var result = ImmutableArray.CreateBuilder<Point2>(n);
        if (n == 0) return result.MoveToImmutable();
        double cornerCos = Math.Cos(CornerAngleDegrees * Math.PI / 180);
        result.Add(points[0]);
        for (int i = 1; i < n - 1; i++)
        {
            Point2 a = points[i - 1], b = points[i], c = points[i + 1];
            double ux = b.X - a.X, uy = b.Y - a.Y, vx = c.X - b.X, vy = c.Y - b.Y;
            double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
            bool corner = lu > 0 && lv > 0 && (ux * vx + uy * vy) / (lu * lv) < cornerCos;
            result.Add(corner ? b : new((a.X + 2 * b.X + c.X) / 4, (a.Y + 2 * b.Y + c.Y) / 4));
        }
        if (n > 1) result.Add(points[n - 1]);
        return result.MoveToImmutable();
    }

    /// <summary>
    /// Ramer–Douglas–Peucker simplification: every removed point lies within <paramref name="tolerance"/> of the
    /// kept polyline. End points are always kept. A stroke whose points all coincide becomes a single dot.
    /// </summary>
    public static ImmutableArray<Point2> Simplify(IReadOnlyList<Point2> points, double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        int n = points.Count;
        if (n <= 2)
            return n == 2 && points[0] == points[1] ? [points[0]] : [.. points];
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, n - 1));
        while (stack.TryPop(out var span))
        {
            double worst = -1;
            int index = -1;
            for (int i = span.First + 1; i < span.Last; i++)
            {
                double d = DistanceToSegment(points[i], points[span.First], points[span.Last]);
                if (d > worst) { worst = d; index = i; }
            }
            if (index < 0 || worst <= tolerance) continue;
            keep[index] = true;
            stack.Push((span.First, index));
            stack.Push((index, span.Last));
        }
        var result = ImmutableArray.CreateBuilder<Point2>();
        for (int i = 0; i < n; i++)
            if (keep[i]) result.Add(points[i]);
        if (result.Count == 2 && result[0] == result[1]) result.RemoveAt(1);
        return result.ToImmutable();
    }

    /// <summary>Committed stroke shape: smoothing followed by simplification.</summary>
    public static ImmutableArray<Point2> Finish(IReadOnlyList<Point2> raw, double tolerance) => Simplify(Smooth(raw), tolerance);

    /// <summary>Axis-aligned bounds of the painted area (centerline grown by half the width; round caps and joins).</summary>
    public static Rect2 Bounds(IReadOnlyList<Point2> points, double widthPt)
    {
        if (points.Count == 0) throw new ArgumentException("A stroke needs at least one point.", nameof(points));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        double r = widthPt / 2;
        return new(minX - r, minY - r, maxX - minX + 2 * r, maxY - minY + 2 * r);
    }

    /// <summary>Shortest distance from <paramref name="p"/> to the centerline (to the point itself for a dot).</summary>
    public static double DistanceToPolyline(IReadOnlyList<Point2> points, Point2 p)
    {
        if (points.Count == 0) return double.PositiveInfinity;
        if (points.Count == 1) return p.DistanceTo(points[0]);
        double best = double.PositiveInfinity;
        for (int i = 1; i < points.Count; i++) best = Math.Min(best, DistanceToSegment(p, points[i - 1], points[i]));
        return best;
    }

    public static double DistanceToSegment(Point2 p, Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0) return p.DistanceTo(a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return p.DistanceTo(new(a.X + t * dx, a.Y + t * dy));
    }
}
