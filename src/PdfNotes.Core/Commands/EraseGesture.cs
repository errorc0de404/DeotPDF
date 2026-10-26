using System.Collections.Immutable;
using Clipper2Lib;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

public enum EraserMode { Stroke, Area }

/// <summary>
/// One eraser gesture on one page, press to release (spec 7.3–7.4). Each new pointer position adds the segment from
/// the previous one, so a fast drag erases everything in between. Erased are unlocked pen and free highlighter strokes
/// and what the highlighter puts on text (spec 7.3, D14g): glyph highlights, underlines and strikeouts, and text box
/// highlight ranges. Lines, shapes, text boxes themselves, images, notes and the PDF background are never touched. The
/// document is not changed until <see cref="Commit"/>, which returns the whole gesture as one Undo unit.
/// <list type="bullet">
/// <item>Stroke mode removes every stroke whose painted area the eraser circle touches.</item>
/// <item>Area mode subtracts the swept circles from the painted area. What remains of a stroke becomes outlined
/// pieces (one annotation per connected piece) at the original stacking position; nothing is painted over.</item>
/// <item>Marks on text are erased whole in both modes (spec 7.4): a touched glyph mark goes, a touched text box
/// highlight range leaves the box (its text stays).</item>
/// </list>
/// </summary>
public sealed class EraseGesture
{
    private readonly Page page;
    private readonly List<(StrokeAnnotation Stroke, Affine ToLocal, Rect2 PageBounds)> candidates = [];
    private readonly HashSet<Guid> removed = [];
    private readonly Dictionary<Guid, PathsD> regions = [];
    private readonly List<(Annotation Mark, PathsD Region, Rect2 PageBounds)> marks = [];
    private readonly List<(TextBoxAnnotation Box, List<(TextHighlightRange Range, PathsD Region)> Ranges)> boxes = [];
    private readonly Dictionary<Guid, HashSet<TextHighlightRange>> erasedRanges = [];
    private readonly Dictionary<Guid, IReadOnlyList<Annotation>> preview = [];
    private Point2? last;

