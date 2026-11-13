using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Layout;
using PdfNotes.ViewModels;
using SkiaSharp;
using Page = PdfNotes.Core.Models.Page;

namespace PdfNotes.UI.Canvas;

/// <summary>Paints on one page in page-viewport space (the canvas is already transformed to the page's DIPs).</summary>
public delegate void PagePainter(SKCanvas canvas, int pageIndex, Page page, ViewTransform transform);

/// <summary>
/// The document area of the app (roadmap P4 A1): the layout (<see cref="DocumentLayout"/>), render plan
/// (<see cref="RenderPlanner"/>) and render queue, drawn with Skia in one custom draw operation: paper, the PDF bitmap
/// (base and detail tiles), the annotation layer, the live preview, then screen marks.
/// Dark pages (spec 24.12) invert the paper, bitmap and annotations, never the marks. Scroll bars are its children.
/// </summary>
public sealed partial class DocumentCanvas : Panel, IDocumentViewport
{
    private const double WheelStepDip = 20, WheelNotchDip = 60;
    private static readonly TimeSpan ScrollPlanDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ZoomPlanDelay = TimeSpan.FromMilliseconds(140);

    private readonly ScrollBar vertical = new() { Orientation = Avalonia.Layout.Orientation.Vertical, AllowAutoHide = false };
    private readonly ScrollBar horizontal = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, AllowAutoHide = false };
    private readonly DispatcherTimer planTimer;
    private readonly Surface surface;
    private IReadOnlyList<Page> pages = [];
    private PageRenderQueue<SKImage>? queue;
    private DocumentLayout? layout;
    private double zoom = 1;
    private ViewMode mode = ViewMode.Continuous;
    private int singlePage;
    private Point2 offset;
    private double viewportWidth, viewportHeight;
    private bool fitWidthPending;
    private bool syncingBars;
    private readonly Dictionary<Guid, (RenderKey Key, SKImage Image)> bases = [];
    private readonly Dictionary<Guid, List<(RenderKey Key, SKImage Image)>> details = []; // the grid tiles at the current zoom (P5d A4)

    public DocumentCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Transparent; // hit-testable everywhere
        surface = new Surface(this) { ClipToBounds = true };
        Children.Add(surface);
        Children.Add(vertical);
        Children.Add(horizontal);
        vertical.ValueChanged += (_, e) => { if (!syncingBars) SetOffset(new(offset.X, e.NewValue)); };
        horizontal.ValueChanged += (_, e) => { if (!syncingBars) SetOffset(new(e.NewValue, offset.Y)); };
        vertical.Scroll += (_, e) => BarPaged(e.ScrollEventType);
        vertical.Cursor = horizontal.Cursor = Cursor.Default; // not the tool's cursor over the bars
        ActualThemeVariantChanged += (_, _) => Redraw(); // shadow, border and paper colors are read per frame
        planTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScrollPlanDelay };
        planTimer.Tick += (_, _) => { planTimer.Stop(); PlanRenders(); };
        InitializeIme();
        InitializeTouchBars();
    }

    /// <summary>Zoom, current page, page count or view mode changed.</summary>
    public event EventHandler? ViewChanged;

    public double Zoom => zoom;
    public ViewMode Mode => mode;
    public int PageCount => pages.Count;
    public int CurrentPageIndex { get; private set; }
    public Point2 Offset => offset;
    public DocumentLayout? Layout => layout;
    public double ViewportWidth => viewportWidth;
    public double ViewportHeight => viewportHeight;
    public double ExtentWidth => layout?.ExtentWidth ?? viewportWidth;
    public double ExtentHeight => layout?.ExtentHeight ?? viewportHeight;
    public PageRenderQueue<SKImage>? Queue => queue;

    /// <summary>Screen pixels per DIP (the monitor's scale).</summary>
    public double DpiScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>
    /// The annotation layer of a page for this frame (inverted with the page on dark pages), taken on the UI thread — what
    /// it shows depends on UI state (a selection being moved, an erase in progress). The default draws the page as it is.
    /// </summary>
    public Func<int, Page, ViewTransform, IReadOnlyCollection<Guid>, PagePainter?>? OverlayFor { get; set; }

    /// <summary>Dark pages for reading (spec 24.12): paper, PDF and annotations inverted.</summary>
    public bool DarkPages
    {
        get => darkPages;
        set { darkPages = value; Redraw(); }
    }
    private bool darkPages;

    /// <summary>Page numbers at the paper's lower right (spec 4.5); 0 hides them.</summary>
    public double PageNumberSize
    {
        get => pageNumberSize;
        set { pageNumberSize = value; Redraw(); }
    }
    private double pageNumberSize = 11;

    /// <summary>Shows a new document from its first page at fit-width zoom. The previous queue is not disposed here.</summary>
    public void SetDocument(IReadOnlyList<Page> documentPages, PageRenderQueue<SKImage> renderQueue)
    {
        if (documentPages.Count == 0) throw new ArgumentException("A document needs at least one page.", nameof(documentPages));
        CancelTool();
        if (queue is not null) queue.Completed -= OnRenderCompleted;
        bases.Clear();
        details.Clear();
        pages = documentPages;
        queue = renderQueue;
        queue.Completed += OnRenderCompleted;
        singlePage = 0;
        offset = new(0, 0);
        CurrentPageIndex = 0;
        fitWidthPending = true;
        layout = null;
        if (viewportWidth > 0) ApplyPendingFit();
        Invalidate(ScrollPlanDelay);
    }

    /// <summary>Lets go of the document before its queue is disposed: nothing is planned or requested afterwards.</summary>
    public void Detach()
    {
        CancelTool();
        planTimer.Stop();
        if (queue is not null) queue.Completed -= OnRenderCompleted;
        queue = null;
    }

    /// <summary>Replaces the page list after a document edit (same queue). Keeps the view position.</summary>
    public void UpdatePages(IReadOnlyList<Page> documentPages)
    {
        if (documentPages.Count == 0) throw new ArgumentException("A document needs at least one page.", nameof(documentPages));
        var anchor = layout?.Anchor(new(0, 0), offset);
        pages = documentPages;
        singlePage = Math.Min(singlePage, pages.Count - 1);
        RebuildLayout();
        if (anchor is { } a && a.PageIndex < pages.Count) offset = ClampOffset(layout!.OffsetForAnchor(a, new(0, 0)));
        Invalidate(ScrollPlanDelay);
    }

    // ----- zoom and navigation -----

    /// <summary>Sets the zoom keeping the page point under <paramref name="viewportAnchor"/> (default: viewport center) in place.</summary>
    public void SetZoom(double newZoom, Point2? viewportAnchor = null)
    {
        newZoom = ZoomLimits.Clamp(newZoom);
        fitWidthPending = false;
        if (Math.Abs(newZoom - zoom) < 1e-9 || pages.Count == 0) { zoom = newZoom; return; }
        var point = viewportAnchor ?? new Point2(viewportWidth / 2, viewportHeight / 2);
        var anchor = EnsureLayout()?.Anchor(point, offset);
        zoom = newZoom;
        RebuildLayout();
        if (anchor is { } a) offset = ClampOffset(layout!.OffsetForAnchor(a, point));
        Invalidate(ZoomPlanDelay);
    }

    void IDocumentViewport.SetZoom(double newZoom) => SetZoom(newZoom);

    public void ZoomBy(double factor, Point2? viewportAnchor = null) => SetZoom(zoom * factor, viewportAnchor);

    public void FitWidth()
    {
        if (pages.Count == 0 || viewportWidth <= 0) { fitWidthPending = true; return; }
        SetZoom(DocumentLayout.FitWidthZoom(pages[CurrentPageIndex].Setup.Frame, viewportWidth));
    }

    public void FitPage()
    {
        if (pages.Count == 0 || viewportWidth <= 0) return;
        int current = CurrentPageIndex;
        SetZoom(DocumentLayout.FitPageZoom(pages[current].Setup.Frame, viewportWidth, viewportHeight));
        GoToPage(current);
    }

    /// <summary>Switches view mode keeping the current page and, when possible, the position on it (spec 4.3).</summary>
    public void SetMode(ViewMode mode)
    {
        if (mode == this.mode) return;
        int current = CurrentPageIndex;
        var center = new Point2(viewportWidth / 2, viewportHeight / 2);
        var anchor = EnsureLayout()?.Anchor(center, offset);
        this.mode = mode;
        singlePage = current;
        RebuildLayout();
        if (anchor is { } a && a.PageIndex == current) offset = ClampOffset(layout!.OffsetForAnchor(a, center));
        else ScrollToPageTop(current);
        Invalidate(ScrollPlanDelay);
    }

    /// <summary>Shows the page (0-based) from its top.</summary>
    public void GoToPage(int pageIndex)
    {
        if (pages.Count == 0) return;
        pageIndex = Math.Clamp(pageIndex, 0, pages.Count - 1);
        if (mode == ViewMode.SinglePage && pageIndex != singlePage)
        {
            singlePage = pageIndex;
            RebuildLayout();
        }
        ScrollToPageTop(pageIndex);
        Invalidate(ScrollPlanDelay);
    }

    public void NextPage() => GoToPage(CurrentPageIndex + 1);
    public void PreviousPage() => GoToPage(CurrentPageIndex - 1);

    public void ScrollToPagePoint(int pageIndex, Point2 point)
    {
        if (pages.Count == 0) return;
        pageIndex = Math.Clamp(pageIndex, 0, pages.Count - 1);
        if (mode == ViewMode.SinglePage && pageIndex != singlePage)
        {
            singlePage = pageIndex;
            RebuildLayout();
        }
        if (EnsureLayout() is not { } l) return;
        offset = ClampOffset(l.OffsetForAnchor(new(pageIndex, point), new(viewportWidth / 2, viewportHeight / 2)));
        Invalidate(ScrollPlanDelay);
    }

    public void ShowPagePoint(int pageIndex, Point2 point)
    {
        GoToPage(pageIndex);
        if (EnsureLayout() is null) return;
        var p = PageTransform(pageIndex).PageToDip.Transform(point);
        SetOffset(new(offset.X, offset.Y + p.Y - DocumentLayout.MarginDip));
    }

    public PageAnchor? CenterAnchor => pages.Count == 0 || viewportWidth <= 0 || EnsureLayout() is not { } l ? null
        : l.Anchor(new(viewportWidth / 2, viewportHeight / 2), offset);

    public void LineUp() => SetOffset(new(offset.X, offset.Y - WheelStepDip));
    public void LineDown() => SetOffset(new(offset.X, offset.Y + WheelStepDip));

    /// <summary>A held ←/→ turns one page per this many milliseconds (2026-10-15 user request: 0.3 s).</summary>
    public const int HeldPageTurnMs = 300;
    private long lastPageTurn = long.MinValue / 2, edgeReached = long.MinValue / 2;

    /// <summary>
    /// ←/→ (spec 24.2): scroll sideways inside a page wider than the view, then turn the page at its edge. Held down it
    /// turns one page per <see cref="HeldPageTurnMs"/> and pauses that long when it reaches an edge; a page wider than the
    /// view is entered from the side the move came from.
    /// </summary>
    public bool ArrowSideways(int direction, bool repeat) => ArrowSideways(direction, repeat, Environment.TickCount64);

    public bool ArrowSideways(int direction, bool repeat, long now)
    {
        if (pages.Count == 0 || EnsureLayout() is not { } l) return false;
        var r = l.PageRect(CurrentPageIndex);
        double max = Math.Max(0, ExtentWidth - viewportWidth);
        bool AtEdge() => direction < 0
            ? offset.X <= 0.5 || r.X - DocumentLayout.MarginDip >= offset.X - 0.5
            : offset.X >= max - 0.5 || r.X + r.Width + DocumentLayout.MarginDip <= offset.X + viewportWidth + 0.5;
        if (!AtEdge())
        {
            var before = offset;
            SetOffset(new(offset.X + direction * WheelStepDip, offset.Y));
            if (AtEdge()) edgeReached = now;
            return offset != before;
        }
        int target = CurrentPageIndex + direction;
        if (target < 0 || target >= pages.Count) return false;
        if (repeat && now - Math.Max(lastPageTurn, edgeReached) < HeldPageTurnMs) return false;
        lastPageTurn = now;
        GoToPage(target);
        var t = EnsureLayout()!.PageRect(target);
        if (t.Width + 2 * DocumentLayout.MarginDip > viewportWidth) // wider than the view: start at the side we came from
            SetOffset(new(direction > 0 ? t.X - DocumentLayout.MarginDip : t.X + t.Width + DocumentLayout.MarginDip - viewportWidth, offset.Y));
        return true;
    }

    public void ScrollBy(double dx, double dy) => SetOffset(new(offset.X + dx, offset.Y + dy));

    public bool IsInView(int pageIndex, IEnumerable<Quad> quads) => ViewportExtent(pageIndex, quads) is { } r &&
        r.MinX >= 0 && r.MinY >= 0 && r.MaxX <= viewportWidth && r.MaxY <= viewportHeight;

    public bool IsPartlyInView(int pageIndex, IEnumerable<Quad> quads) => ViewportExtent(pageIndex, quads) is { } r &&
        r.MaxX >= 0 && r.MaxY >= 0 && r.MinX <= viewportWidth && r.MinY <= viewportHeight;

    public Rect2? VisiblePart(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= pages.Count || (mode == ViewMode.SinglePage && pageIndex != CurrentPageIndex)) return null;
        try
        {
            var setup = pages[pageIndex].Setup;
            var toPage = PageTransform(pageIndex).DipToPage;
            Point2[] corners = [new(0, 0), new(viewportWidth, 0), new(viewportWidth, viewportHeight), new(0, viewportHeight)];
            var p = corners.Select(toPage.Transform).ToArray();
            double x0 = Math.Max(0, p.Min(c => c.X)), y0 = Math.Max(0, p.Min(c => c.Y));
            double x1 = Math.Min(setup.WidthPt, p.Max(c => c.X)), y1 = Math.Min(setup.HeightPt, p.Max(c => c.Y));
            return x1 > x0 && y1 > y0 ? new Rect2(x0, y0, x1 - x0, y1 - y0) : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
    }

    private (double MinX, double MinY, double MaxX, double MaxY)? ViewportExtent(int page, IEnumerable<Quad> quads)
    {
        if (mode == ViewMode.SinglePage && page != CurrentPageIndex) return null;
        try
        {
            var m = PageTransform(page).PageToDip;
            var ring = quads.SelectMany(TextSelection.Ring).Select(m.Transform).ToArray();
            return (ring.Min(p => p.X), ring.Min(p => p.Y), ring.Max(p => p.X), ring.Max(p => p.Y));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>Page transform for a laid-out page: canonical points → viewport DIPs (snapped to device pixels like the bitmaps).</summary>
    public ViewTransform PageTransform(int index)
    {
        var l = EnsureLayout() ?? throw new InvalidOperationException("No document.");
        var r = l.PageRect(index);
        return new(pages[index].Setup.Frame, zoom, DpiScale, new(Snap(r.X - offset.X), Snap(r.Y - offset.Y)));
    }

    /// <summary>The page and canonical point under a viewport position, or null outside pages.</summary>
    public PageAnchor? HitTestPage(Point2 viewportPoint)
    {
        if (EnsureLayout()?.HitTest(viewportPoint, offset) is not { } hit) return null;
        return new(hit.PageIndex, PageTransform(hit.PageIndex).DipToPage.Transform(viewportPoint));
    }

    /// <summary>Recomputes the render plan now instead of after the debounce delay (tests).</summary>
    internal void PlanRendersNow()
    {
        planTimer.Stop();
        PlanRenders();
    }

    void IDocumentViewport.RefreshOverlays() => Redraw();

    // ----- layout -----

    private DocumentLayout? EnsureLayout()
    {
        if (layout is null && pages.Count > 0) RebuildLayout();
        return layout;
    }

    private void RebuildLayout()
    {
        if (pages.Count == 0) { layout = null; return; }
        int shown = layout?.SinglePageIndex ?? -1;
        layout = new DocumentLayout(pages.Select(p => p.Setup.Frame).ToArray(), zoom, mode, singlePage, viewportWidth, viewportHeight);
        singlePage = layout.SinglePageIndex;
        offset = ClampOffset(offset);
        if (mode == ViewMode.SinglePage && shown >= 0 && shown != singlePage && IsToolActive) CancelTool();
    }

    private Point2 ClampOffset(Point2 value) => layout?.ClampOffset(value, viewportWidth, viewportHeight) ?? new(0, 0);

    private void ScrollToPageTop(int index)
    {
        var l = EnsureLayout();
        if (l is null) return;
        var r = l.PageRect(index);
        double x = r.Width + 2 * DocumentLayout.MarginDip <= viewportWidth ? r.X + r.Width / 2 - viewportWidth / 2 : r.X - DocumentLayout.MarginDip;
        offset = ClampOffset(new(x, r.Y - DocumentLayout.MarginDip));
        CurrentPageIndex = index;
    }

    private void ApplyPendingFit()
    {
        if (!fitWidthPending || pages.Count == 0 || viewportWidth <= 0) return;
        fitWidthPending = false;
        zoom = Math.Min(DocumentLayout.FitWidthZoom(pages[CurrentPageIndex].Setup.Frame, viewportWidth), 2.0);
        RebuildLayout();
        ScrollToPageTop(CurrentPageIndex);
    }

    private void SetOffset(Point2 value)
    {
        var clamped = ClampOffset(value);
        if (clamped == offset) return;
        offset = clamped;
        Invalidate(ScrollPlanDelay);
    }

    private void Invalidate(TimeSpan planDelay)
    {
        if (layout is not null) CurrentPageIndex = layout.CurrentPage(new(offset.X, offset.Y, viewportWidth, viewportHeight));
        SyncScrollBars();
        if (NeedBars(Bounds.Size) != (vertical.IsVisible, horizontal.IsVisible)) InvalidateArrange(); // a zoom or mode change needs or frees a bar
        Redraw();
        // A scroll plans at most every ScrollPlanDelay while it goes on (a fling or a drag moves the page every frame and
        // would otherwise push the plan back until it stops: blank pages, roadmap P5d); a zoom waits until it settles.
        if (!(planTimer.IsEnabled && planDelay == ScrollPlanDelay && planTimer.Interval == ScrollPlanDelay))
        {
            planTimer.Stop();
            planTimer.Interval = planDelay;
            planTimer.Start();
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private double Snap(double dip) { double s = DpiScale; return Math.Round(dip * s) / s; }

    private const double BarSize = 12;

    protected override Size MeasureOverride(Size availableSize)
    {
        surface.Measure(availableSize);
        vertical.Measure(availableSize);
        horizontal.Measure(availableSize);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;
        double height = double.IsFinite(availableSize.Height) ? availableSize.Height : 600;
        return new(width, height);
    }

    /// <summary>The pages' own size at the current zoom with the margins (the layout's extent without the viewport).</summary>
    private (IReadOnlyList<Page> Pages, double Zoom, ViewMode Mode, int Single, (double, double) Size)? contentSize;

    /// <summary>Asked on every scroll step (plan C6): worked out again only when the pages, zoom or mode change.</summary>
    private (double Width, double Height) ContentSize()
    {
        if (contentSize is { } known && ReferenceEquals(known.Pages, pages) && known.Zoom == zoom && known.Mode == mode && known.Single == singlePage)
            return known.Size;
        var size = MeasureContent();
        contentSize = (pages, zoom, mode, singlePage, size);
        return size;
    }

    private (double Width, double Height) MeasureContent()
    {
        if (pages.Count == 0) return (0, 0);
        double scale = zoom * Units.DipsPerPoint;
        if (mode == ViewMode.SinglePage)
        {
            var f = pages[Math.Clamp(singlePage, 0, pages.Count - 1)].Setup.Frame;
            return (f.DisplayWidthPt * scale + 2 * DocumentLayout.MarginDip, f.DisplayHeightPt * scale + 2 * DocumentLayout.MarginDip);
        }
        double height = pages.Sum(p => p.Setup.Frame.DisplayHeightPt * scale) + (pages.Count - 1) * DocumentLayout.PageGapDip + 2 * DocumentLayout.MarginDip;
        return (pages.Max(p => p.Setup.Frame.DisplayWidthPt) * scale + 2 * DocumentLayout.MarginDip, height);
    }

    /// <summary>Scroll bars only when the pages do not fit; a bar takes room from the other way.</summary>
    private (bool Vertical, bool Horizontal) NeedBars(Size size)
    {
        var (contentWidth, contentHeight) = ContentSize();
        bool needV = contentHeight > size.Height + 0.5, needH = contentWidth > size.Width - (needV ? BarSize : 0) + 0.5;
        if (needH && !needV) needV = contentHeight > size.Height - BarSize + 0.5;
        return (needV, needH);
    }

    private TopLevel? scaledTop;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Another monitor scale: the pages are rendered again at its pixels, the drawn cursors too.
        scaledTop = TopLevel.GetTopLevel(this);
        if (scaledTop is not null) scaledTop.ScalingChanged += OnScalingChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (scaledTop is not null) scaledTop.ScalingChanged -= OnScalingChanged;
        scaledTop = null;
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        bases.Clear();
        details.Clear();
        Invalidate(ScrollPlanDelay);
        UpdateCursor();
    }

    /// <summary>The scroll bars shown changed (layers over the pages leave room only for bars that are there).</summary>
    public event EventHandler<Thickness>? BarsChanged;
    private Thickness barRoom = new(-1);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (needV, needH) = NeedBars(finalSize);
        vertical.IsVisible = needV;
        horizontal.IsVisible = needH;
        var room = new Thickness(0, 0, needV ? BarSize : 0, needH ? BarSize : 0);
        if (room != barRoom) { barRoom = room; Avalonia.Threading.Dispatcher.UIThread.Post(() => BarsChanged?.Invoke(this, room)); }
        double width = Math.Max(0, finalSize.Width - (needV ? BarSize : 0)), height = Math.Max(0, finalSize.Height - (needH ? BarSize : 0));
        surface.Arrange(new Rect(0, 0, width, height));
        vertical.Arrange(new Rect(width, 0, BarSize, height));
        horizontal.Arrange(new Rect(0, height, width, BarSize));
        if (Math.Abs(width - viewportWidth) > 0.01 || Math.Abs(height - viewportHeight) > 0.01)
        {
            var top = new Point2(viewportWidth / 2, 0);
            var anchor = viewportWidth > 0 ? layout?.Anchor(top, offset) : null;
            viewportWidth = width;
            viewportHeight = height;
            if (fitWidthPending) ApplyPendingFit();
            else
            {
                RebuildLayout();
                if (anchor is { } a) offset = ClampOffset(layout!.OffsetForAnchor(a, new(width / 2, 0)));
            }
            Invalidate(ScrollPlanDelay);
        }
        return finalSize;
    }

    private void SyncScrollBars()
    {
        syncingBars = true;
        vertical.Maximum = Math.Max(0, ExtentHeight - viewportHeight);
        vertical.ViewportSize = viewportHeight;
        vertical.Value = offset.Y;
        vertical.SmallChange = WheelStepDip;
        vertical.LargeChange = viewportHeight * 0.9;
        horizontal.Maximum = Math.Max(0, ExtentWidth - viewportWidth);
        horizontal.ViewportSize = viewportWidth;
        horizontal.Value = offset.X;
        horizontal.SmallChange = WheelStepDip;
        horizontal.LargeChange = viewportWidth * 0.9;
        syncingBars = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        StopFling();
        if (pages.Count == 0) return;
        var at = e.GetPosition(this);
        if (IsControl(e.KeyModifiers))
        {
            // Ctrl+wheel zooms around the pointer (spec 4.4). One notch = ×1.1.
            ZoomBy(Math.Pow(1.1, e.Delta.Y), new(at.X, at.Y));
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ScrollBy(-e.Delta.Y * WheelNotchDip, 0);
        else if (e.Delta.Y > 0 && mode == ViewMode.SinglePage && offset.Y <= 0.5 && CurrentPageIndex > 0 && !IsToolActive)
        {
            // Single page (spec 4.4): scroll inside the page first, turn it at its edge; up shows the previous page's bottom.
            GoToPage(CurrentPageIndex - 1);
            SetOffset(new(offset.X, double.MaxValue / 4));
        }
        else if (e.Delta.Y < 0 && mode == ViewMode.SinglePage && offset.Y >= ExtentHeight - viewportHeight - 0.5 && CurrentPageIndex < pages.Count - 1 && !IsToolActive)
            NextPage();
        else ScrollBy(-e.Delta.X * WheelNotchDip, -e.Delta.Y * WheelNotchDip);
        e.Handled = true;
    }

    /// <summary>A click on the scroll bar's track: in single page mode it turns the page, like PageUp/PageDown.</summary>
    private void BarPaged(Avalonia.Controls.Primitives.ScrollEventType type)
    {
        if (mode != ViewMode.SinglePage) return;
        if (type == Avalonia.Controls.Primitives.ScrollEventType.LargeIncrement) NextPage();
        else if (type == Avalonia.Controls.Primitives.ScrollEventType.LargeDecrement) PreviousPage();
    }

    // ----- rendering -----

    private void PlanRenders()
    {
        if (queue is null || EnsureLayout() is not { } l) return;
        double scale = DpiScale;
        var plan = RenderPlanner.Plan(l, pages, offset, viewportWidth, viewportHeight, scale);
        double target = RenderPlanner.TargetPixelsPerPoint(zoom, scale);
        var viewport = new Rect2(offset.X, offset.Y, viewportWidth, viewportHeight);
        foreach (int index in l.VisiblePages(viewport))
        {
            var page = pages[index];
            var wantedBase = RenderPlanner.BaseKey(page, target);
            if (queue.TryGet(wantedBase, out var image)) bases[page.Id] = (wantedBase, image);
            else if (!bases.ContainsKey(page.Id) && queue.BestBase(wantedBase) is { } best) bases[page.Id] = best;
            var tiles = new List<(RenderKey, SKImage)>();
            foreach (var wantedTile in RenderPlanner.DetailKeys(l, page, index, viewport, scale))
                if (queue.TryGet(wantedTile, out var tile)) tiles.Add((wantedTile, tile));
            if (tiles.Count == 0) details.Remove(page.Id);
            else details[page.Id] = tiles;
        }
        queue.Request(plan);
        // Pictures of pages neither visible nor planned are dropped (the queue's cache keeps what it can afford).
        var keep = plan.Select(k => k.PageId).Concat(l.VisiblePages(viewport).Select(i => pages[i].Id)).ToHashSet();
        foreach (var id in bases.Keys.Where(id => !keep.Contains(id)).ToList()) bases.Remove(id);
        foreach (var id in details.Keys.Where(id => !keep.Contains(id)).ToList()) details.Remove(id);
        Redraw();
    }

    private void OnRenderCompleted(PageRenderResult<SKImage> result)
    {
        if (layout is null || result.Bitmap is not { } image) return;
        var key = result.Key;
        var page = pages.FirstOrDefault(p => p.Id == key.PageId);
        if (page is null || !page.Setup.Equals(key.Setup)) return; // stale: the page changed since
        double target = RenderPlanner.TargetPixelsPerPoint(zoom, DpiScale);
        if (!key.IsDetail)
        {
            var wanted = RenderPlanner.BaseKey(page, target);
            if (!bases.TryGetValue(page.Id, out var shown) || Distance(key, wanted) < Distance(shown.Key, wanted)) bases[page.Id] = (key, image);
        }
        else if (Math.Abs(key.PixelsPerPoint - target) < 1e-9)
        {
            if (!details.TryGetValue(page.Id, out var tiles)) details[page.Id] = tiles = [];
            tiles.RemoveAll(t => t.Key.PixelsPerPoint != key.PixelsPerPoint || !t.Key.Setup.Equals(key.Setup) || t.Key.Region == key.Region);
            tiles.Add((key, image));
        }
        Redraw();
    }

    private static double Distance(RenderKey key, RenderKey wanted) => Math.Abs(Math.Log(key.PixelsPerPoint / wanted.PixelsPerPoint));

    /// <summary>The control the pages are drawn on (a panel cannot draw itself in Avalonia 12); below the scroll bars.</summary>
    private sealed class Surface(DocumentCanvas owner) : Control
    {
        public override void Render(DrawingContext context) => owner.RenderPages(context);
    }

    /// <summary>Something besides a tool preview changed: the cached frame is made again (<see cref="CanvasDrawOperation"/>).</summary>
    private void Redraw()
    {
        baseVersion++;
        surface.InvalidateVisual();
    }

    /// <summary>Only the tool preview changed (a pointer move while drawing): the rest of the frame is reused.</summary>
    private void RedrawPreview() => surface.InvalidateVisual();

    private int baseVersion;
    private readonly FrameCache frameCache = new();

    private void RenderPages(DrawingContext context)
    {
        if (layout is null || pages.Count == 0) return;
        var viewport = new Rect2(offset.X, offset.Y, viewportWidth, viewportHeight);
        var visible = layout.VisiblePages(viewport);
        var shots = new List<PageShot>(visible.Count);
        bool anyLive = UpdateLive();
        foreach (int index in visible)
        {
            var page = pages[index];
            var transform = PageTransform(index);
            var b = bases.TryGetValue(page.Id, out var bb) && bb.Key.Setup.Equals(page.Setup) ? bb : default;
            if (b.Image is null && queue is not null)
            {
                var wanted = RenderPlanner.BaseKey(page, RenderPlanner.TargetPixelsPerPoint(zoom, DpiScale));
                if (queue.TryGet(wanted, out var cached)) b = bases[page.Id] = (wanted, cached);
                else if (queue.BestBase(wanted) is { } best) b = bases[page.Id] = best;
            }
            bool failed = b.Image is null && queue is not null && queue.HasFailed(RenderPlanner.BaseKey(page, RenderPlanner.TargetPixelsPerPoint(zoom, DpiScale)), out _);
            var tiles = details.TryGetValue(page.Id, out var dd) ? dd.Where(t => t.Key.Setup.Equals(page.Setup)).ToArray() : [];
            var live = PreviewPainter(index, page, transform);
            shots.Add(new(index, page, transform, b.Image, tiles, previewIsMark ? null : live, previewIsMark ? live : null,
                MarksFor?.Invoke(index, page, transform), OverlayFor?.Invoke(index, page, transform, lastLive))
            {
                BaseKey = b.Image is null ? null : b.Key, Failed = failed,
                Live = anyLive ? LiveFor?.Invoke(index, page, transform, lastLive) : null,
            });
        }
        context.Custom(new CanvasDrawOperation(new Rect(0, 0, viewportWidth, viewportHeight), shots, Snapshot(), frameCache, baseVersion));
    }

    /// <summary>One visible page as it is drawn this frame (immutable: the draw runs on the render thread).</summary>
    internal sealed record PageShot(int Index, Page Page, ViewTransform Transform, SKImage? Base, IReadOnlyList<(RenderKey Key, SKImage Image)> Tiles,
        PagePainter? Preview = null, PagePainter? MarkPreview = null, PagePainter? Marks = null, PagePainter? Overlay = null)
    {
        /// <summary>The key the base picture was made for (its pixels per point place it 1:1).</summary>
        public RenderKey? BaseKey { get; init; }

        /// <summary>The page could not be rendered: its paper says so (page.renderFailed).</summary>
        public bool Failed { get; init; }

        /// <summary>Annotations a drag is changing, painted over the overlay (not part of the kept frame).</summary>
        public PagePainter? Live { get; init; }
    }

    /// <summary>What the render thread needs from the control for one frame.</summary>
    internal sealed record CanvasLook(bool DarkPages, double PageNumberSize, SKColor Paper, SKColor Shadow, SKColor Edge, SKColor PageNumber)
    {
        public string FailedText { get; init; } = "";
        public SKColor PaperText { get; init; } = new(0x69, 0x69, 0x69);
    }

    private CanvasLook Snapshot()
    {
        SKColor Color(string key, SKColor fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out var v) && v is ISolidColorBrush brush
                ? new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, (byte)(brush.Color.A * brush.Opacity)) : fallback;
        return new(darkPages, pageNumberSize, Color("Paper", SKColors.White), Color("Page.Shadow", new SKColor(0, 0, 0, 0x30)),
            Color("Page.Border", new SKColor(0xC8, 0xC8, 0xC8)), Color("Overlay.PageNumber", new SKColor(0x80, 0x80, 0x80, 0x99)))
            { FailedText = PdfNotes.Editor.Localization.Strings.Get("page.renderFailed"), PaperText = Color("Paper.Text", new SKColor(0x69, 0x69, 0x69)) };
    }
}
