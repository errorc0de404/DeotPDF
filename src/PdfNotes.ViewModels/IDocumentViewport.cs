using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Layout;

namespace PdfNotes.ViewModels;

/// <summary>
/// The document canvas as the view models use it (P1 V3–V4): which page is in view, whether marks are on screen,
/// scrolling, zoom and the view mode, and redrawing the overlays. Points are canonical page points. All of it is view
/// state: never a document change.
/// </summary>
public interface IDocumentViewport
{
    /// <summary>The page in view (the one at the viewport's center line).</summary>
    int CurrentPageIndex { get; }

    int PageCount { get; }

    /// <summary>1 = 100 %.</summary>
    double Zoom { get; }

    ViewMode Mode { get; }

    /// <summary>The page and its point at the viewport's center, or null without pages.</summary>
    PageAnchor? CenterAnchor { get; }

    /// <summary>A stroke or another press on the canvas is in progress (the page must stay).</summary>
    bool IsToolActive { get; }

    /// <summary>All of <paramref name="quads"/> on page <paramref name="pageIndex"/> is inside the viewport.</summary>
    bool IsInView(int pageIndex, IEnumerable<Quad> quads);

    /// <summary>Some part of <paramref name="quads"/> on page <paramref name="pageIndex"/> is inside the viewport.</summary>
    bool IsPartlyInView(int pageIndex, IEnumerable<Quad> quads);

    /// <summary>Scrolls so that <paramref name="point"/> of page <paramref name="pageIndex"/> is at the viewport's center (zoom kept).</summary>
    void ScrollToPagePoint(int pageIndex, Point2 point);

    /// <summary>Shows <paramref name="point"/> of a page near the top of the viewport (link and outline targets).</summary>
    void ShowPagePoint(int pageIndex, Point2 point);

    /// <summary>Goes to the top of a page (clamped to the document).</summary>
    void GoToPage(int pageIndex);

    /// <summary>Zoom around the viewport's center.</summary>
    void SetZoom(double zoom);

    void FitWidth();
    void FitPage();
    void SetMode(ViewMode mode);

    /// <summary>↑/↓: one scroll step.</summary>
    void LineUp();
    void LineDown();

    /// <summary>←/→: scroll sideways, turning the page at the edge.</summary>
    bool ArrowSideways(int direction, bool repeat);

    /// <summary>Redraws the temporary marks drawn over the pages (search results, selection).</summary>
    void RefreshOverlays();
}
