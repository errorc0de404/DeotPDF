using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// The PDF text selection of <see cref="SelectTool"/> (spec 5.3, 17): one page, an anchor and a focus caret, and the word a
/// drag began with (a double click or a long press keeps that whole word while the drag goes on).
/// </summary>
internal sealed class TextSelectionDrag(ICanvasView view, Func<int, IReadOnlyList<TextGlyph>>? glyphs)
{
    private int anchor, focus;
    private (int Start, int End)? word;

    /// <summary>The page of the selection or of the drag, −1 when there is none.</summary>
    public int Page { get; private set; } = -1;

    public bool HasSelection => Page >= 0 && anchor != focus;
    public (int Start, int End) Range => (Math.Min(anchor, focus), Math.Max(anchor, focus));
    public string Text => HasSelection && glyphs is not null ? TextSelection.TextOf(glyphs(Page), Range.Start, Range.End) : "";

    public IReadOnlyList<Quad> Quads(int pageIndex) =>
        HasSelection && pageIndex == Page && glyphs is not null ? TextSelection.LineQuads(glyphs(Page), Range.Start, Range.End) : [];

    public void Set(int pageIndex, int anchorCaret, int focusCaret) => (Page, anchor, focus, word) = (pageIndex, anchorCaret, focusCaret, null);

    /// <summary>Forgets the selection; true when there was one to forget.</summary>
    public bool Clear()
    {
        if (Page < 0) return false;
        Page = -1;
        return true;
    }

    /// <summary>Caret under a viewport point when PDF text is within <paramref name="toleranceDip"/>, with its page.</summary>
    public (int Page, int Caret)? CaretAt(Point2 viewportPoint, double toleranceDip)
    {
        if (glyphs is null || view.HitTestPage(viewportPoint) is not { } hit) return null;
        double tolerance = view.PageTransform(hit.PageIndex).DipLengthToPoints(toleranceDip);
        return TextSelection.CaretAt(glyphs(hit.PageIndex), hit.PagePoint, tolerance) is { } caret ? (hit.PageIndex, caret) : null;
    }

    /// <summary>A drag starts at <paramref name="caret"/>; with <paramref name="wholeWord"/> it starts with the word at <paramref name="at"/>.</summary>
    public void Begin((int Page, int Caret) caret, Point2 at, bool wholeWord, double toleranceDip)
    {
        Set(caret.Page, caret.Caret, caret.Caret);
        if (wholeWord && glyphs is not null && view.HitTestPage(at) is { } on &&
            TextSelection.WordAt(glyphs(caret.Page), on.PagePoint, view.PageTransform(caret.Page).DipLengthToPoints(toleranceDip)) is { } found)
        {
            word = found;
            (anchor, focus) = found;
            view.RefreshOverlays();
        }
    }

    /// <summary>The drag reached <paramref name="viewportPoint"/>; true when the selection changed.</summary>
    public bool Extend(Point2 viewportPoint)
    {
        var at = view.PageTransform(Page).DipToPage.Transform(viewportPoint);
        if (glyphs is null || TextSelection.CaretAt(glyphs(Page), at) is not { } caret) return false;
        var (a, f) = word is not { } w ? (anchor, caret)
            : caret >= w.End ? (w.Start, caret) : caret <= w.Start ? (w.End, caret) : (w.Start, w.End);
        if (a == anchor && f == focus) return false;
        (anchor, focus) = (a, f);
        return true;
    }
}
