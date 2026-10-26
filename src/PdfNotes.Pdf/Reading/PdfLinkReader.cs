using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Pdf.Engine;

namespace PdfNotes.Pdf.Reading;

/// <summary>
/// A link of a source page (spec 24.3) in that page's unplaced canonical points: an internal target (source page index in
/// <see cref="DocumentSources"/> numbering, with the destination point when the PDF gives one) or a URI.
/// </summary>
public sealed record PdfLink(Quad Area, int? TargetPage, Point2? TargetPoint, string? Uri);

/// <summary>An outline item of the opened PDF (spec 24.4) with an internal target like <see cref="PdfLink"/>.</summary>
public sealed record PdfOutlineItem(string Title, int? TargetPage, Point2? TargetPoint, ImmutableArray<PdfOutlineItem> Children);

/// <summary>Reads links and the outline through PDFium and converts them to canonical points (D02).</summary>
public static class PdfLinkReader
{
    /// <param name="local">The page in <paramref name="engine"/>.</param>
    /// <param name="first">Source index of the engine's first page (imported PDFs continue the numbering, 11.9).</param>
    public static PdfLink[] Links(PdfiumDocument engine, IReadOnlyList<PdfPageGeometry> pages, int local, int first)
    {
        var toCanonical = pages[local].Box.PdfUserToCanonical;
        return engine.Links(local).Select(l => new PdfLink(
            new Quad(new(l.Left, l.Top), new(l.Right, l.Top), new(l.Left, l.Bottom), new(l.Right, l.Bottom)).Transform(toCanonical),
            l.TargetPage is { } t && t < pages.Count ? first + t : null, Point(pages, l.TargetPage, l.TargetX, l.TargetY), l.TargetPage is null ? l.Uri : null)).ToArray();
    }

    public static PdfOutlineItem[] Outline(PdfiumDocument engine, IReadOnlyList<PdfPageGeometry> pages) =>
        engine.Bookmarks().Select(b => Item(b, pages)).ToArray();

    private static PdfOutlineItem Item(PdfiumBookmark b, IReadOnlyList<PdfPageGeometry> pages) =>
        new(b.Title, b.TargetPage is { } t && t < pages.Count ? t : null, Point(pages, b.TargetPage, b.TargetX, b.TargetY),
            b.Children.Select(c => Item(c, pages)).ToImmutableArray());

    /// <summary>The destination point in the target page's canonical points; a missing coordinate is that page's left or top edge.</summary>
    private static Point2? Point(IReadOnlyList<PdfPageGeometry> pages, int? page, double? x, double? y)
    {
        if (page is not { } p || p < 0 || p >= pages.Count || x is null && y is null) return null;
        var box = pages[p].Box;
        var point = box.PdfUserToCanonical.Transform(new Point2(x ?? box.Left, y ?? box.Top));
        return point.IsFinite ? point : null;
    }
}
