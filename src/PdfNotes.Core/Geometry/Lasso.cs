using Clipper2Lib;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Geometry;

/// <summary>
/// Lasso selection (spec 5.4–5.5). The drawn path is closed from its last point back to the first and its inside is
/// the even-odd fill of that polygon, so a self-crossing lasso selects what its loops enclose. Like common note apps,
/// an annotation is selected when any part of its painted area lies inside (2026-10-12 user decision, replacing
/// complete containment): strokes and lines with their width, caps and arrow heads, text boxes and images by their
/// transformed rectangle. Locked annotations and the PDF itself are never selected.
/// </summary>
public static class Lasso
{
    /// <summary>A drag shorter than this on screen is a click, not a lasso.</summary>
    public const double MinDragDip = 4;

    /// <summary>Painted area (pt²) that must lie inside; smaller overlaps are grid rounding, not a real touch.</summary>
    public const double MinOverlapPt2 = 0.01;

    /// <summary>Unlocked annotations of <paramref name="page"/> with painted area inside the closed path, in stacking order.</summary>
    public static IReadOnlyList<Guid> Select(Page page, IReadOnlyList<Point2> path, Func<TextBoxAnnotation, Rect2> textBounds)
    {
        if (path.Count < 3) return [];
        var polygon = new PathD(path.Count);
        foreach (var p in path) polygon.Add(new(p.X, p.Y));
        PathsD area = [polygon];
        double minX = path.Min(p => p.X), minY = path.Min(p => p.Y), maxX = path.Max(p => p.X), maxY = path.Max(p => p.Y);
        var selected = new List<Guid>();
        foreach (var annotation in page.Annotations)
        {
            if (annotation.Locked || PaintedRegion(annotation, textBounds) is not { } region) continue;
            var points = region.SelectMany(r => r).ToArray();
            if (points.Length == 0 || points.Max(p => p.x) < minX || points.Min(p => p.x) > maxX ||
                points.Max(p => p.y) < minY || points.Min(p => p.y) > maxY) continue; // cannot overlap
            if (StrokeRegion.AreaInside(region, area) > MinOverlapPt2) selected.Add(annotation.Id);
        }
        return selected;
    }

    /// <summary>
    /// Painted area in canonical page points as non-overlapping polygons, or null for kinds the lasso does not take
    /// yet. Dash gaps count as painted: a dashed line is selected by its whole extent.
    /// </summary>
    public static PathsD? PaintedRegion(Annotation annotation, Func<TextBoxAnnotation, Rect2>? textBounds = null)
    {
        PathsD local;
        switch (annotation)
        {
            case StrokeAnnotation stroke:
                local = StrokeRegion.Region(stroke);
                break;
            case LineAnnotation line:
                local = LineRegion(line);
                break;
            case ShapeAnnotation shape:
                local = ShapeGeometry.Region(shape);
                break;
            case PdfTextHighlight highlight:
                var quads = new PathsD();
                foreach (var q in TextSelection.PaintedQuads(highlight)) quads.Add(new PathD(TextSelection.Ring(q).Select(p => new PointD(p.X, p.Y))));
                local = Clipper.Union(quads, [], FillRule.NonZero, StrokeRegion.Precision);
                break;
            case PdfTextMarkup markup:
                var bands = new PathsD();
                foreach (var q in TextSelection.PaintedQuads(markup)) bands.Add(new PathD(TextSelection.Ring(q).Select(p => new PointD(p.X, p.Y))));
                local = Clipper.Union(bands, [], FillRule.NonZero, StrokeRegion.Precision);
                break;
            case ImageAnnotation or TextBoxAnnotation or StickyNote:
                var r = AnnotationGeometry.LocalBounds(annotation, textBounds);
                local = [new PathD { new(r.X, r.Y), new(r.X + r.Width, r.Y), new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height) }];
                break;
            default:
                return null;
        }
        return StrokeRegion.Transform(local, annotation.Transform);
    }

    private static PathsD LineRegion(LineAnnotation line)
    {
        var parts = new PathsD();
        if (LineGeometry.Body(line) is { } body)
        {
            var points = LineGeometry.Flatten(body, LineGeometry.ShapeTolerancePt);
            var path = new PathD(points.Length);
            foreach (var p in points) path.Add(new(p.X, p.Y));
            parts.AddRange(points.All(p => p == points[0])
                ? [StrokeRegion.Circle(points[0], line.WidthPt / 2)]
                : Clipper.InflatePaths([path], line.WidthPt / 2, JoinType.Round, EndType.Round, 2, StrokeRegion.Precision, StrokeRegion.ArcTolerancePt));
        }
        foreach (var head in LineGeometry.ArrowHeads(line))
            parts.Add(new PathD { new(head[0].X, head[0].Y), new(head[1].X, head[1].Y), new(head[2].X, head[2].Y) });
        return Clipper.Union(parts, [], FillRule.NonZero, StrokeRegion.Precision);
    }
}
