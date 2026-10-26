using System.Collections.Immutable;
using Clipper2Lib;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Geometry;

/// <summary>
/// Painted regions as polygons (spec 5.5, 7.4). A stroke's region is its centerline grown by half the width with round
/// caps and joins — exactly what the painter strokes — or its stored outline. The area eraser subtracts swept circles
/// from that region, so cutting only the edge of a thick stroke keeps the rest of it unchanged. Clipper2 does the
/// polygon arithmetic on a 0.001pt grid; arcs are flattened within <see cref="ArcTolerancePt"/>.
/// </summary>
public static class StrokeRegion
{
    /// <summary>Flattening of round caps, joins and eraser circles; about 0.1 DIP at 800%.</summary>
    public const double ArcTolerancePt = 0.01;

    /// <summary>Decimal places of the polygon grid (0.001pt).</summary>
    public const int Precision = 3;

    /// <summary>Pieces smaller than this (pt²) are numerical crumbs, not visible ink, and are dropped.</summary>
    public const double MinPieceAreaPt2 = 0.01;

    /// <summary>The painted region of a stroke in its local space.</summary>
    public static PathsD Region(StrokeAnnotation stroke)
    {
        if (stroke.IsOutlined) return ToPaths(stroke.Outline);
        var points = stroke.Points;
        double r = stroke.WidthPt / 2;
        if (points.All(p => p == points[0])) return [Circle(points[0], r)];
        var path = new PathD(points.Length);
        foreach (var p in points) path.Add(new(p.X, p.Y));
        return Clipper.InflatePaths([path], r, JoinType.Round, EndType.Round, 2, Precision, ArcTolerancePt);
    }

    /// <summary>The area one eraser step sweeps: the segment <paramref name="a"/>–<paramref name="b"/> grown by <paramref name="radius"/>.</summary>
    public static PathsD Capsule(Point2 a, Point2 b, double radius)
    {
        if (a == b) return [Circle(a, radius)];
        return Clipper.InflatePaths([new PathD { new(a.X, a.Y), new(b.X, b.Y) }], radius, JoinType.Round, EndType.Round, 2, Precision, ArcTolerancePt);
    }

