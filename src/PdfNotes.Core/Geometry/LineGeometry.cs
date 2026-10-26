using System.Collections.Immutable;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Geometry;

/// <summary>
/// Straight lines and Bézier curves (spec 8). All values are in the line's local space (canonical page points for a
/// new line). A curve is the control polygon [Start, controls..., End]; controls are not points on the curve.
/// Screen painter, PDF appearance, hit testing and bounds all use the body/arrow geometry built here.
/// </summary>
public static class LineGeometry
{
    /// <summary>Arrow head size is proportional to the line width (spec 8.2).</summary>
    public const double ArrowLengthPerWidth = 5, ArrowHalfWidthPerWidth = 2.25;

    /// <summary>The body ends this far (fraction of the arrow length) inside the head so its round cap stays hidden.</summary>
    public const double ArrowTrimFraction = 0.6;

    /// <summary>Directions shorter than this are degenerate; the next control point is used instead.</summary>
    public const double DegenerateLength = 1e-9;

    public static Point2[] ControlPolygon(LineAnnotation line) => [line.Start, .. line.Controls, line.End];

    public static LineAnnotation WithPolygon(LineAnnotation line, IReadOnlyList<Point2> polygon)
    {
        var kind = polygon.Count switch
        {
            2 => LineKind.Straight,
            3 => LineKind.Quadratic,
            4 => LineKind.Cubic,
            _ => throw new ArgumentException("A line has 2 to 4 control points.", nameof(polygon))
        };
        return line with { LineKind = kind, Start = polygon[0], End = polygon[^1], Controls = [.. polygon.Skip(1).Take(polygon.Count - 2)] };
    }

    /// <summary>De Casteljau evaluation; exact for every degree.</summary>
    public static Point2 Evaluate(IReadOnlyList<Point2> polygon, double t)
    {
        Span<Point2> p = stackalloc Point2[polygon.Count];
        for (int i = 0; i < p.Length; i++) p[i] = polygon[i];
        for (int level = p.Length - 1; level > 0; level--)
            for (int i = 0; i < level; i++) p[i] = Lerp(p[i], p[i + 1], t);
        return p[0];
    }

    /// <summary>Splits the curve at <paramref name="t"/> into two curves of the same degree.</summary>
    public static (Point2[] Left, Point2[] Right) Split(IReadOnlyList<Point2> polygon, double t)
    {
        int n = polygon.Count;
        var work = polygon.ToArray();
        var left = new Point2[n];
        var right = new Point2[n];
        for (int level = 0; level < n; level++)
        {
            left[level] = work[0];
            right[n - 1 - level] = work[n - 1 - level];
            for (int i = 0; i < n - 1 - level; i++) work[i] = Lerp(work[i], work[i + 1], t);
        }
        return (left, right);
    }

    /// <summary>The part of the curve between parameters <paramref name="t0"/> and <paramref name="t1"/>.</summary>
    public static Point2[] SubCurve(IReadOnlyList<Point2> polygon, double t0, double t1)
    {
        if (t0 <= 0 && t1 >= 1) return polygon.ToArray();
        var head = t1 >= 1 ? polygon.ToArray() : Split(polygon, t1).Left;
        return t0 <= 0 ? head : Split(head, t0 / t1).Right;
    }

