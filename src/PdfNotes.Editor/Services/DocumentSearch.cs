using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Reading;
using PdfNotes.Text;

namespace PdfNotes.Editor.Services;

/// <summary>
/// Selectable PDF glyphs of one open document (spec 9.3, 12), extracted once per source page and placed on the
/// current paper of each model page (D10 content transform; glyphs entirely off the paper are left out). Safe to use
/// from a background search: the engine serializes its calls and the cache is concurrent.
/// </summary>
public sealed class PageGlyphSource(DocumentSources? pdf)
{
    public PageGlyphSource(PdfSourceDocument? pdf) : this(pdf is null ? null : new DocumentSources(pdf, ownsMain: false)) { }

    private readonly ConcurrentDictionary<int, TextGlyph[]> extracted = new();

    public IReadOnlyList<TextGlyph> Glyphs(PageSetup setup)
    {
        var source = setup.Source;
        if (source.Kind != PageSourceKind.Pdf || pdf is null || source.PdfPageIndex is not { } index || index >= pdf.Pages.Count) return [];
        var glyphs = extracted.GetOrAdd(index, i =>
        {
            try { return pdf.Glyphs(i); }
            catch (InvalidDataException) { return []; }
        });
        var placement = setup.Content?.Transform ?? Affine.Identity;
        var paper = new Rect2(0, 0, setup.WidthPt, setup.HeightPt);
        var originalPaper = new Rect2(0, 0, pdf.Pages[index].Box.WidthPt, pdf.Pages[index].Box.HeightPt);
        return glyphs.Where(g => g.IsEmpty || Touches(g.Box, originalPaper))
            .Select(g => g with { Box = g.Box.Transform(placement) }).Where(g => g.IsEmpty || Touches(g.Box, paper)).ToArray();
    }

    private static bool Touches(Quad box, Rect2 paper)
    {
        var ring = TextSelection.Ring(box);
        return ring.Max(p => p.X) >= paper.X && ring.Min(p => p.X) <= paper.X + paper.Width &&
               ring.Max(p => p.Y) >= paper.Y && ring.Min(p => p.Y) <= paper.Y + paper.Height;
    }
}

/// <summary>One search result: PDF text of a page (<see cref="BoxId"/> null) or characters of a text box, with its line quads in canonical points.</summary>
public sealed record SearchHit(Guid PageId, Guid? BoxId, int Start, int End, ImmutableArray<Quad> Quads);

/// <summary>
/// Document search over PDF text and text boxes (spec 12): results in page order, and inside a page in reading order
/// by position (PDF text keeps the engine order, text box results are merged in by their place on the shown page).
/// Characters entirely off the current paper are not searched; partly visible results are clipped when drawn.
/// </summary>
public static class DocumentSearch
{
    public static IReadOnlyList<SearchHit> Run(DocumentModel model, Func<PageSetup, IReadOnlyList<TextGlyph>> glyphs, string query, CancellationToken token)
    {
        var results = new List<SearchHit>();
        if (TextSearch.NormalizeQuery(query).Length == 0) return results;
        foreach (var page in model.Pages)
        {
            token.ThrowIfCancellationRequested();
            results.AddRange(SearchPage(page, glyphs(page.Setup), query, token));
        }
        return results;
    }

