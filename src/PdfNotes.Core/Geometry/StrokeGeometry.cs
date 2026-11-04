using System.Collections.Immutable;
using PdfNotes.Core.Models;

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
    public static ImmutableArray<Point2> Simplify(IReadOnlyList<Point2> points, double tolerance) =>
        [.. SimplifyIndices(points, tolerance, null).Select(i => points[i])];

    /// <summary>
    /// Indices <see cref="Simplify"/> keeps. With <paramref name="pressures"/>, a point is also kept where its pressure
    /// differs from the straight blend between the kept neighbours by more than <see cref="PressureTolerance"/>, so a
    /// pressure swell on a straight line survives (spec 7.6).
    /// </summary>
    public static int[] SimplifyIndices(IReadOnlyList<Point2> points, double tolerance, IReadOnlyList<double>? pressures)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (pressures is not null && pressures.Count != points.Count) throw new ArgumentException("One pressure per point.", nameof(pressures));
        int n = points.Count;
        bool Same(int i, int j) => points[i] == points[j] && (pressures is null || pressures[i] == pressures[j]);
        if (n <= 2) return n == 2 && Same(0, 1) ? [0] : [.. Enumerable.Range(0, n)];
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, n - 1));
        while (stack.TryPop(out var span))
        {
            double worst = 0;
            int index = -1;
            for (int i = span.First + 1; i < span.Last; i++)
            {
                // Errors are measured in tolerances so distance and pressure compare on one scale.
                double distance = DistanceToSegment(points[i], points[span.First], points[span.Last]);
                double d = tolerance > 0 ? distance / tolerance : distance > 0 ? double.MaxValue : 0;
                if (pressures is not null)
                {
                    double t = Along(points[i], points[span.First], points[span.Last]);
                    double expected = pressures[span.First] + (pressures[span.Last] - pressures[span.First]) * t;
                    d = Math.Max(d, Math.Abs(pressures[i] - expected) / PressureTolerance);
                }
                if (d > worst) { worst = d; index = i; }
            }
            if (index < 0 || worst <= 1) continue;
            keep[index] = true;
            stack.Push((span.First, index));
            stack.Push((index, span.Last));
        }
        var result = new List<int>();
        for (int i = 0; i < n; i++)
            if (keep[i]) result.Add(i);
        if (result.Count == 2 && Same(result[0], result[1])) result.RemoveAt(1);
        return [.. result];
    }

    /// <summary>Where the projection of <paramref name="p"/> falls on <paramref name="a"/>–<paramref name="b"/>, 0–1.</summary>
    private static double Along(Point2 p, Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
        return lengthSquared == 0 ? 0.5 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
    }

    /// <summary>Committed stroke shape: smoothing followed by simplification.</summary>
    public static ImmutableArray<Point2> Finish(IReadOnlyList<Point2> raw, double tolerance) => Simplify(Smooth(raw), tolerance);

    // ----- pen pressure (spec 7.6, schema v8) -----

    /// <summary>Width multiplier at pressure 0 and 1; pressure 0.5 draws the set width.</summary>
    public const double MinPressureFactor = 0.4, MaxPressureFactor = 1.6;

    /// <summary>Pressure changes smaller than this are dropped by simplification.</summary>
    public const double PressureTolerance = 0.05;

    /// <summary>Width multiplier for a pen pressure of 0–1.</summary>
    public static double PressureFactor(double pressure) =>
        MinPressureFactor + (MaxPressureFactor - MinPressureFactor) * Math.Clamp(double.IsFinite(pressure) ? pressure : 0.5, 0, 1);

    /// <summary>
    /// Smoothing with pressures: the same 1-2-1 pass as <see cref="Smooth(IReadOnlyList{Point2})"/>, applied to the pressure
    /// of every point that is smoothed, so a pressure spike from one noisy sample is softened like a jitter.
    /// </summary>
    public static (ImmutableArray<Point2> Points, ImmutableArray<double> Pressures) Smooth(IReadOnlyList<Point2> points, IReadOnlyList<double> pressures)
    {
        if (pressures.Count != points.Count) throw new ArgumentException("One pressure per point.", nameof(pressures));
        var smoothed = Smooth(points);
        var result = ImmutableArray.CreateBuilder<double>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            bool interior = i > 0 && i < points.Count - 1 && smoothed[i] != points[i];
            result.Add(interior ? (pressures[i - 1] + 2 * pressures[i] + pressures[i + 1]) / 4 : pressures[i]);
        }
        return (smoothed, result.MoveToImmutable());
    }

    /// <summary>Committed stroke with pressures: smoothing followed by simplification that also keeps pressure changes.</summary>
    public static (ImmutableArray<Point2> Points, ImmutableArray<double> Pressures) Finish(IReadOnlyList<Point2> raw, IReadOnlyList<double> pressures, double tolerance)
    {
        var (points, smoothed) = Smooth(raw, pressures);
        var keep = SimplifyIndices(points, tolerance, smoothed);
        return ([.. keep.Select(i => points[i])], [.. keep.Select(i => smoothed[i])]);
    }

    /// <summary>Half the painted width at point <paramref name="index"/>.</summary>
    public static double Radius(StrokeAnnotation stroke, int index) =>
        stroke is InkStroke { HasPressure: true } ink ? ink.WidthPt * PressureFactor(ink.Pressures[index]) / 2 : stroke.WidthPt / 2;

    /// <summary>The widest half-width anywhere on the stroke.</summary>
    public static double MaxRadius(StrokeAnnotation stroke) =>
        stroke is InkStroke { HasPressure: true } ink ? ink.WidthPt * PressureFactor(ink.Pressures.Max()) / 2 : stroke.WidthPt / 2;

    /// <summary>Axis-aligned painted bounds of a centerline stroke, with its pressure widths.</summary>
    public static Rect2 Bounds(StrokeAnnotation stroke)
    {
        if (stroke is not InkStroke { HasPressure: true }) return Bounds(stroke.Points, stroke.WidthPt);
        if (stroke.Points.Length == 0) throw new ArgumentException("A stroke needs at least one point.", nameof(stroke));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < stroke.Points.Length; i++)
        {
            var p = stroke.Points[i];
            double r = Radius(stroke, i);
            minX = Math.Min(minX, p.X - r); minY = Math.Min(minY, p.Y - r);
            maxX = Math.Max(maxX, p.X + r); maxY = Math.Max(maxY, p.Y + r);
        }
        return new(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// True when <paramref name="p"/> is within <paramref name="tolerance"/> of the painted centerline stroke. With pressures
    /// the half-width is blended along each segment like the painted outline.
    /// </summary>
    public static bool Reaches(StrokeAnnotation stroke, Point2 p, double tolerance)
    {
        if (stroke is not InkStroke { HasPressure: true }) return DistanceToPolyline(stroke.Points, p) <= stroke.WidthPt / 2 + tolerance;
        return PaintedDistance(stroke, p) <= tolerance;
    }

    /// <summary>
    /// Distance from <paramref name="p"/> to the painted area of a centerline stroke (negative inside): the outline of
    /// <see cref="StrokeRegion.PressureRegion"/> for a pressure stroke, the round-capped band otherwise.
    /// </summary>
    public static double PaintedDistance(StrokeAnnotation stroke, Point2 p)
    {
        var points = stroke.Points;
        if (points.Length == 1) return p.DistanceTo(points[0]) - Radius(stroke, 0);
        double best = double.PositiveInfinity;
        for (int i = 1; i < points.Length; i++)
            best = Math.Min(best, TaperDistance(p, points[i - 1], points[i], Radius(stroke, i - 1), Radius(stroke, i)));
        return best;
    }

    /// <summary>
    /// Shortest distance from the segment <paramref name="a"/>–<paramref name="b"/> to the painted area of a centerline
    /// stroke (0 or less when they touch). Each piece is convex, so its distance along the segment has one minimum.
    /// </summary>
    public static double PaintedDistance(StrokeAnnotation stroke, Point2 a, Point2 b)
    {
        var points = stroke.Points;
        if (points.Length == 1) return DistanceToSegment(points[0], a, b) - Radius(stroke, 0);
        double best = double.PositiveInfinity;
        for (int i = 1; i < points.Length; i++)
        {
            Point2 c = points[i - 1], d = points[i];
            double rc = Radius(stroke, i - 1), rd = Radius(stroke, i);
            double At(double t) => TaperDistance(new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t), c, d, rc, rd);
            double lo = 0, hi = 1;
            for (int k = 0; k < 40 && hi - lo > 1e-6; k++)
            {
                double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if (At(m1) <= At(m2)) hi = m2; else lo = m1;
            }
            best = Math.Min(best, Math.Min(At((lo + hi) / 2), Math.Min(At(0), At(1))));
            if (best <= 0) return best;
        }
        return best;
    }

    /// <summary>
    /// Signed distance to the hull of two circles (a tapered segment with round ends): negative inside. When one circle
    /// holds the other, the hull is that circle.
    /// </summary>
    public static double TaperDistance(Point2 p, Point2 a, Point2 b, double ra, double rb)
    {
        double bx = b.X - a.X, by = b.Y - a.Y, h = bx * bx + by * by, dr = ra - rb;
        if (h <= dr * dr) return Math.Min(p.DistanceTo(a) - ra, p.DistanceTo(b) - rb);
        double px = p.X - a.X, py = p.Y - a.Y;
        // Frame along the segment, scaled by its length: y runs from a (0) to b (1), x is the distance across.
        double qx = Math.Abs(px * by - py * bx) / h, qy = (px * bx + py * by) / h;
        double cx = Math.Sqrt(h - dr * dr), cy = dr;
        double k = cx * qy - cy * qx, m = cx * qx + cy * qy, n = qx * qx + qy * qy;
        if (k < 0) return Math.Sqrt(h * n) - ra;
        if (k > cx) return Math.Sqrt(h * (n + 1 - 2 * qy)) - rb;
        return m - ra;
    }

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
