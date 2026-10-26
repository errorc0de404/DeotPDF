using System.Collections.Immutable;
using Clipper2Lib;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Geometry;

/// <summary>
/// The one geometry of shapes (D11): outline, painted bounds, picking and lasso region. The screen painter and the
/// PDF appearance draw the same outline — straight segments for polygonal kinds, four cubic arcs for the ellipse.
/// Fills use the even-odd rule, so a self-crossing free polygon looks the same everywhere.
/// </summary>
public static class ShapeGeometry
{
    /// <summary>Cubic arc constant of a quarter ellipse.</summary>
    public const double Kappa = 0.5522847498307936;

    /// <summary>Vertices of a polygonal shape in local space (null for the ellipse, which is curved).</summary>
    public static Point2[]? Vertices(ShapeAnnotation shape)
    {
        var b = shape.Box;
        return shape.ShapeKind switch
        {
            ShapeKind.Rectangle => [new(b.X, b.Y), new(b.X + b.Width, b.Y), new(b.X + b.Width, b.Y + b.Height), new(b.X, b.Y + b.Height)],
            ShapeKind.Triangle => [new(b.X + b.Width / 2, b.Y), new(b.X + b.Width, b.Y + b.Height), new(b.X, b.Y + b.Height)],
            ShapeKind.Polygon or ShapeKind.Polyline => [.. shape.Points],
            _ => null
        };
    }

    /// <summary>The ellipse as four cubic Béziers: start point, then 4 × (control, control, end).</summary>
    public static Point2[] EllipseCubics(Rect2 b)
    {
        double rx = b.Width / 2, ry = b.Height / 2, cx = b.X + rx, cy = b.Y + ry, kx = Kappa * rx, ky = Kappa * ry;
        return
        [
            new(cx + rx, cy),
            new(cx + rx, cy + ky), new(cx + kx, cy + ry), new(cx, cy + ry),
            new(cx - kx, cy + ry), new(cx - rx, cy + ky), new(cx - rx, cy),
            new(cx - rx, cy - ky), new(cx - kx, cy - ry), new(cx, cy - ry),
            new(cx + kx, cy - ry), new(cx + rx, cy - ky), new(cx + rx, cy),
        ];
    }

    /// <summary>The outline as a polyline in local space (the ellipse flattened within <paramref name="tolerance"/> points).</summary>
    public static Point2[] Outline(ShapeAnnotation shape, double tolerance = 0.05)
    {
        if (Vertices(shape) is { } vertices) return vertices;
        var b = shape.Box;
        double r = Math.Max(b.Width, b.Height) / 2;
        int n = (int)Math.Clamp(Math.Ceiling(Math.PI / Math.Acos(Math.Clamp(1 - tolerance / Math.Max(r, tolerance), -1, 1))), 16, 720);
        return Enumerable.Range(0, n).Select(i => new Point2(b.X + b.Width / 2 * (1 + Math.Cos(2 * Math.PI * i / n)), b.Y + b.Height / 2 * (1 + Math.Sin(2 * Math.PI * i / n)))).ToArray();
    }

    public static bool HasFill(ShapeAnnotation shape) => shape.IsClosed && shape.Style.FillOpacity > 0;

    /// <summary>Painted bounds in local space: the outline plus half the border width.</summary>
    public static Rect2 Bounds(ShapeAnnotation shape)
    {
        var points = shape.UsesBox ? [new(shape.Box.X, shape.Box.Y), new(shape.Box.X + shape.Box.Width, shape.Box.Y + shape.Box.Height)] : Outline(shape);
        double pad = shape.Style.BorderEnabled ? shape.Style.BorderWidthPt / 2 : 0;
        double x0 = points.Min(p => p.X), y0 = points.Min(p => p.Y), x1 = points.Max(p => p.X), y1 = points.Max(p => p.Y);
        return new(x0 - pad, y0 - pad, x1 - x0 + 2 * pad, y1 - y0 + 2 * pad);
    }

    /// <summary>A filled inside, or the border band (with <paramref name="tolerance"/>) of any shape.</summary>
    public static bool Hit(ShapeAnnotation shape, Point2 local, double tolerance)
    {
        var outline = Outline(shape);
        if (HasFill(shape) && Contains(outline, local)) return true;
        double reach = (shape.Style.BorderEnabled ? shape.Style.BorderWidthPt / 2 : 0) + tolerance;
        var path = shape.IsClosed ? [.. outline, outline[0]] : outline;
        return StrokeGeometry.DistanceToPolyline([.. path], local) <= reach;
    }

    /// <summary>Even-odd point in polygon.</summary>
    public static bool Contains(IReadOnlyList<Point2> polygon, Point2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Painted area in local space for the lasso: the even-odd fill and the border band (dash gaps included).</summary>
    public static PathsD Region(ShapeAnnotation shape)
    {
        var outline = Outline(shape, ShapeTolerance);
        var path = new PathD(outline.Length);
        foreach (var p in outline) path.Add(new(p.X, p.Y));
        var parts = new PathsD();
        if (HasFill(shape)) parts.AddRange(Clipper.Union([path], [], FillRule.EvenOdd, StrokeRegion.Precision));
        if (shape.Style.BorderEnabled)
            parts.AddRange(Clipper.InflatePaths([path], shape.Style.BorderWidthPt / 2, JoinType.Round, shape.IsClosed ? EndType.Joined : EndType.Round, 2,
                StrokeRegion.Precision, StrokeRegion.ArcTolerancePt));
        return Clipper.Union(parts, [], FillRule.NonZero, StrokeRegion.Precision);
    }

    private const double ShapeTolerance = 0.05;

    /// <summary>A positive uniform scale about the local origin, baked into the box, points and border width.</summary>
    public static ShapeAnnotation Scaled(ShapeAnnotation shape, double scale) => shape with
    {
        Box = shape.UsesBox ? new(shape.Box.X * scale, shape.Box.Y * scale, shape.Box.Width * scale, shape.Box.Height * scale) : default,
        Points = shape.Points.Select(p => new Point2(p.X * scale, p.Y * scale)).ToImmutableArray(),
        Style = shape.Style with { BorderWidthPt = shape.Style.BorderWidthPt * scale }
    };

    /// <summary>
    /// A box shape between two local corners; <paramref name="square"/> makes width and height equal (Shift while
    /// dragging), growing from <paramref name="from"/> toward <paramref name="to"/>.
    /// </summary>
    public static Rect2 BoxBetween(Point2 from, Point2 to, bool square)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        if (square)
        {
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = Math.Sign(dx == 0 ? 1 : dx) * side; dy = Math.Sign(dy == 0 ? 1 : dy) * side;
        }
        return new(Math.Min(from.X, from.X + dx), Math.Min(from.Y, from.Y + dy), Math.Abs(dx), Math.Abs(dy));
    }
}