    /// <param name="boxHighlights">Laid-out highlight rectangles of a text box (local points); without it text box highlights are not erased.</param>
    public EraseGesture(DocumentModel model, Guid pageId, EraserMode mode, double diameterPt,
        Func<TextBoxAnnotation, IEnumerable<(TextHighlightRange Highlight, Rect2[] Lines)>>? boxHighlights = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!double.IsFinite(diameterPt) || diameterPt <= 0) throw new ArgumentOutOfRangeException(nameof(diameterPt));
        page = model.GetPage(pageId);
        Mode = mode;
        Radius = diameterPt / 2;
        foreach (var stroke in page.Annotations.OfType<StrokeAnnotation>().Where(s => !s.Locked))
            candidates.Add((stroke, stroke.Transform.Invert(), AnnotationGeometry.Bounds([stroke])));
        foreach (var mark in page.Annotations.Where(a => a is PdfTextHighlight or PdfTextMarkup && !a.Locked))
            if (Lasso.PaintedRegion(mark) is { Count: > 0 } region) marks.Add((mark, region, RegionBounds(region)));
        if (boxHighlights is null) return;
        foreach (var box in page.Annotations.OfType<TextBoxAnnotation>().Where(b => !b.Locked && !b.Highlights.IsEmpty))
        {
            var ranges = boxHighlights(box).Select(h => (h.Highlight, StrokeRegion.Transform(
                [.. h.Lines.Select(r => new PathD { new(r.X, r.Y), new(r.X + r.Width, r.Y), new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height) })],
                box.Transform))).ToList();
            if (ranges.Count > 0) boxes.Add((box, ranges));
        }
    }

    private static Rect2 RegionBounds(PathsD region)
    {
        var points = region.SelectMany(r => r).ToArray();
        double x0 = points.Min(p => p.x), y0 = points.Min(p => p.y);
        return new(x0, y0, points.Max(p => p.x) - x0, points.Max(p => p.y) - y0);
    }

    public EraserMode Mode { get; }
    public double Radius { get; }
    public Guid PageId => page.Id;

    /// <summary>
    /// How the touched annotations look now: an empty list hides one, otherwise the list replaces it (area mode shows
    /// the remaining region of a stroke as one outlined preview stroke; a text box shows without its erased ranges).
    /// Untouched annotations are absent.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<Annotation>> Preview => preview;

    public bool HasChanges => preview.Count > 0;

    /// <summary>Extends the eraser path to <paramref name="pagePoint"/> (canonical page points). Returns true when the preview changed.</summary>
    public bool AddPoint(Point2 pagePoint)
    {
        if (!pagePoint.IsFinite) return false;
        var a = last ?? pagePoint;
        last = pagePoint;
        double minX = Math.Min(a.X, pagePoint.X) - Radius, maxX = Math.Max(a.X, pagePoint.X) + Radius;
        double minY = Math.Min(a.Y, pagePoint.Y) - Radius, maxY = Math.Max(a.Y, pagePoint.Y) + Radius;
        bool changed = false;
        foreach (var (stroke, toLocal, b) in candidates)
        {
            if (removed.Contains(stroke.Id) || b.X > maxX || b.Y > maxY || b.X + b.Width < minX || b.Y + b.Height < minY) continue;
            Point2 la = toLocal.Transform(a), lb = toLocal.Transform(pagePoint);
            if (Mode == EraserMode.Stroke)
            {
                if (!StrokeRegion.Touches(stroke, la, lb, Radius)) continue;
                removed.Add(stroke.Id);
                preview[stroke.Id] = [];
                changed = true;
                continue;
            }
            if (!regions.TryGetValue(stroke.Id, out var region))
            {
                if (!StrokeRegion.Touches(stroke, la, lb, Radius)) continue;
                region = StrokeRegion.Region(stroke);
            }
            double before = StrokeRegion.Area(region);
            var rest = StrokeRegion.Subtract(region, StrokeRegion.Capsule(la, lb, Radius));
            regions[stroke.Id] = rest;
            if (StrokeRegion.Area(rest) >= before) continue;
            var rings = StrokeRegion.ToRings(rest);
            if (rings.IsEmpty || StrokeRegion.Area(rest) < StrokeRegion.MinPieceAreaPt2)
            {
                removed.Add(stroke.Id);
                preview[stroke.Id] = [];
            }
            else preview[stroke.Id] = [stroke with { Points = [], Outline = rings }];
            changed = true;
        }
        if (marks.Count == 0 && boxes.Count == 0) return changed;
        var capsule = StrokeRegion.Capsule(a, pagePoint, Radius);
        foreach (var (mark, region, b) in marks)
        {
            if (removed.Contains(mark.Id) || b.X > maxX || b.Y > maxY || b.X + b.Width < minX || b.Y + b.Height < minY) continue;
            if (StrokeRegion.AreaInside(region, capsule) <= Lasso.MinOverlapPt2) continue;
            removed.Add(mark.Id);
            preview[mark.Id] = [];
            changed = true;
        }
        foreach (var (box, ranges) in boxes)
        {
            var erased = erasedRanges.GetValueOrDefault(box.Id);
            bool touched = false;
            foreach (var (range, region) in ranges)
            {
                if (erased?.Contains(range) == true || StrokeRegion.AreaInside(region, capsule) <= Lasso.MinOverlapPt2) continue;
                (erased ??= erasedRanges[box.Id] = []).Add(range);
                touched = true;
            }
            if (!touched) continue;
            preview[box.Id] = [Without(box, erased!)];
            changed = true;
        }
        return changed;
    }

    private static TextBoxAnnotation Without(TextBoxAnnotation box, HashSet<TextHighlightRange> erased) =>
        box with { Highlights = [.. box.Highlights.Where(h => !erased.Contains(h))] };

    /// <summary>
    /// The gesture as one command (empty when nothing was erased). A stroke split by the area eraser keeps its id on
    /// its largest piece; the other pieces get new ids directly above it, so the stacking order is unchanged.
    /// </summary>
    public EditCommand Commit()
    {
        string label = Mode == EraserMode.Stroke ? "undo.strokeEraser" : "undo.areaEraser";
        var operations = new List<EditOperation>();
        // Highest index first: inserting pieces never shifts an index that a later operation still uses.
        for (int index = page.Annotations.Count - 1; index >= 0; index--)
        {
            var annotation = page.Annotations[index];
            if (!preview.ContainsKey(annotation.Id)) continue;
            if (annotation is TextBoxAnnotation box)
            {
                operations.Add(new ReplaceAnnotationOperation(page.Id, box, Without(box, erasedRanges[box.Id])));
                continue;
            }
            if (annotation is not StrokeAnnotation stroke)
            {
                operations.Add(new RemoveAnnotationOperation(page.Id, index, annotation)); // a glyph mark goes whole
                continue;
            }
            var pieces = removed.Contains(stroke.Id) ? [] : StrokeRegion.Pieces(regions[stroke.Id]);
            if (pieces.IsEmpty)
            {
                operations.Add(new RemoveAnnotationOperation(page.Id, index, stroke));
                continue;
            }
            operations.Add(new ReplaceAnnotationOperation(page.Id, stroke, Piece(stroke, stroke.Id, pieces[0])));
            for (int i = 1; i < pieces.Length; i++)
                operations.Add(new InsertAnnotationOperation(page.Id, index + i, Piece(stroke, Guid.NewGuid(), pieces[i])));
        }
        return new(label, operations);
    }

    private static StrokeAnnotation Piece(StrokeAnnotation stroke, Guid id, ImmutableArray<ImmutableArray<Point2>> rings) =>
        stroke with { Id = id, Points = [], Outline = rings };
}