    public static IReadOnlyList<SearchHit> SearchPage(Page page, IReadOnlyList<TextGlyph> glyphs, string query, CancellationToken token = default)
    {
        var pdf = TextSearch.Find(glyphs.Select(g => g.Text).ToArray(), query)
            .Select(r => new SearchHit(page.Id, null, r.Start, r.End, TextSelection.LineQuads(glyphs, r.Start, r.End)))
            .Where(h => !h.Quads.IsEmpty).ToList();
        var boxes = new List<SearchHit>();
        var paper = new Rect2(0, 0, page.Setup.WidthPt, page.Setup.HeightPt);
        // Sticky notes are found by their text; the hit marks the icon (spec 24.9).
        foreach (var note in page.Annotations.OfType<StickyNote>())
        {
            var icon = AnnotationGeometry.Corners(new(0, 0, StickyNote.IconPt, StickyNote.IconPt), note.Transform);
            foreach (var r in TextSearch.Find([.. note.Text.EnumerateRunes().Select(r => r.ToString())], query))
                boxes.Add(new SearchHit(page.Id, note.Id, r.Start, r.End, [new Quad(icon[0], icon[1], icon[3], icon[2])]));
        }
        foreach (var box in page.Annotations.OfType<TextBoxAnnotation>())
        {
            token.ThrowIfCancellationRequested();
            if (box.IsBlank) continue;
            // Characters cut off the paper entirely are not found; a gap they leave breaks words like a line end.
            var visible = TextLayout.Measure(box).Characters.Where(c => Visible(Corners(c.Bounds, box.Transform), paper)).ToArray();
            var found = TextSearch.Find(visible.Select(c => c.Text).ToArray(), query, i =>
                visible[i].Line != visible[i - 1].Line || visible[i].Offset != visible[i - 1].Offset + visible[i - 1].Text.Length);
            foreach (var (start, end) in found)
            {
                var quads = visible[start..end].GroupBy(c => c.Line).Select(line =>
                {
                    double x0 = line.Min(c => c.Bounds.X), y0 = line.Min(c => c.Bounds.Y);
                    double x1 = line.Max(c => c.Bounds.X + c.Bounds.Width), y1 = line.Max(c => c.Bounds.Y + c.Bounds.Height);
                    var q = Corners(new Rect2(x0, y0, x1 - x0, y1 - y0), box.Transform);
                    return new Quad(q[0], q[1], q[3], q[2]);
                }).ToImmutableArray();
                boxes.Add(new SearchHit(page.Id, box.Id, visible[start].Offset, visible[end - 1].Offset + visible[end - 1].Text.Length, quads));
            }
        }
        if (boxes.Count == 0) return pdf;
        // Merge the two sequences by position on the shown (rotated) page, keeping each one's own order.
        var display = new PageFrame(page.Setup.WidthPt, page.Setup.HeightPt, page.Setup.Rotation).CanonicalToDisplay;
        (double Top, double Left, double Height) Key(SearchHit h)
        {
            var ring = h.Quads[0] is var q ? new[] { q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight }.Select(display.Transform).ToArray() : [];
            return (ring.Min(p => p.Y), ring.Min(p => p.X), ring.Max(p => p.Y) - ring.Min(p => p.Y));
        }
        bool Before(SearchHit a, SearchHit b)
        {
            var (ta, la, ha) = Key(a); var (tb, lb, hb) = Key(b);
            double sameLine = Math.Min(ha, hb) / 2;
            return Math.Abs(ta - tb) <= sameLine ? la < lb : ta < tb;
        }
        boxes.Sort((a, b) => Before(a, b) ? -1 : Before(b, a) ? 1 : 0);
        var merged = new List<SearchHit>(pdf.Count + boxes.Count);
        int i = 0, j = 0;
        while (i < pdf.Count || j < boxes.Count)
            merged.Add(j >= boxes.Count || i < pdf.Count && !Before(boxes[j], pdf[i]) ? pdf[i++] : boxes[j++]);
        return merged;
    }

    /// <summary>Corners of a local rectangle on the page: upper-left, upper-right, lower-right, lower-left.</summary>
    private static Point2[] Corners(Rect2 r, Affine transform) =>
        [transform.Transform(new(r.X, r.Y)), transform.Transform(new(r.X + r.Width, r.Y)),
         transform.Transform(new(r.X + r.Width, r.Y + r.Height)), transform.Transform(new(r.X, r.Y + r.Height))];

    private static bool Visible(Point2[] corners, Rect2 paper) =>
        corners.Max(p => p.X) >= paper.X && corners.Min(p => p.X) <= paper.X + paper.Width &&
        corners.Max(p => p.Y) >= paper.Y && corners.Min(p => p.Y) <= paper.Y + paper.Height;
}
