using PdfNotes.Core.Geometry;

namespace PdfNotes.Editor.Layout;

/// <summary>Spec 4.3: vertical continuous scrolling (default) or one page at a time.</summary>
public enum ViewMode { Continuous, SinglePage }

/// <summary>A page point under a viewport position: page index and canonical page point (may lie outside the page).</summary>
public readonly record struct PageAnchor(int PageIndex, Point2 PagePoint);

/// <summary>
/// Page placement in DIPs for one zoom/mode/viewport width. UI-free so it can be tested directly.
/// Extent coordinates: origin at the top-left of the scrollable area. Viewport = extent − scroll offset.
/// In single-page mode only <see cref="SinglePageIndex"/> is laid out.
/// </summary>
public sealed class DocumentLayout
{
    public const double MarginDip = 24;
    public const double PageGapDip = 16;

    private readonly Rect2[] rects;

    public DocumentLayout(IReadOnlyList<PageFrame> frames, double zoom, ViewMode mode, int singlePageIndex,
        double viewportWidth, double viewportHeight)
    {
        if (frames.Count == 0) throw new ArgumentException("A document needs at least one page.", nameof(frames));
        if (!double.IsFinite(zoom) || zoom <= 0) throw new ArgumentOutOfRangeException(nameof(zoom));
        Frames = frames;
        Zoom = zoom;
        Mode = mode;
        SinglePageIndex = Math.Clamp(singlePageIndex, 0, frames.Count - 1);
        double scale = DipsPerPoint;
        rects = new Rect2[frames.Count];
        viewportWidth = Math.Max(0, double.IsFinite(viewportWidth) ? viewportWidth : 0);
        viewportHeight = Math.Max(0, double.IsFinite(viewportHeight) ? viewportHeight : 0);
        if (mode == ViewMode.SinglePage)
        {
            var f = frames[SinglePageIndex];
            double w = f.DisplayWidthPt * scale, h = f.DisplayHeightPt * scale;
            ExtentWidth = Math.Max(viewportWidth, w + 2 * MarginDip);
            ExtentHeight = Math.Max(viewportHeight, h + 2 * MarginDip);
            rects[SinglePageIndex] = new((ExtentWidth - w) / 2, (ExtentHeight - h) / 2, w, h);
            return;
        }
        double maxWidth = frames.Max(f => f.DisplayWidthPt) * scale;
        ExtentWidth = Math.Max(viewportWidth, maxWidth + 2 * MarginDip);
        double y = MarginDip;
        for (int i = 0; i < frames.Count; i++)
        {
            double w = frames[i].DisplayWidthPt * scale, h = frames[i].DisplayHeightPt * scale;
            rects[i] = new((ExtentWidth - w) / 2, y, w, h);
            y += h + PageGapDip;
        }
        ExtentHeight = Math.Max(viewportHeight, y - PageGapDip + MarginDip);
    }

    public IReadOnlyList<PageFrame> Frames { get; }
    public double Zoom { get; }
    public ViewMode Mode { get; }
    public int SinglePageIndex { get; }
    public double ExtentWidth { get; }
    public double ExtentHeight { get; }
    public int PageCount => rects.Length;
    public double DipsPerPoint => Zoom * Units.DipsPerPoint;

    public bool IsLaidOut(int index) => Mode == ViewMode.Continuous || index == SinglePageIndex;

    /// <summary>Page rectangle in extent DIPs (displayed, i.e. after page rotation).</summary>
    public Rect2 PageRect(int index) => IsLaidOut(index) ? rects[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Coordinate service for one page at a given scroll offset (viewport DIPs).</summary>
    public ViewTransform Transform(int index, Point2 scrollOffset, double dpiScale)
    {
        var r = PageRect(index);
        return new(Frames[index], Zoom, dpiScale, new(r.X - scrollOffset.X, r.Y - scrollOffset.Y));
    }

    /// <summary>Indices of laid-out pages intersecting the viewport rectangle (extent DIPs), in order.</summary>
    public IReadOnlyList<int> VisiblePages(Rect2 viewport)
    {
        if (Mode == ViewMode.SinglePage)
            return Intersects(rects[SinglePageIndex], viewport) ? [SinglePageIndex] : [];
        // Pages are stacked by Y: binary search the first page whose bottom is below the viewport top.
        int lo = 0, hi = rects.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (rects[mid].Y + rects[mid].Height < viewport.Y) lo = mid + 1; else hi = mid;
        }
        var result = new List<int>();
        for (int i = lo; i < rects.Length && rects[i].Y <= viewport.Y + viewport.Height; i++)
            if (Intersects(rects[i], viewport)) result.Add(i);
        return result;
    }