    /// <summary>
    /// Polyline within <paramref name="tolerance"/> of the curve, using the uniform-subdivision bound
    /// d(d-1)/8 · max|Δ²P| / n². Straight lines return their two end points.
    /// </summary>
    public static Point2[] Flatten(IReadOnlyList<Point2> polygon, double tolerance)
    {
        if (polygon.Count == 2) return [polygon[0], polygon[1]];
        int degree = polygon.Count - 1;
        double secondDifference = 0;
        for (int i = 0; i + 2 < polygon.Count; i++)
        {
            var d = new Point2(polygon[i].X - 2 * polygon[i + 1].X + polygon[i + 2].X, polygon[i].Y - 2 * polygon[i + 1].Y + polygon[i + 2].Y);
            secondDifference = Math.Max(secondDifference, Length(d));
        }
        double bound = degree * (degree - 1) / 8.0 * secondDifference / Math.Max(tolerance, 1e-6);
        int segments = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(bound)), 1, 2048);
        var result = new Point2[segments + 1];
        for (int i = 0; i <= segments; i++) result[i] = Evaluate(polygon, (double)i / segments);
        return result;
    }

    /// <summary>
    /// Unit direction the curve leaves through its end (or start) point. A zero-length end tangent falls back to the
    /// next distinct control point, then to the other end; a fully degenerate curve points along +X (no NaN).
    /// </summary>
    public static Point2 EndDirection(IReadOnlyList<Point2> polygon, bool atEnd)
    {
        var tip = atEnd ? polygon[^1] : polygon[0];
        for (int k = 1; k < polygon.Count; k++)
        {
            var other = atEnd ? polygon[^(k + 1)] : polygon[k];
            var d = new Point2(tip.X - other.X, tip.Y - other.Y);
            double length = Length(d);
            if (length > DegenerateLength && double.IsFinite(length)) return new(d.X / length, d.Y / length);
        }
        return atEnd ? new(1, 0) : new(-1, 0);
    }

    public static double ArrowLength(double widthPt) => widthPt * ArrowLengthPerWidth;

    /// <summary>Filled arrow head: tip, then the two base corners.</summary>
    public static Point2[] ArrowHead(IReadOnlyList<Point2> polygon, bool atEnd, double widthPt)
    {
        var tip = atEnd ? polygon[^1] : polygon[0];
        var d = EndDirection(polygon, atEnd);
        double length = ArrowLength(widthPt), half = widthPt * ArrowHalfWidthPerWidth;
        var b = new Point2(tip.X - d.X * length, tip.Y - d.Y * length);
        return [tip, new(b.X - d.Y * half, b.Y + d.X * half), new(b.X + d.Y * half, b.Y - d.X * half)];
    }

    public static IEnumerable<Point2[]> ArrowHeads(LineAnnotation line)
    {
        var polygon = ControlPolygon(line);
        if (line.StartArrow) yield return ArrowHead(polygon, false, line.WidthPt);
        if (line.EndArrow) yield return ArrowHead(polygon, true, line.WidthPt);
    }

    /// <summary>
    /// The stroked part of the line: the curve trimmed under each arrow head. Null when the heads cover the whole line
    /// (a very short line with arrows draws only its heads).
    /// </summary>
    public static Point2[]? Body(LineAnnotation line)
    {
        var polygon = ControlPolygon(line);
        if (!line.StartArrow && !line.EndArrow) return polygon;
        double trim = ArrowLength(line.WidthPt) * ArrowTrimFraction;
        double t0 = line.StartArrow ? ParameterAtDistance(polygon, false, trim) : 0;
        double t1 = line.EndArrow ? ParameterAtDistance(polygon, true, trim) : 1;
        return t1 - t0 > 1e-9 ? SubCurve(polygon, t0, t1) : null;
    }

    /// <summary>First parameter, walking from the start (or end), whose point is <paramref name="distance"/> from that end.</summary>
    private static double ParameterAtDistance(IReadOnlyList<Point2> polygon, bool fromEnd, double distance)
    {
        var tip = fromEnd ? polygon[^1] : polygon[0];
        const int samples = 64;
        double previous = fromEnd ? 1 : 0;
        for (int i = 1; i <= samples; i++)
        {
            double t = fromEnd ? 1 - (double)i / samples : (double)i / samples;
            if (Evaluate(polygon, t).DistanceTo(tip) < distance) { previous = t; continue; }
            double lo = previous, hi = t;
            for (int k = 0; k < 40; k++)
            {
                double mid = (lo + hi) / 2;
                if (Evaluate(polygon, mid).DistanceTo(tip) < distance) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        }
        return fromEnd ? 0 : 1; // the whole curve is within the head
    }

    /// <summary>Flattening tolerance for picking and bounds; well below what is visible.</summary>
    public const double ShapeTolerancePt = 0.02;

    /// <summary>Axis-aligned painted bounds in local space: curve grown by half the width (round caps) plus arrow heads.</summary>
    public static Rect2 Bounds(LineAnnotation line)
    {
        var points = Flatten(ControlPolygon(line), ShapeTolerancePt);
        double r = line.WidthPt / 2 + ShapeTolerancePt;
        double minX = points.Min(p => p.X) - r, minY = points.Min(p => p.Y) - r;
        double maxX = points.Max(p => p.X) + r, maxY = points.Max(p => p.Y) + r;
        foreach (var head in ArrowHeads(line))
            foreach (var p in head)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        return new(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>True when <paramref name="local"/> is on the stroked curve (with tolerance) or inside an arrow head.</summary>
    public static bool Hit(LineAnnotation line, Point2 local, double tolerance)
    {
        var points = Flatten(ControlPolygon(line), ShapeTolerancePt);
        if (StrokeGeometry.DistanceToPolyline(points, local) <= line.WidthPt / 2 + tolerance) return true;
        return ArrowHeads(line).Any(head => InTriangle(head, local) || DistanceToClosed(head, local) <= tolerance);
    }

    private static bool InTriangle(Point2[] t, Point2 p)
    {
        double d1 = Cross(t[0], t[1], p), d2 = Cross(t[1], t[2], p), d3 = Cross(t[2], t[0], p);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    private static double DistanceToClosed(Point2[] polygon, Point2 p) =>
        Enumerable.Range(0, polygon.Length).Min(i => StrokeGeometry.DistanceToSegment(p, polygon[i], polygon[(i + 1) % polygon.Length]));

    private static double Cross(Point2 a, Point2 b, Point2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    // ----- fitting (spec 8.2) -----

    /// <summary>Result of fitting a freehand path with fixed end points.</summary>
    public sealed record CurveFit(LineKind Kind, ImmutableArray<Point2> Controls, double MaxError);

    /// <summary>
    /// Fits a single Bézier through the first and last input point. A quadratic is tried first and kept when its
    /// largest distance to the input is within <paramref name="tolerance"/>; otherwise a cubic is used.
    /// <paramref name="forcedControls"/> 1 or 2 overrides the automatic choice.
    /// </summary>
    public static CurveFit FitCurve(IReadOnlyList<Point2> path, double tolerance, int? forcedControls = null)
    {
        if (path.Count < 2) throw new ArgumentException("A curve needs two points.", nameof(path));
        if (forcedControls is not (null or 1 or 2)) throw new ArgumentOutOfRangeException(nameof(forcedControls));
        if (forcedControls != 2)
        {
            var q = Fit(path, 3);
            double error = MaxDistance(path, q);
            if (forcedControls == 1 || error <= tolerance) return new(LineKind.Quadratic, [q[1]], error);
        }
        var c = Fit(path, 4);
        return new(LineKind.Cubic, [c[1], c[2]], MaxDistance(path, c));
    }

    /// <summary>Largest distance from an input point to the curve.</summary>
    public static double MaxDistance(IReadOnlyList<Point2> path, IReadOnlyList<Point2> polygon)
    {
        var curve = Flatten(polygon, ShapeTolerancePt / 4);
        return path.Max(p => StrokeGeometry.DistanceToPolyline(curve, p));
    }

    /// <summary>
    /// Least-squares Bézier of 2 or 3 degrees with fixed ends: chord-length parameters refined by Newton steps
    /// (Schneider). The best polygon over the iterations is returned.
    /// </summary>
    private static Point2[] Fit(IReadOnlyList<Point2> path, int count)
    {
        var p0 = path[0];
        var p1 = path[^1];
        var t = ChordParameters(path);
        Point2[] best = Solve(path, t, count);
        double bestError = MaxDistance(path, best);
        for (int iteration = 0; iteration < 6 && path.Count > 2; iteration++)
        {
            for (int i = 1; i < t.Length - 1; i++) t[i] = Math.Clamp(Newton(best, path[i], t[i]), 0, 1);
            var candidate = Solve(path, t, count);
            double error = MaxDistance(path, candidate);
            if (error < bestError) { best = candidate; bestError = error; }
        }
        best[0] = p0;
        best[^1] = p1;
        return best;
    }

    private static double[] ChordParameters(IReadOnlyList<Point2> path)
    {
        var t = new double[path.Count];
        for (int i = 1; i < path.Count; i++) t[i] = t[i - 1] + path[i].DistanceTo(path[i - 1]);
        double total = t[^1];
        for (int i = 1; i < t.Length; i++) t[i] = total > 0 ? t[i] / total : (double)i / (t.Length - 1);
        t[^1] = 1;
        return t;
    }

    private static Point2[] Solve(IReadOnlyList<Point2> path, double[] t, int count)
    {
        var a = path[0];
        var b = path[^1];
        if (count == 3)
        {
            // B(t) = (1-t)²A + 2t(1-t)C + t²B; minimize Σ|B(tᵢ) - Qᵢ|² over C.
            double den = 0, x = 0, y = 0;
            for (int i = 0; i < path.Count; i++)
            {
                double u = 1 - t[i], b1 = 2 * t[i] * u;
                den += b1 * b1;
                x += b1 * (path[i].X - u * u * a.X - t[i] * t[i] * b.X);
                y += b1 * (path[i].Y - u * u * a.Y - t[i] * t[i] * b.Y);
            }
            return den > 1e-12 ? [a, new(x / den, y / den), b] : [a, Lerp(a, b, .5), b];
        }
        double c11 = 0, c12 = 0, c22 = 0, x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        for (int i = 0; i < path.Count; i++)
        {
            double s = t[i], u = 1 - s;
            double b0 = u * u * u, b1 = 3 * s * u * u, b2 = 3 * s * s * u, b3 = s * s * s;
            double rx = path[i].X - b0 * a.X - b3 * b.X, ry = path[i].Y - b0 * a.Y - b3 * b.Y;
            c11 += b1 * b1; c12 += b1 * b2; c22 += b2 * b2;
            x1 += b1 * rx; y1 += b1 * ry; x2 += b2 * rx; y2 += b2 * ry;
        }
        double det = c11 * c22 - c12 * c12;
        if (Math.Abs(det) <= 1e-12 * Math.Max(1, c11 * c22))
        {
            var q = Solve(path, t, 3);
            return Elevate(q);
        }
        return [a, new((x1 * c22 - x2 * c12) / det, (y1 * c22 - y2 * c12) / det), new((c11 * x2 - c12 * x1) / det, (c11 * y2 - c12 * y1) / det), b];
    }

    /// <summary>One Newton step towards the parameter of the curve point closest to <paramref name="p"/>.</summary>
    private static double Newton(Point2[] polygon, Point2 p, double t)
    {
        var d1 = Derivative(polygon);
        var d2 = Derivative(d1);
        var q = Evaluate(polygon, t);
        var q1 = Evaluate(d1, t);
        var q2 = d2.Length == 0 ? default : Evaluate(d2, t);
        double numerator = (q.X - p.X) * q1.X + (q.Y - p.Y) * q1.Y;
        double denominator = q1.X * q1.X + q1.Y * q1.Y + (q.X - p.X) * q2.X + (q.Y - p.Y) * q2.Y;
        return Math.Abs(denominator) < 1e-12 ? t : t - numerator / denominator;
    }

    private static Point2[] Derivative(IReadOnlyList<Point2> polygon)
    {
        int n = polygon.Count - 1;
        var result = new Point2[n];
        for (int i = 0; i < n; i++) result[i] = new(n * (polygon[i + 1].X - polygon[i].X), n * (polygon[i + 1].Y - polygon[i].Y));
        return result;
    }

    // ----- kind conversion (spec 8.2) -----

    /// <summary>Exact degree elevation of a quadratic: the cubic draws the identical curve.</summary>
    public static Point2[] Elevate(IReadOnlyList<Point2> quadratic) =>
        [quadratic[0], Lerp(quadratic[0], quadratic[1], 2.0 / 3), Lerp(quadratic[2], quadratic[1], 2.0 / 3), quadratic[2]];

    /// <summary>
    /// Changes the number of control points. Raising (straight → curve, quadratic → cubic) keeps the shape exactly;
    /// lowering keeps both end points and uses the closest least-squares shape.
    /// </summary>
    public static LineAnnotation Convert(LineAnnotation line, LineKind kind)
    {
        if (line.LineKind == kind) return line;
        var p = ControlPolygon(line);
        Point2[] polygon = (line.LineKind, kind) switch
        {
            (_, LineKind.Straight) => [p[0], p[^1]],
            (LineKind.Straight, LineKind.Quadratic) => [p[0], Lerp(p[0], p[1], .5), p[1]],
            (LineKind.Straight, LineKind.Cubic) => [p[0], Lerp(p[0], p[1], 1.0 / 3), Lerp(p[0], p[1], 2.0 / 3), p[1]],
            (LineKind.Quadratic, LineKind.Cubic) => Elevate(p),
            (LineKind.Cubic, LineKind.Quadratic) => Reduce(p),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return WithPolygon(line, polygon);
    }

    /// <summary>Closest quadratic to a cubic with the same end points (least squares over the cubic's own parameters).</summary>
    private static Point2[] Reduce(IReadOnlyList<Point2> cubic)
    {
        const int samples = 65;
        var path = new Point2[samples];
        var t = new double[samples];
        for (int i = 0; i < samples; i++) { t[i] = (double)i / (samples - 1); path[i] = Evaluate(cubic, t[i]); }
        var best = Solve(path, t, 3);
        double bestError = MaxDistance(path, best);
        for (int iteration = 0; iteration < 6; iteration++)
        {
            for (int i = 1; i < samples - 1; i++) t[i] = Math.Clamp(Newton(best, path[i], t[i]), 0, 1);
            var candidate = Solve(path, t, 3);
            double error = MaxDistance(path, candidate);
            if (error < bestError) { best = candidate; bestError = error; }
        }
        return [cubic[0], best[1], cubic[^1]];
    }

    // ----- dash patterns -----

    /// <summary>
    /// Dash pattern in multiples of the line width for round caps (each dash grows by one width).
    /// The same numbers drive WPF (relative to thickness) and PDF (multiplied by the width).
    /// </summary>
    public static double[] DashPattern(DashStyle dash) => dash switch
    {
        DashStyle.Solid => [],
        DashStyle.Dash => [3, 2.5],
        DashStyle.Dot => [0, 2],
        DashStyle.DashDot => [3, 2.5, 0, 2.5],
        _ => throw new ArgumentOutOfRangeException(nameof(dash))
    };

    private static Point2 Lerp(Point2 a, Point2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static double Length(Point2 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
}
