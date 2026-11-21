using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace PdfNotes.Core.Geometry;

public enum RecognizedKind { Line, Arrow, Ellipse, Rectangle, Triangle }

/// <summary>
/// A stroke recognised as a shape (1.4 plan 1.2). Line/Arrow: <c>Points = [start, end]</c> (the arrow head is at
/// the end). Ellipse/Rectangle: <c>Points = [center, (halfWidth, halfHeight)]</c> with the half extents along the axes
/// turned by <see cref="RotationRad"/> (+Y down, so positive turns clockwise on screen). Triangle: the three corners,
/// rotation 0. Equality compares the points, so the same input gives an equal result.
/// </summary>
public sealed record RecognizedShape(RecognizedKind Kind, ImmutableArray<Point2> Points, double RotationRad)
{
    public bool Equals(RecognizedShape? other) =>
        other is not null && Kind == other.Kind && RotationRad.Equals(other.RotationRad) &&
        Points.IsDefault == other.Points.IsDefault && (Points.IsDefault || Points.SequenceEqual(other.Points));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(RotationRad);
        if (!Points.IsDefault)
            foreach (var p in Points) hash.Add(p);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Geometric fit of a hand-drawn stroke to a line, arrow, ellipse, rectangle or triangle (1.4 plan 1.2). A result
/// comes only when every input point lies within <c>tolerance</c> of the fitted outline; anything doubtful is
/// <c>null</c> so handwriting stays handwriting. Pure and deterministic.
/// </summary>
public static class ShapeRecognizer
{
    /// <summary>Closed when start and end are closer than this share of the stroke length.</summary>
    public const double ClosureFraction = 0.15;
    /// <summary>Each arrow barb is at most this share of the shaft (the head sits in the last quarter).</summary>
    public const double ArrowHeadFraction = 0.25;
    public const double BarbMinDegrees = 20, BarbMaxDegrees = 70;
    /// <summary>Rectangle corners are 90° ± this.</summary>
    public const double RightAngleSlackDegrees = 15;
    /// <summary>A barb shorter than this many tolerances is noise, not a head.</summary>
    public const double BarbMinTolerances = 2;
    /// <summary>A corner turns the stroke by more than this between the chords on either side of it.</summary>
    public const double CornerTurnDegrees = 50;
    /// <summary>Corner chords reach this share of the stroke length, but at least <see cref="CornerReachTolerances"/> tolerances.</summary>
    public const double CornerReachFraction = 1.0 / 16;
    public const double CornerReachTolerances = 2.5;
    /// <summary>The last share of the stroke searched for the point where a closed stroke meets its start.</summary>
    public const double OverdrawFraction = 0.25;
    private const int EllipseSamples = 180;
    /// <summary>
    /// An ellipse's short half axis is at least this many tolerances, at least this share of the long one, and the
    /// gap where the stroke closes is at most this share of the short half axis — so a narrow u, n or retraced stroke
    /// that happens to close within 15 % of its length stays handwriting.
    /// </summary>
    public const double EllipseMinHalfAxisTolerances = 2;
    public const double EllipseMinAxisRatio = 0.2;
    public const double EllipseMaxGapPerShortHalfAxis = 0.5;
    /// <summary>
    /// An ellipse's root-mean-square distance is at most this share of the tolerance: jitter scatters about the
    /// outline, while a flat side (a D) stays off it all along.
    /// </summary>
    public const double EllipseMaxRmsPerTolerance = 0.4;
    /// <summary>
    /// A triangle corner moves from the drawn corner to where the side lines meet only this many tolerances (a
    /// rounded pen corner); farther, the sides do not really meet there (a D's straight back and its curve).
    /// </summary>
    public const double TriangleCornerTolerances = 1.5;