    /// <summary>The page shown as "current": the one covering most of the viewport height, else the nearest one.</summary>
    public int CurrentPage(Rect2 viewport)
    {
        if (Mode == ViewMode.SinglePage) return SinglePageIndex;
        int best = -1;
        double bestHeight = 0;
        foreach (int i in VisiblePages(viewport))
        {
            double h = Math.Min(rects[i].Y + rects[i].Height, viewport.Y + viewport.Height) - Math.Max(rects[i].Y, viewport.Y);
            if (h > bestHeight + 1e-9) (best, bestHeight) = (i, h);
        }
        return best >= 0 ? best : NearestPage(viewport.Y + viewport.Height / 2);
    }

    /// <summary>Laid-out page whose vertical span is closest to an extent Y.</summary>
    public int NearestPage(double extentY)
    {
        if (Mode == ViewMode.SinglePage) return SinglePageIndex;
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < rects.Length; i++)
        {
            double d = extentY < rects[i].Y ? rects[i].Y - extentY : Math.Max(0, extentY - (rects[i].Y + rects[i].Height));
            if (d < bestDistance) (best, bestDistance) = (i, d);
        }
        return best;
    }

    /// <summary>
    /// The page point under a viewport position. Outside every page, the nearest page is used and the point lies
    /// outside its bounds, which still anchors zooming correctly.
    /// </summary>
    public PageAnchor Anchor(Point2 viewportPoint, Point2 scrollOffset)
    {
        var extent = new Point2(viewportPoint.X + scrollOffset.X, viewportPoint.Y + scrollOffset.Y);
        int index = NearestPage(extent.Y);
        foreach (int i in VisiblePages(new(extent.X, extent.Y, 0, 0)))
            if (Contains(rects[i], extent)) { index = i; break; }
        return new(index, Transform(index, scrollOffset, 1).DipToPage.Transform(viewportPoint));
    }

    /// <summary>Hit test: the page and canonical point under a viewport position, or null outside all pages.</summary>
    public PageAnchor? HitTest(Point2 viewportPoint, Point2 scrollOffset)
    {
        var extent = new Point2(viewportPoint.X + scrollOffset.X, viewportPoint.Y + scrollOffset.Y);
        foreach (int i in VisiblePages(new(extent.X, extent.Y, 0, 0)))
            if (Contains(rects[i], extent)) return new(i, Transform(i, scrollOffset, 1).DipToPage.Transform(viewportPoint));
        return null;
    }

    /// <summary>Scroll offset that puts <paramref name="anchor"/> under <paramref name="viewportPoint"/>, before clamping.</summary>
    public Point2 OffsetForAnchor(PageAnchor anchor, Point2 viewportPoint)
    {
        int index = IsLaidOut(anchor.PageIndex) ? anchor.PageIndex : SinglePageIndex;
        var extent = Transform(index, new(0, 0), 1).PageToDip.Transform(anchor.PagePoint);
        return new(extent.X - viewportPoint.X, extent.Y - viewportPoint.Y);
    }

    public Point2 ClampOffset(Point2 offset, double viewportWidth, double viewportHeight) => new(
        Math.Clamp(offset.X, 0, Math.Max(0, ExtentWidth - viewportWidth)),
        Math.Clamp(offset.Y, 0, Math.Max(0, ExtentHeight - viewportHeight)));

    /// <summary>Zoom at which the page's displayed width fills the viewport width minus margins.</summary>
    public static double FitWidthZoom(PageFrame frame, double viewportWidth) =>
        ZoomLimits.Clamp((viewportWidth - 2 * MarginDip) / (frame.DisplayWidthPt * Units.DipsPerPoint));

    /// <summary>Zoom at which the whole displayed page fits the viewport.</summary>
    public static double FitPageZoom(PageFrame frame, double viewportWidth, double viewportHeight) => ZoomLimits.Clamp(Math.Min(
        (viewportWidth - 2 * MarginDip) / (frame.DisplayWidthPt * Units.DipsPerPoint),
        (viewportHeight - 2 * MarginDip) / (frame.DisplayHeightPt * Units.DipsPerPoint)));

    private static bool Intersects(Rect2 a, Rect2 b) =>
        a.X <= b.X + b.Width && b.X <= a.X + a.Width && a.Y <= b.Y + b.Height && b.Y <= a.Y + a.Height;

    private static bool Contains(Rect2 r, Point2 p) => p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height;
}