    public static PathD Circle(Point2 center, double radius)
    {
        int steps = Math.Clamp((int)Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - ArcTolerancePt / Math.Max(radius, ArcTolerancePt)))), 8, 4096);
        var path = new PathD(steps);
        for (int i = 0; i < steps; i++)
        {
            double angle = 2 * Math.PI * i / steps;
            path.Add(new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
        return path;
    }

    public static PathsD Subtract(PathsD region, PathsD cut) => Clipper.Difference(region, cut, FillRule.NonZero, Precision);

    /// <summary>Area of <paramref name="region"/> inside the even-odd interior of <paramref name="area"/>.</summary>
    public static double AreaInside(PathsD region, PathsD area) => Area(Clipper.Intersect(region, area, FillRule.EvenOdd, Precision));

    public static double Area(PathsD region) => Math.Abs(Clipper.Area(region));

    /// <summary>
    /// Splits a region into separately editable pieces: each outer ring with its holes. Islands inside a hole are
    /// pieces of their own. Crumbs below <see cref="MinPieceAreaPt2"/> are dropped. Largest piece first.
    /// </summary>
    public static ImmutableArray<ImmutableArray<ImmutableArray<Point2>>> Pieces(PathsD region)
    {
        var clipper = new ClipperD(Precision);
        clipper.AddSubject(region);
        var tree = new PolyTreeD();
        clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
        var pieces = new List<(double Area, ImmutableArray<ImmutableArray<Point2>> Rings)>();
        Collect(tree, pieces);
        return [.. pieces.OrderByDescending(p => p.Area).Select(p => p.Rings)];
    }

    private static void Collect(PolyPathD parent, List<(double, ImmutableArray<ImmutableArray<Point2>>)> pieces)
    {
        for (int i = 0; i < parent.Count; i++)
        {
            var outer = parent[i];
            var rings = ImmutableArray.CreateBuilder<ImmutableArray<Point2>>();
            double area = Math.Abs(Clipper.Area(outer.Polygon!));
            rings.Add(ToRing(outer.Polygon!));
            for (int h = 0; h < outer.Count; h++)
            {
                var hole = outer[h];
                area -= Math.Abs(Clipper.Area(hole.Polygon!));
                rings.Add(ToRing(hole.Polygon!));
                Collect(hole, pieces); // islands inside the hole
            }
            if (area >= MinPieceAreaPt2) pieces.Add((area, rings.ToImmutable()));
        }
    }

    public static PathsD ToPaths(ImmutableArray<ImmutableArray<Point2>> rings)
    {
        var paths = new PathsD(rings.Length);
        foreach (var ring in rings)
        {
            var path = new PathD(ring.Length);
            foreach (var p in ring) path.Add(new(p.X, p.Y));
            paths.Add(path);
        }
        return paths;
    }

    public static ImmutableArray<ImmutableArray<Point2>> ToRings(PathsD paths) =>
        [.. paths.Where(p => p.Count >= 3).Select(ToRing)];

    private static ImmutableArray<Point2> ToRing(PathD path) => [.. path.Select(p => new Point2(p.x, p.y))];

    public static PathsD Transform(PathsD paths, Affine m)
    {
        var result = new PathsD(paths.Count);
        foreach (var path in paths)
        {
            var moved = new PathD(path.Count);
            foreach (var p in path)
            {
                var q = m.Transform(new(p.x, p.y));
                moved.Add(new(q.X, q.Y));
            }
            result.Add(moved);
        }
        return result;
    }

    // ----- outline queries without polygon arithmetic (picking, bounds, eraser contact) -----

    public static Rect2 Bounds(ImmutableArray<ImmutableArray<Point2>> rings)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var ring in rings)
            foreach (var p in ring)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        return new(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Even-odd point containment, matching how outlines are painted.</summary>
    public static bool Contains(ImmutableArray<ImmutableArray<Point2>> rings, Point2 p)
    {
        bool inside = false;
        foreach (var ring in rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                Point2 a = ring[i], b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        return inside;
    }

    /// <summary>Shortest distance from the segment <paramref name="a"/>–<paramref name="b"/> to any ring edge.</summary>
    public static double DistanceToEdges(ImmutableArray<ImmutableArray<Point2>> rings, Point2 a, Point2 b)
    {
        double best = double.PositiveInfinity;
        foreach (var ring in rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                best = Math.Min(best, SegmentDistance(a, b, ring[j], ring[i]));
        return best;
    }

    /// <summary>
    /// True when a circle of <paramref name="radius"/> moved from <paramref name="a"/> to <paramref name="b"/> (local
    /// space) overlaps the painted region. Consecutive input points are joined, so a fast stroke of the eraser
    /// still touches everything between them (spec 7.4).
    /// </summary>
    public static bool Touches(StrokeAnnotation stroke, Point2 a, Point2 b, double radius)
    {
        if (stroke.IsOutlined)
            return Contains(stroke.Outline, a) || DistanceToEdges(stroke.Outline, a, b) < radius;
        var points = stroke.Points;
        double reach = stroke.WidthPt / 2 + radius;
        if (points.Length == 1) return StrokeGeometry.DistanceToSegment(points[0], a, b) < reach;
        for (int i = 1; i < points.Length; i++)
            if (SegmentDistance(a, b, points[i - 1], points[i]) < reach) return true;
        return false;
    }

    public static double SegmentDistance(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        if (SegmentsCross(a, b, c, d)) return 0;
        return Math.Min(Math.Min(StrokeGeometry.DistanceToSegment(a, c, d), StrokeGeometry.DistanceToSegment(b, c, d)),
            Math.Min(StrokeGeometry.DistanceToSegment(c, a, b), StrokeGeometry.DistanceToSegment(d, a, b)));
    }

    private static bool SegmentsCross(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(Point2 a, Point2 b, Point2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
}