    public static RecognizedShape? Recognize(IReadOnlyList<Point2> points, double minSize, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(minSize) || minSize < 0) throw new ArgumentOutOfRangeException(nameof(minSize));
        if (!double.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (points.Count < 3 || points.Any(p => !p.IsFinite)) return null;
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        if (Math.Max(maxX - minX, maxY - minY) < minSize) return null;
        // Length of the simplified path, so pen jitter does not inflate it.
        double length = PathLength(StrokeGeometry.Simplify(points, tolerance));
        if (length <= 0) return null;
        bool closed = points[0].DistanceTo(points[^1]) < length * ClosureFraction;
        return closed ? Closed(points, length, tolerance) : Open(points, tolerance);
    }

    /// <summary>
    /// Shape after the pen moves on from where it held (<paramref name="grab"/>) to <paramref name="to"/>. A line or
    /// arrow keeps its start and ends at <paramref name="to"/>; a rectangle or ellipse keeps the point opposite
    /// <paramref name="grab"/> through its center, a triangle its corner farthest from <paramref name="grab"/>, and
    /// scales and turns about it.
    /// </summary>
    public static RecognizedShape Dragged(RecognizedShape shape, Point2 grab, Point2 to)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Kind is RecognizedKind.Line or RecognizedKind.Arrow) return shape with { Points = [shape.Points[0], to] };
        Point2 center = shape.Points[0];
        // A triangle turns about its corner farthest from the grab; a rectangle or ellipse about the point opposite it.
        Point2 anchor = shape.Kind == RecognizedKind.Triangle
            ? shape.Points.Aggregate((best, p) => p.DistanceTo(grab) > best.DistanceTo(grab) ? p : best)
            : new(2 * center.X - grab.X, 2 * center.Y - grab.Y);
        double fromX = grab.X - anchor.X, fromY = grab.Y - anchor.Y, toX = to.X - anchor.X, toY = to.Y - anchor.Y;
        double from = Math.Sqrt(fromX * fromX + fromY * fromY);
        if (from < 1e-9) return shape;
        double scale = Math.Sqrt(toX * toX + toY * toY) / from;
        double turn = Math.Atan2(toY, toX) - Math.Atan2(fromY, fromX);
        double c = Math.Cos(turn) * scale, s = Math.Sin(turn) * scale;
        Point2 Map(Point2 p)
        {
            double x = p.X - anchor.X, y = p.Y - anchor.Y;
            return new(anchor.X + x * c - y * s, anchor.Y + x * s + y * c);
        }
        if (shape.Kind == RecognizedKind.Triangle) return shape with { Points = [.. shape.Points.Select(Map)] };
        Point2 half = shape.Points[1];
        return shape with { Points = [Map(center), new(half.X * scale, half.Y * scale)], RotationRad = shape.RotationRad + turn };
    }

    // ---- open strokes ----

    private static RecognizedShape? Open(IReadOnlyList<Point2> points, double tolerance)
    {
        // Line: total least squares, ends projected onto it.
        var (origin, direction) = FitLine(points, 0, points.Count - 1);
        Point2 a = Project(points[0], origin, direction), b = Project(points[^1], origin, direction);
        if (points.All(p => StrokeGeometry.DistanceToSegment(p, a, b) <= tolerance))
            return new(RecognizedKind.Line, [a, b], 0);
        return Arrow(points, tolerance);
    }

    /// <summary>Shaft to the point farthest from the start, then two short barbs bent back on either side.</summary>
    private static RecognizedShape? Arrow(IReadOnlyList<Point2> points, double tolerance)
    {
        Point2 start = points[0];
        double farthest = points.Max(p => p.DistanceTo(start));
        // The tip is reached twice (after the shaft and after the first barb): take the first visit.
        int tipIndex = 0;
        while (points[tipIndex].DistanceTo(start) < farthest - tolerance) tipIndex++;
        for (int i = tipIndex + 1; i < points.Count && points[i].DistanceTo(start) >= farthest - tolerance; i++)
            if (points[i].DistanceTo(start) > points[tipIndex].DistanceTo(start)) tipIndex = i;
        double shaft = points[tipIndex].DistanceTo(start);
        if (tipIndex >= points.Count - 1 || shaft <= 0) return null;
        Point2 tip = points[tipIndex];
        for (int i = 0; i <= tipIndex; i++)
            if (StrokeGeometry.DistanceToSegment(points[i], start, tip) > tolerance) return null;
        double ux = (tip.X - start.X) / shaft, uy = (tip.Y - start.Y) / shaft;
        double Side(Point2 p) => ux * (p.Y - tip.Y) - uy * (p.X - tip.X);
        int first = -1, second = -1;
        for (int i = tipIndex + 1; i < points.Count; i++)
            if (first < 0 || points[i].DistanceTo(tip) > points[first].DistanceTo(tip)) first = i;
        double firstSide = Side(points[first]);
        if (firstSide == 0) return null;
        for (int i = tipIndex + 1; i < points.Count; i++)
            if (Side(points[i]) * firstSide < 0 && (second < 0 || points[i].DistanceTo(tip) > points[second].DistanceTo(tip))) second = i;
        if (second < 0) return null;
        Point2 barbA = points[first], barbB = points[second];
        foreach (var end in (Point2[])[barbA, barbB])
        {
            double barb = end.DistanceTo(tip);
            if (barb < BarbMinTolerances * tolerance || barb > ArrowHeadFraction * shaft) return null;
            double cos = (-(end.X - tip.X) * ux - (end.Y - tip.Y) * uy) / barb;
            double degrees = Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
            if (degrees < BarbMinDegrees || degrees > BarbMaxDegrees) return null;
        }
        for (int i = tipIndex + 1; i < points.Count; i++)
            if (Math.Min(StrokeGeometry.DistanceToSegment(points[i], tip, barbA), StrokeGeometry.DistanceToSegment(points[i], tip, barbB)) > tolerance)
                return null;
        return new(RecognizedKind.Arrow, [start, tip], 0);
    }

    // ---- closed strokes ----

    private static RecognizedShape? Closed(IReadOnlyList<Point2> points, double length, double tolerance)
    {
        var loop = TrimOverdraw(points);
        double reach = Math.Max(length * CornerReachFraction, CornerReachTolerances * tolerance);
        var corners = Corners(loop, reach);
        RecognizedShape? cornered = corners?.Count switch
        {
            3 => Triangle(loop, corners, reach, tolerance),
            4 when RightAngled(loop, corners) => Rectangle(points),
            _ => null,
        };
        // A corner count that does not fit still may be a round shape with jitter.
        if (Fits(cornered, points, tolerance)) return cornered;
        var ellipse = Ellipse(loop);
        return Fits(ellipse, points, tolerance) && Rounded(ellipse, loop, tolerance) ? ellipse : null;
    }

    private static bool Rounded(RecognizedShape ellipse, List<Point2> loop, double tolerance)
    {
        Func<Point2, double> distance = Outline(ellipse);
        double shortHalf = Math.Min(ellipse.Points[1].X, ellipse.Points[1].Y), longHalf = Math.Max(ellipse.Points[1].X, ellipse.Points[1].Y);
        return shortHalf >= EllipseMinHalfAxisTolerances * tolerance &&
            shortHalf >= EllipseMinAxisRatio * longHalf &&
            loop[0].DistanceTo(loop[^1]) <= EllipseMaxGapPerShortHalfAxis * shortHalf &&
            Math.Sqrt(loop.Average(p => Math.Pow(distance(p), 2))) <= EllipseMaxRmsPerTolerance * tolerance;
    }

    private static bool Fits([NotNullWhen(true)] RecognizedShape? shape, IReadOnlyList<Point2> points, double tolerance)
    {
        if (shape is null) return false;
        Func<Point2, double> distance = Outline(shape);
        return points.All(p => distance(p) <= tolerance);
    }

    /// <summary>The stroke up to where it comes back nearest its start (within the last quarter), dropping overdraw.</summary>
    private static List<Point2> TrimOverdraw(IReadOnlyList<Point2> points)
    {
        double total = 0;
        var along = new double[points.Count];
        for (int i = 1; i < points.Count; i++) along[i] = total += points[i].DistanceTo(points[i - 1]);
        int end = points.Count - 1;
        for (int i = points.Count - 1; i > 0 && along[i] >= total * (1 - OverdrawFraction); i--)
            if (points[i].DistanceTo(points[0]) < points[end].DistanceTo(points[0])) end = i;
        return [.. points.Take(end + 1)];
    }

    /// <summary>
    /// Indices on the closed <paramref name="loop"/> where the chords reaching <paramref name="reach"/> back and
    /// forward turn by more than <see cref="CornerTurnDegrees"/>; null when every point turns that much.
    /// </summary>
    private static List<int>? Corners(List<Point2> loop, double reach)
    {
        int n = loop.Count;
        if (n < 4) return null;
        var turn = new double[n];
        for (int i = 0; i < n; i++)
        {
            int back = Reach(loop, i, -1, reach), forward = Reach(loop, i, 1, reach);
            if (back < 0 || forward < 0) continue;
            double ax = loop[i].X - loop[back].X, ay = loop[i].Y - loop[back].Y, bx = loop[forward].X - loop[i].X, by = loop[forward].Y - loop[i].Y;
            turn[i] = Math.Abs(Math.Atan2(ax * by - ay * bx, ax * bx + ay * by)) * 180 / Math.PI;
        }
        int calm = Array.FindIndex(turn, t => t <= CornerTurnDegrees);
        if (calm < 0) return null;
        var corners = new List<int>();
        int best = -1;
        for (int k = 1; k <= n; k++)
        {
            int i = (calm + k) % n;
            if (turn[i] > CornerTurnDegrees)
            {
                if (best < 0 || turn[i] > turn[best]) best = i;
            }
            else if (best >= 0)
            {
                corners.Add(best);
                best = -1;
            }
        }
        // A corner split by jitter into two runs counts once.
        for (int k = 0; corners.Count > 1 && k < corners.Count;)
        {
            int next = (k + 1) % corners.Count;
            if (loop[corners[k]].DistanceTo(loop[corners[next]]) < reach)
            {
                corners.RemoveAt(turn[corners[k]] >= turn[corners[next]] ? next : k);
                k = 0;
            }
            else k++;
        }
        corners.Sort();
        return corners;
    }

    /// <summary>First index from <paramref name="i"/> stepping <paramref name="step"/> (wrapping) at least <paramref name="reach"/> away.</summary>
    private static int Reach(List<Point2> loop, int i, int step, double reach)
    {
        int n = loop.Count;
        for (int k = 1; k < n; k++)
        {
            int j = ((i + step * k) % n + n) % n;
            if (loop[j].DistanceTo(loop[i]) >= reach) return j;
        }
        return -1;
    }

    private static bool RightAngled(List<Point2> loop, List<int> corners)
    {
        for (int k = 0; k < 4; k++)
        {
            Point2 p = loop[corners[(k + 3) % 4]], c = loop[corners[k]], q = loop[corners[(k + 1) % 4]];
            double degrees = Angle(p.X - c.X, p.Y - c.Y, q.X - c.X, q.Y - c.Y);
            if (Math.Abs(degrees - 90) > RightAngleSlackDegrees) return false;
        }
        return true;
    }

    /// <summary>Corners where the side lines (fitted away from the rounded corners) meet; the drawn corner when they do not meet near it.</summary>
    private static RecognizedShape Triangle(List<Point2> loop, List<int> corners, double reach, double tolerance)
    {
        int n = loop.Count;
        var sides = new (Point2 Origin, Point2 Direction)?[3];
        for (int k = 0; k < 3; k++)
        {
            int from = corners[k], to = corners[(k + 1) % 3];
            var side = new List<Point2>();
            for (int i = from; i != to; i = (i + 1) % n)
                if (loop[i].DistanceTo(loop[from]) >= reach && loop[i].DistanceTo(loop[to]) >= reach) side.Add(loop[i]);
            if (side.Count >= 2) sides[k] = FitLine(side, 0, side.Count - 1);
        }
        var vertices = new Point2[3];
        for (int k = 0; k < 3; k++)
        {
            Point2 raw = loop[corners[k]];
            var before = sides[(k + 2) % 3];
            var after = sides[k];
            Point2? meet = before is { } u && after is { } v ? Intersect(u.Origin, u.Direction, v.Origin, v.Direction) : null;
            vertices[k] = meet is { } m && m.DistanceTo(raw) <= Math.Min(reach, TriangleCornerTolerances * tolerance) ? m : raw;
        }
        return new(RecognizedKind.Triangle, [.. vertices], 0);
    }

    /// <summary>Minimum-area bounding rectangle: rotating calipers over the convex hull edges.</summary>
    private static RecognizedShape? Rectangle(IReadOnlyList<Point2> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count < 3) return null;
        double bestArea = double.MaxValue, bestAngle = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            Point2 a = hull[i], b = hull[(i + 1) % hull.Count];
            double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            var (_, half) = Extents(hull, angle);
            double area = half.X * half.Y;
            if (area < bestArea - 1e-9) { bestArea = area; bestAngle = angle; }
        }
        double rotation = QuarterTurn(bestAngle);
        var (center, halves) = Extents(hull, rotation);
        return new(RecognizedKind.Rectangle, [center, halves], rotation);
    }

    /// <summary>Center and axis directions from the stroke's covariance, half axes by least squares.</summary>
    private static RecognizedShape? Ellipse(List<Point2> loop)
    {
        // Moments of the closed polyline, weighted by length so uneven sampling does not pull the center.
        double weight = 0, mx = 0, my = 0, sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            Point2 a = loop[i], b = loop[(i + 1) % loop.Count];
            double l = a.DistanceTo(b);
            weight += l;
            mx += l * (a.X + b.X) / 2;
            my += l * (a.Y + b.Y) / 2;
            sxx += l * (a.X * a.X + a.X * b.X + b.X * b.X) / 3;
            syy += l * (a.Y * a.Y + a.Y * b.Y + b.Y * b.Y) / 3;
            sxy += l * (2 * a.X * a.Y + a.X * b.Y + b.X * a.Y + 2 * b.X * b.Y) / 6;
        }
        if (weight <= 0) return null;
        mx /= weight; my /= weight;
        double cxx = sxx / weight - mx * mx, cyy = syy / weight - my * my, cxy = sxy / weight - mx * my;
        double rotation = QuarterTurn(0.5 * Math.Atan2(2 * cxy, cxx - cyy));
        double c = Math.Cos(rotation), s = Math.Sin(rotation);
        // u²·A + v²·B = 1 in the axis frame.
        double uu = 0, uv = 0, vv = 0, ru = 0, rv = 0;
        foreach (var p in loop)
        {
            double dx = p.X - mx, dy = p.Y - my, u = dx * c + dy * s, v = -dx * s + dy * c;
            double u2 = u * u, v2 = v * v;
            uu += u2 * u2; uv += u2 * v2; vv += v2 * v2; ru += u2; rv += v2;
        }
        double det = uu * vv - uv * uv;
        if (Math.Abs(det) < 1e-12) return null;
        double a2 = (ru * vv - rv * uv) / det, b2 = (uu * rv - uv * ru) / det;
        if (!(a2 > 0 && b2 > 0)) return null;
        double[] q = Refine(loop, [mx, my, rotation, 1 / Math.Sqrt(a2), 1 / Math.Sqrt(b2)]);
        double turned = QuarterTurn(q[2]);
        // An odd number of quarter turns swaps the axes.
        bool swap = ((long)Math.Round((q[2] - turned) / (Math.PI / 2)) & 1) != 0;
        Point2 half = swap ? new(q[4], q[3]) : new(q[3], q[4]);
        return new(RecognizedKind.Ellipse, [new(q[0], q[1]), half], turned);
    }

    private const int RefineSteps = 8;

    /// <summary>
    /// Gauss–Newton on the Sampson distance (algebraic error over its gradient, close to the true distance) for
    /// center, rotation and half axes. A step is kept only when it lowers the squared error.
    /// </summary>
    private static double[] Refine(List<Point2> loop, double[] q)
    {
        double Residual(double[] p, Point2 point)
        {
            double c = Math.Cos(p[2]), s = Math.Sin(p[2]), dx = point.X - p[0], dy = point.Y - p[1];
            double u = dx * c + dy * s, v = -dx * s + dy * c, a2 = p[3] * p[3], b2 = p[4] * p[4];
            double f = u * u / a2 + v * v / b2 - 1, gu = 2 * u / a2, gv = 2 * v / b2;
            double g = Math.Sqrt(gu * gu + gv * gv);
            return g > 1e-12 ? f / g : 0;
        }
        double Cost(double[] p) => loop.Sum(point => Math.Pow(Residual(p, point), 2));
        double cost = Cost(q);
        for (int step = 0; step < RefineSteps; step++)
        {
            var jtj = new double[5, 5];
            var jtr = new double[5];
            var row = new double[5];
            foreach (var point in loop)
            {
                double r = Residual(q, point);
                for (int k = 0; k < 5; k++)
                {
                    double h = 1e-6 * Math.Max(1, Math.Abs(q[k]));
                    var moved = (double[])q.Clone();
                    moved[k] += h;
                    row[k] = (Residual(moved, point) - r) / h;
                }
                for (int i = 0; i < 5; i++)
                {
                    jtr[i] += row[i] * r;
                    for (int j = 0; j < 5; j++) jtj[i, j] += row[i] * row[j];
                }
            }
            // Light damping keeps a circle's free rotation from making the system singular.
            for (int i = 0; i < 5; i++) jtj[i, i] = jtj[i, i] * (1 + 1e-6) + 1e-9;
            double[]? delta = Solve(jtj, jtr);
            if (delta is null) break;
            var next = new double[5];
            for (int i = 0; i < 5; i++) next[i] = q[i] - delta[i];
            if (!(next[3] > 0 && next[4] > 0)) break;
            double nextCost = Cost(next);
            if (!(nextCost < cost)) break;
            q = next;
            cost = nextCost;
        }
        return q;
    }

    /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
    private static double[]? Solve(double[,] m, double[] rhs)
    {
        int n = rhs.Length;
        var a = (double[,])m.Clone();
        var b = (double[])rhs.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-12) return null;
            if (pivot != col)
            {
                for (int k = 0; k < n; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int k = col; k < n; k++) a[r, k] -= f * a[col, k];
                b[r] -= f * b[col];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = b[r];
            for (int k = r + 1; k < n; k++) sum -= a[r, k] * x[k];
            x[r] = sum / a[r, r];
        }
        return x;
    }

    // ---- outlines ----

    private static Func<Point2, double> Outline(RecognizedShape shape)
    {
        switch (shape.Kind)
        {
            case RecognizedKind.Triangle:
                var t = shape.Points;
                return p => Math.Min(StrokeGeometry.DistanceToSegment(p, t[0], t[1]),
                    Math.Min(StrokeGeometry.DistanceToSegment(p, t[1], t[2]), StrokeGeometry.DistanceToSegment(p, t[2], t[0])));
            case RecognizedKind.Rectangle:
            {
                Point2 center = shape.Points[0], half = shape.Points[1];
                double c = Math.Cos(shape.RotationRad), s = Math.Sin(shape.RotationRad);
                return p =>
                {
                    double dx = p.X - center.X, dy = p.Y - center.Y;
                    double u = Math.Abs(dx * c + dy * s) - half.X, v = Math.Abs(-dx * s + dy * c) - half.Y;
                    return u <= 0 && v <= 0 ? -Math.Max(u, v) : Math.Sqrt(Math.Pow(Math.Max(u, 0), 2) + Math.Pow(Math.Max(v, 0), 2));
                };
            }
            default:
            {
                Point2 center = shape.Points[0], half = shape.Points[1];
                double c = Math.Cos(shape.RotationRad), s = Math.Sin(shape.RotationRad);
                var ring = new Point2[EllipseSamples];
                for (int i = 0; i < ring.Length; i++)
                {
                    double a = 2 * Math.PI * i / ring.Length, u = half.X * Math.Cos(a), v = half.Y * Math.Sin(a);
                    ring[i] = new(center.X + u * c - v * s, center.Y + u * s + v * c);
                }
                return p =>
                {
                    double best = double.MaxValue;
                    for (int i = 0; i < ring.Length; i++) best = Math.Min(best, StrokeGeometry.DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Length]));
                    return best;
                };
            }
        }
    }

    // ---- helpers ----

    private static double PathLength(IReadOnlyList<Point2> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++) length += points[i].DistanceTo(points[i - 1]);
        return length;
    }

    /// <summary>Total least squares line through points[from..to]: centroid and unit direction.</summary>
    private static (Point2 Origin, Point2 Direction) FitLine(IReadOnlyList<Point2> points, int from, int to)
    {
        int count = to - from + 1;
        double mx = 0, my = 0;
        for (int i = from; i <= to; i++) { mx += points[i].X; my += points[i].Y; }
        mx /= count; my /= count;
        double cxx = 0, cyy = 0, cxy = 0;
        for (int i = from; i <= to; i++)
        {
            double dx = points[i].X - mx, dy = points[i].Y - my;
            cxx += dx * dx; cyy += dy * dy; cxy += dx * dy;
        }
        double angle = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        return (new(mx, my), new(Math.Cos(angle), Math.Sin(angle)));
    }

    private static Point2 Project(Point2 p, Point2 origin, Point2 direction)
    {
        double t = (p.X - origin.X) * direction.X + (p.Y - origin.Y) * direction.Y;
        return new(origin.X + direction.X * t, origin.Y + direction.Y * t);
    }

    private static Point2? Intersect(Point2 p, Point2 u, Point2 q, Point2 v)
    {
        double cross = u.X * v.Y - u.Y * v.X;
        if (Math.Abs(cross) < 1e-6) return null;
        double t = ((q.X - p.X) * v.Y - (q.Y - p.Y) * v.X) / cross;
        return new(p.X + u.X * t, p.Y + u.Y * t);
    }

    /// <summary>Unsigned angle between two vectors, degrees.</summary>
    private static double Angle(double ax, double ay, double bx, double by) =>
        Math.Abs(Math.Atan2(ax * by - ay * bx, ax * bx + ay * by)) * 180 / Math.PI;

    /// <summary>The same axes turned by a multiple of 90° into (−45°, 45°].</summary>
    private static double QuarterTurn(double radians)
    {
        double quarter = Math.PI / 2;
        double r = radians - quarter * Math.Floor(radians / quarter);
        return r > quarter / 2 ? r - quarter : r;
    }

    /// <summary>Center and half extents of the points in the frame turned by <paramref name="angle"/>.</summary>
    private static (Point2 Center, Point2 Half) Extents(IReadOnlyList<Point2> points, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var p in points)
        {
            double u = p.X * c + p.Y * s, v = -p.X * s + p.Y * c;
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
        return (new(cu * c - cv * s, cu * s + cv * c), new((maxU - minU) / 2, (maxV - minV) / 2));
    }

    /// <summary>Andrew's monotone chain, counter-clockwise without collinear points.</summary>
    private static List<Point2> ConvexHull(IReadOnlyList<Point2> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;
        var hull = new List<Point2>();
        static double Cross(Point2 o, Point2 a, Point2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            foreach (var p in pass == 0 ? sorted : Enumerable.Reverse(sorted))
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }
}
