using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PdfNotes.App.Tools;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;
using Page = PdfNotes.Core.Models.Page;

namespace PdfNotes.App.Canvas;

/// <summary>
/// Virtualized page area hosted in a ScrollViewer (CanContentScroll = true). Lays out pages for the current zoom
/// and view mode, realizes only the pages intersecting the viewport, and asks the render scheduler for exactly
/// the bitmaps those pages (plus their neighbours) need. View state never touches the document model.
/// </summary>
public sealed class DocumentView : Panel, IScrollInfo, ICanvasView
{
    private const double WheelStepDip = 20;
    private static readonly TimeSpan ScrollPlanDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ZoomPlanDelay = TimeSpan.FromMilliseconds(140);

    private readonly Dictionary<int, PageVisual> active = new();
    private readonly Stack<PageVisual> pool = new();
    private readonly DispatcherTimer planTimer;
    private IReadOnlyList<Page> pages = [];
    private PageRenderScheduler? scheduler;
    private DocumentLayout? layout;
    private double zoom = 1;
    private ViewMode mode = ViewMode.Continuous;
    private int singlePage;
    private Point2 offset;
    private double viewportWidth, viewportHeight;
    private double dpiScale = 1;
    private bool fitWidthPending;
    private bool spaceHeld, panning;
    private Point panStart;
    private Point2 panStartOffset;
    private PageOverlayPainter? overlayPainter;
    private PageOverlayPainter? marksPainter;
    private bool previewIsMark;
    private ICanvasTool? tool;
    private bool toolCaptured;
    private int previewPage = -1;
    private PageOverlayPainter? previewPainter;

    public DocumentView()
    {
        SetResourceReference(BackgroundProperty, "Canvas.Background"); // the area outside the pages follows the theme
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        planTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScrollPlanDelay };
        planTimer.Tick += (_, _) => { planTimer.Stop(); PlanRenders(); };
    }

    /// <summary>Zoom, current page, page count or view mode changed.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>The canonical page position under the mouse (null outside pages). For the status bar and later tools.</summary>
    public event EventHandler<PageAnchor?>? PointerPageChanged;

    public double Zoom => zoom;
    public ViewMode Mode => mode;
    public int PageCount => pages.Count;
    public int CurrentPageIndex { get; private set; }
    public double DpiScale => dpiScale;
    public Point2 Offset => offset;
    public DocumentLayout? Layout => layout;
    public IReadOnlyCollection<PageVisual> RealizedPages => active.Values;
    public PageRenderScheduler? Scheduler => scheduler;

    /// <summary>Overlay layer painter (app annotations from D04 on). Never part of the background bitmap.</summary>
    public PageOverlayPainter? OverlayPainter
    {
        get => overlayPainter;
        set
        {
            overlayPainter = value;
            foreach (var visual in active.Values) visual.SetOverlayPainter(value, marksPainter);
        }
    }

    /// <summary>Screen marks above the page content (selection, search, text selection): not inverted on a dark page (spec 24.12).</summary>
    public PageOverlayPainter? MarksPainter
    {
        get => marksPainter;
        set
        {
            marksPainter = value;
            foreach (var visual in active.Values) visual.SetOverlayPainter(overlayPainter, value);
        }
    }

    /// <summary>Active canvas tool. Switching cancels an operation of the previous tool.</summary>
    public ICanvasTool? Tool
    {
        get => tool;
        set
        {
            if (ReferenceEquals(tool, value)) return;
            CancelTool();
            (tool as IHoverTool)?.PointerHover(null, Modifiers);
            tool = value;
            UpdateCursor();
        }
    }

    /// <summary>True while the active tool has an operation in progress (for example a stroke being drawn).</summary>
    public bool IsToolActive => tool?.IsActive == true || middleTool is not null;

    /// <summary>
    /// The middle mouse button (spec 7.5 quick erase): <c>Reserved</c> means the current tool owns the button, so it never
    /// cancels a left-button stroke; <c>Tool</c> is the temporary tool used while it is held (null: the button does nothing).
    /// </summary>
    public Func<(bool Reserved, ICanvasTool? Tool)>? MiddleButton { get; set; }

    private ICanvasTool? middleTool;

    /// <summary>The tool whose operation is in progress: the temporary middle-button tool while it is held, else <see cref="Tool"/>.</summary>
    public ICanvasTool? ActiveTool => middleTool ?? tool;

    /// <summary>Raised when a middle-button operation ends (committed or cancelled): the original tool is back.</summary>
    public event EventHandler? MiddleButtonEnded;

    /// <summary>Raised when a press starts an operation of the tool (or of the middle-button tool); panning does not.</summary>
    public event EventHandler? ToolPressed;

    /// <summary>
    /// PDF links (spec 24.3): the tooltip of a link under a viewport point, null where there is none. With
    /// <see cref="LinkFollowsClick"/> (the selection tool) a click that does not drag follows it; with any tool Ctrl+click
    /// does, before the tool sees the press (a drawing tool otherwise draws over links).
    /// </summary>
    public Func<Point, string?>? LinkTip { get; set; }
    public Func<bool>? LinkFollowsClick { get; set; }
    public event EventHandler<Point>? LinkClicked;
    private Point? linkPress;

    /// <summary>Cancels the active tool's operation; the document is not changed (Esc, spec 4.3).</summary>
    public void CancelTool()
    {
        if (tool?.IsActive == true) tool.Cancel();
        EndMiddle(commit: null);
        if (toolCaptured)
        {
            toolCaptured = false;
            ReleaseMouseCapture();
        }
    }

    /// <summary>Shows a temporary mark on one page above its annotations; null clears it.</summary>
    /// <param name="mark">A screen mark (eraser circle, lasso path) rather than content being drawn.</param>
    public void SetPreview(int pageIndex, PageOverlayPainter? painter, bool mark = false)
    {
        previewIsMark = mark;
        previewPage = painter is null ? -1 : pageIndex;
        previewPainter = painter;
        foreach (var (index, visual) in active) visual.SetPreview(index == previewPage ? previewPainter : null, previewIsMark);
    }

    /// <summary>Redraws only the preview layer (called on every pointer move while drawing).</summary>
    public void RefreshPreview()
    {
        if (active.TryGetValue(previewPage, out var visual)) visual.RefreshPreview();
    }

    /// <summary>Redraws the annotation overlays of realized pages (selection changes).</summary>
    public void RefreshOverlays()
    {
        foreach (var visual in active.Values) visual.RefreshOverlay();
    }

    /// <summary>Redraws the annotation overlay of one page if it is realized (eraser preview).</summary>
    public void RefreshOverlay(int pageIndex)
    {
        if (active.TryGetValue(pageIndex, out var visual)) visual.RefreshOverlay();
    }

    /// <summary>Shows a new document from its first page at fit-width zoom. The previous scheduler is not disposed here.</summary>
    public void SetDocument(IReadOnlyList<Page> documentPages, PageRenderScheduler renderScheduler)
    {
        if (documentPages.Count == 0) throw new ArgumentException("A document needs at least one page.", nameof(documentPages));
        CancelTool();
        SetPreview(-1, null);
        if (scheduler is not null) scheduler.Completed -= OnRenderCompleted;
        foreach (var visual in active.Values) Recycle(visual);
        active.Clear();
        pages = documentPages;
        scheduler = renderScheduler;
        scheduler.Completed += OnRenderCompleted;
        singlePage = 0;
        offset = new(0, 0);
        CurrentPageIndex = 0;
        fitWidthPending = true;
        layout = null;
        if (viewportWidth > 0) ApplyPendingFit();
        Invalidate(ScrollPlanDelay);
    }

    /// <summary>Lets go of the document before its scheduler is disposed (window closed): no render is planned or requested afterwards.</summary>
    public void Detach()
    {
        CancelTool();
        planTimer.Stop();
        if (scheduler is not null) scheduler.Completed -= OnRenderCompleted;
        scheduler = null;
    }

    /// <summary>Replaces the page list after a document edit (same scheduler). Keeps the view position.</summary>
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

    /// <summary>Sets the zoom keeping the page point under <paramref name="viewportAnchor"/> (default: viewport center) in place.</summary>
    public void SetZoom(double newZoom, Point? viewportAnchor = null)
    {
        newZoom = ZoomLimits.Clamp(newZoom);
        fitWidthPending = false;
        if (Math.Abs(newZoom - zoom) < 1e-9 || pages.Count == 0) { zoom = newZoom; return; }
        var point = viewportAnchor is { } p ? new Point2(p.X, p.Y) : new Point2(viewportWidth / 2, viewportHeight / 2);
        var anchor = EnsureLayout()?.Anchor(point, offset);
        zoom = newZoom;
        RebuildLayout();
        if (anchor is { } a) offset = ClampOffset(layout!.OffsetForAnchor(a, point));
        Invalidate(ZoomPlanDelay);
    }

    public void ZoomBy(double factor, Point? viewportAnchor = null) => SetZoom(zoom * factor, viewportAnchor);

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
    public void SetMode(ViewMode newMode)
    {
        if (newMode == mode) return;
        int current = CurrentPageIndex;
        var center = new Point2(viewportWidth / 2, viewportHeight / 2);
        var anchor = EnsureLayout()?.Anchor(center, offset);
        mode = newMode;
        singlePage = current;
        RebuildLayout();
        if (anchor is { } a && a.PageIndex == current) offset = ClampOffset(layout!.OffsetForAnchor(a, center));
        else ScrollToPageTop(current);
        Invalidate(ScrollPlanDelay);
    }

    /// <summary>Shows the page (0-based) from its top.</summary>
    public void GoToPage(int index)
    {
        if (pages.Count == 0) return;
        index = Math.Clamp(index, 0, pages.Count - 1);
        if (mode == ViewMode.SinglePage && index != singlePage)
        {
            singlePage = index;
            RebuildLayout();
        }
        ScrollToPageTop(index);
        Invalidate(ScrollPlanDelay);
    }

    /// <summary>Scrolls so that a canonical point of a page is at the viewport center (as far as the extent allows).</summary>
    public void ScrollToPagePoint(int index, Point2 pagePoint)
    {
        if (pages.Count == 0) return;
        index = Math.Clamp(index, 0, pages.Count - 1);
        if (mode == ViewMode.SinglePage && index != singlePage)
        {
            singlePage = index;
            RebuildLayout();
        }
        if (EnsureLayout() is not { } l) return;
        offset = ClampOffset(l.OffsetForAnchor(new(index, pagePoint), new(viewportWidth / 2, viewportHeight / 2)));
        Invalidate(ScrollPlanDelay);
    }

    /// <summary>A press on a link that moves less than this (DIP) before release is a click (spec 24.3).</summary>
    public const double LinkClickDip = 4;

    /// <summary>Shows a page with a canonical point of it near the top of the view (a link or outline destination, spec 24.3).</summary>
    public void ShowPagePoint(int index, Point2 pagePoint)
    {
        GoToPage(index);
        if (EnsureLayout() is null) return;
        var p = PageTransform(index).PageToDip.Transform(pagePoint);
        SetOffset(new(offset.X, offset.Y + p.Y - DocumentLayout.MarginDip));
    }

    /// <summary>The page point at the viewport center: what is remembered per document (spec 24.1).</summary>
    public PageAnchor? CenterAnchor => pages.Count == 0 || viewportWidth <= 0 || EnsureLayout() is not { } l ? null
        : l.Anchor(new(viewportWidth / 2, viewportHeight / 2), offset);

    public void NextPage() => GoToPage(CurrentPageIndex + 1);

    public void PreviousPage() => GoToPage(CurrentPageIndex - 1);

    /// <summary>
    /// ←/→ (spec 4.4, 2026-10-14, like the Edge and Chrome PDF viewers): scrolls sideways; once the current page's edge
    /// in that direction is shown (at once when it fits the view) a new press turns to the previous/next page, shown from
    /// its top at the opposite edge. A held key (<paramref name="repeat"/>) keeps turning pages, one per
    /// <see cref="HeldPageTurnMs"/>, after pausing as long at an edge it scrolled to (2026-10-15 user request; it used to
    /// stop at the edge). Returns false when nothing happened.
    /// </summary>
    public bool ArrowSideways(int direction, bool repeat) => ArrowSideways(direction, repeat, Environment.TickCount64);

    /// <summary>A held ←/→ turns one page per this many milliseconds (2026-10-15 user request: 0.3 s).</summary>
    public const int HeldPageTurnMs = 300;
    private long lastPageTurn = long.MinValue / 2, edgeReached = long.MinValue / 2;

    /// <param name="now">Milliseconds (Environment.TickCount64; the validation harness passes its own clock).</param>
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

    /// <summary>Space held outside text input: the next left drag pans the view (spec 4.4).</summary>
    public void SetTemporaryHand(bool held)
    {
        spaceHeld = held;
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (spaceHeld || panning) Cursor = Cursors.Hand;
        else if (SelectionCursors.For(ActiveTool?.Cursor) is { } cursor) Cursor = cursor;
        else ClearValue(CursorProperty);
    }

    /// <summary>Page element transform for a laid-out page: canonical points → viewport DIPs (snapped like the bitmaps).</summary>
    public ViewTransform PageTransform(int index)
    {
        var l = EnsureLayout() ?? throw new InvalidOperationException("No document.");
        var r = l.PageRect(index);
        return new(pages[index].Setup.Frame, zoom, dpiScale, new(Snap(r.X - offset.X), Snap(r.Y - offset.Y)));
    }

    /// <summary>The page and canonical point under a viewport position, or null outside pages.</summary>
    public PageAnchor? HitTestPage(Point viewportPoint)
    {
        if (EnsureLayout()?.HitTest(new(viewportPoint.X, viewportPoint.Y), offset) is not { } hit) return null;
        return new(hit.PageIndex, PageTransform(hit.PageIndex).DipToPage.Transform(new(viewportPoint.X, viewportPoint.Y)));
    }

    /// <summary>Recomputes the render plan now instead of after the debounce delay.</summary>
    public void PlanRendersNow()
    {
        planTimer.Stop();
        PlanRenders();
    }

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
        // Single page mode lays out one page only: an operation on the page that left the view cannot continue
        // (its page transform no longer exists — the 2026-10-15 crash). Page keys and the wheel wait for it instead.
        if (mode == ViewMode.SinglePage && shown >= 0 && shown != singlePage && IsToolActive) CancelTool();
    }

    private Point2 ClampOffset(Point2 value) => layout?.ClampOffset(value, viewportWidth, viewportHeight) ?? new(0, 0);

    private void ScrollToPageTop(int index)
    {
        var l = EnsureLayout();
        if (l is null) return;
        var r = l.PageRect(index);
        // Pages are centred on the widest page; centre this one (or show its left edge when it is wider than the view).
        double x = r.Width + 2 * DocumentLayout.MarginDip <= viewportWidth ? r.X + r.Width / 2 - viewportWidth / 2 : r.X - DocumentLayout.MarginDip;
        offset = ClampOffset(new(x, r.Y - DocumentLayout.MarginDip));
        CurrentPageIndex = index;
    }

    private void ApplyPendingFit()
    {
        if (!fitWidthPending || pages.Count == 0 || viewportWidth <= 0) return;
        fitWidthPending = false;
        // Open at fit-width, but not larger than 200% for small pages.
        zoom = Math.Min(DocumentLayout.FitWidthZoom(pages[CurrentPageIndex].Setup.Frame, viewportWidth), 2.0);
        RebuildLayout();
        ScrollToPageTop(CurrentPageIndex);
    }

    private void Invalidate(TimeSpan planDelay)
    {
        InvalidateMeasure();
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
        UpdateCurrentPage();
        planTimer.Stop();
        planTimer.Interval = planDelay;
        planTimer.Start();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCurrentPage()
    {
        if (layout is not null) CurrentPageIndex = layout.CurrentPage(new(offset.X, offset.Y, viewportWidth, viewportHeight));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;
        double height = double.IsFinite(availableSize.Height) ? availableSize.Height : 600;
        if (Math.Abs(width - viewportWidth) > 0.01 || Math.Abs(height - viewportHeight) > 0.01)
        {
            // Keep the content under the top-center of the viewport stable while the window is resized.
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
            ScrollOwner?.InvalidateScrollInfo();
            UpdateCurrentPage();
            planTimer.Stop();
            planTimer.Interval = ScrollPlanDelay;
            planTimer.Start();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        RealizeVisiblePages();
        foreach (var visual in active.Values) visual.Measure(new Size(visual.Width, visual.Height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (layout is null) return finalSize;
        foreach (var (index, visual) in active)
        {
            var r = layout.PageRect(index);
            // Snap to device pixels so a bitmap rendered at the current zoom maps 1:1 to screen pixels.
            visual.Arrange(new Rect(Snap(r.X - offset.X), Snap(r.Y - offset.Y), r.Width, r.Height));
        }
        return finalSize;
    }

    private double Snap(double dip) => Math.Round(dip * dpiScale) / dpiScale;

    private void RealizeVisiblePages()
    {
        var l = EnsureLayout();
        if (l is null) return;
        var visible = l.VisiblePages(new(offset.X, offset.Y, viewportWidth, viewportHeight)).ToHashSet();
        foreach (int index in active.Keys.Where(i => !visible.Contains(i)).ToList())
        {
            Recycle(active[index]);
            active.Remove(index);
        }
        double dipsPerPoint = l.DipsPerPoint;
        double target = RenderPlanner.TargetPixelsPerPoint(zoom, dpiScale);
        foreach (int index in visible)
        {
            var page = pages[index];
            if (!active.TryGetValue(index, out var visual))
            {
                visual = pool.Count > 0 ? pool.Pop() : new PageVisual();
                active[index] = visual;
                Children.Add(visual);
            }
            var r = l.PageRect(index);
            visual.Width = r.Width;
            visual.Height = r.Height;
            visual.Assign(index, page, dipsPerPoint, dpiScale, overlayPainter, marksPainter);
            visual.SetPreview(index == previewPage ? previewPainter : null, previewIsMark);
            if (visual.BaseBitmap is null && scheduler?.BestBase(RenderPlanner.BaseKey(page, target)) is { } best)
                visual.SetBase(best.Key, best.Bitmap);
        }
    }

    private void Recycle(PageVisual visual)
    {
        visual.Release();
        Children.Remove(visual);
        pool.Push(visual);
    }

    // ----- rendering -----

    private void PlanRenders()
    {
        if (scheduler is null || EnsureLayout() is not { } l) return;
        var plan = RenderPlanner.Plan(l, pages, offset, viewportWidth, viewportHeight, dpiScale);
        double target = RenderPlanner.TargetPixelsPerPoint(zoom, dpiScale);
        foreach (var visual in active.Values)
        {
            var wantedBase = RenderPlanner.BaseKey(visual.Page!, target);
            if (!wantedBase.Equals(visual.BaseKey) && scheduler.TryGet(wantedBase, out var bitmap)) visual.SetBase(wantedBase, bitmap);
            else if (scheduler.HasFailed(wantedBase, out var error)) visual.SetError(error);
            var viewport = new Rect2(offset.X, offset.Y, viewportWidth, viewportHeight);
            var wantedDetail = RenderPlanner.DetailKey(l, visual.Page!, visual.Index, viewport, dpiScale);
            if (wantedDetail is null)
            {
                if (visual.DetailKey is not null && wantedBase.Equals(visual.BaseKey)) visual.SetDetail(null, null);
            }
            else if (!wantedDetail.Equals(visual.DetailKey) && scheduler.TryGet(wantedDetail, out var detail))
                visual.SetDetail(wantedDetail, detail);
        }
        scheduler.Request(plan);
    }

    private void OnRenderCompleted(RenderResult result)
    {
        if (layout is null) return;
        var key = result.Key;
        double target = RenderPlanner.TargetPixelsPerPoint(zoom, dpiScale);
        foreach (var visual in active.Values)
        {
            // Stale guard: only a page with the same id AND the same setup accepts the result.
            if (visual.Page is not { } page || page.Id != key.PageId || !page.Setup.Equals(key.Setup)) continue;
            if (result.Bitmap is null)
            {
                if (key.Equals(RenderPlanner.BaseKey(page, target))) visual.SetError(result.Error ?? "");
                continue;
            }
            if (!key.IsDetail)
            {
                var wanted = RenderPlanner.BaseKey(page, target);
                if (visual.BaseKey is null || Distance(key, wanted) < Distance(visual.BaseKey, wanted)) visual.SetBase(key, result.Bitmap);
                if (key.Equals(wanted) && RenderPlanner.DetailKey(layout, page, visual.Index,
                        new(offset.X, offset.Y, viewportWidth, viewportHeight), dpiScale) is null)
                    visual.SetDetail(null, null);
            }
            else if (Math.Abs(key.PixelsPerPoint - target) < 1e-9)
                visual.SetDetail(key, result.Bitmap);
        }
    }

    private static double Distance(RenderKey key, RenderKey wanted) => Math.Abs(Math.Log(key.PixelsPerPoint / wanted.PixelsPerPoint));

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        dpiScale = newDpi.DpiScaleX;
        foreach (var visual in active.Values) Recycle(visual);
        active.Clear();
        Invalidate(ScrollPlanDelay);
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    // ----- input -----

    private static InputModifiers Modifiers => (InputModifiers)Keyboard.Modifiers;

    private static Point2 At(Point p) => new(p.X, p.Y);

    /// <summary>The editor's pointer event for a WPF mouse event (a pen or touch arrives as promoted mouse input).</summary>
    private CanvasPointer Pointer(MouseEventArgs e, Point? at = null, int clicks = 1, PointerButton button = PointerButton.Left)
    {
        var device = PointerDevice.Mouse;
        double? pressure = null;
        if (e.StylusDevice is { } stylus)
        {
            device = stylus.TabletDevice?.Type == TabletDeviceType.Touch ? PointerDevice.Touch : PointerDevice.Pen;
            var points = stylus.GetStylusPoints(this);
            if (points.Count > 0 && points.Description.HasProperty(StylusPointProperties.NormalPressure)) pressure = points[points.Count - 1].PressureFactor;
        }
        return new(At(at ?? e.GetPosition(this)), Modifiers, clicks, button, device, pressure);
    }

    public PageAnchor? HitTestPage(Point2 viewportPoint) => HitTestPage(new Point(viewportPoint.X, viewportPoint.Y));

    void ICanvasView.SetPreview(int pageIndex, ICanvasPreview? preview, bool mark) =>
        SetPreview(pageIndex, preview is null ? null : ToolMarkPainter.For(preview), mark);


    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || pages.Count == 0) return;
        // Ctrl+wheel zooms around the cursor (spec 4.4). One notch = ×1.1.
        ZoomBy(Math.Pow(1.1, e.Delta / 120.0), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (middleTool is not null)
        {
            // Another button while the middle button erases cancels the rub and returns to the tool (spec 7.5).
            if (e.ChangedButton != MouseButton.Middle) EndMiddle(commit: null);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle && MiddleButton?.Invoke() is { Reserved: true } middle)
        {
            // Quick erase (spec 7.5): only from idle; while the left button draws, the middle button is ignored.
            if (!IsToolActive && !panning && middle.Tool is { } temporary && temporary.PointerDown(Pointer(e, button: PointerButton.Middle)))
            {
                if (temporary.IsActive && CaptureMouse()) { middleTool = temporary; (tool as IHoverTool)?.PointerHover(null, Modifiers); }
                else temporary.Cancel();
                UpdateCursor();
                ToolPressed?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
        {
            // Another button during a stroke never commits or creates it (spec 5.6).
            if (IsToolActive) CancelTool();
            return;
        }
        linkPress = null;
        if (!spaceHeld && !IsToolActive && LinkTip?.Invoke(e.GetPosition(this)) is not null)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                LinkClicked?.Invoke(this, e.GetPosition(this));
                e.Handled = true;
                return;
            }
            if (LinkFollowsClick?.Invoke() == true) linkPress = e.GetPosition(this);
        }
        if (spaceHeld && !IsToolActive && CaptureMouse())
        {
            panning = true;
            panStart = e.GetPosition(this);
            panStartOffset = offset;
            e.Handled = true;
        }
        else if (!spaceHeld && tool is not null && (!IsToolActive || tool is IMultiClickTool && !toolCaptured) &&
                 tool.PointerDown(Pointer(e, clicks: e.ClickCount)))
        {
            toolCaptured = tool.IsActive && CaptureMouse();
            if (tool.IsActive && !toolCaptured) tool.Cancel();
            UpdateCursor(); // the operation picks its cursor (move, resize, rotate)
            e.Handled = true;
            ToolPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (panning)
        {
            SetOffset(new(panStartOffset.X - (position.X - panStart.X), panStartOffset.Y - (position.Y - panStart.Y)));
            e.Handled = true;
        }
        else if (middleTool is { } temporary)
        {
            temporary.PointerMove(Pointer(e, position, button: PointerButton.Middle));
            e.Handled = true;
        }
        else if (toolCaptured && tool is { IsActive: true })
        {
            tool.PointerMove(Pointer(e, position));
            e.Handled = true;
        }
        else
        {
            (tool as IHoverTool)?.PointerHover(spaceHeld ? null : At(position), Modifiers);
            UpdateCursor(); // a hover tool may change its cursor (I-beam over PDF text)
            string? tip = spaceHeld ? null : LinkTip?.Invoke(position);
            if (tip is not null && (LinkFollowsClick?.Invoke() == true || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) Cursor = Cursors.Hand;
            if (!Equals(ToolTip, tip)) ToolTip = tip;
        }
        PointerPageChanged?.Invoke(this, HitTestPage(position));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && middleTool is not null)
        {
            EndMiddle(commit: e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        var press = linkPress;
        linkPress = null;
        if (press is { } at && (e.GetPosition(this) - at).Length < LinkClickDip)
            Dispatcher.BeginInvoke(() => LinkClicked?.Invoke(this, at)); // after the tool has finished the click
        if (panning)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
        else if (toolCaptured && tool is { IsActive: true })
        {
            toolCaptured = false;
            tool.PointerUp(Pointer(e));
            ReleaseMouseCapture();
            (tool as IHoverTool)?.PointerHover(At(e.GetPosition(this)), Modifiers);
            UpdateCursor();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndMiddle(commit: null);
        panning = false;
        // Losing capture mid-stroke (another window, Alt+Tab, …) cancels the unfinished operation (spec 4.3). A release
        // after the press ended is not a loss: a free polygon stays active between its clicks.
        bool pressed = toolCaptured;
        toolCaptured = false;
        if (pressed && tool?.IsActive == true) tool.Cancel();
        UpdateCursor();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsToolActive) (tool as IHoverTool)?.PointerHover(null, Modifiers);
        PointerPageChanged?.Invoke(this, null);
    }

    /// <summary>Ends the middle-button tool: commits at <paramref name="commit"/> (release) or cancels (null), then shows the original tool again.</summary>
    private void EndMiddle(Point? commit)
    {
        if (middleTool is not { } temporary) return;
        middleTool = null; // first: releasing the capture below calls back here
        if (commit is { } at && temporary.IsActive) temporary.PointerUp(new(At(at), Modifiers, Button: PointerButton.Middle));
        else if (temporary.IsActive) temporary.Cancel();
        (temporary as IHoverTool)?.PointerHover(null, Modifiers);
        if (IsMouseCaptured && !toolCaptured) ReleaseMouseCapture();
        if (IsMouseOver) (tool as IHoverTool)?.PointerHover(At(Mouse.GetPosition(this)), Modifiers);
        UpdateCursor();
        MiddleButtonEnded?.Invoke(this, EventArgs.Empty);
    }

    private void SetOffset(Point2 value)
    {
        var clamped = ClampOffset(value);
        if (clamped == offset) return;
        offset = clamped;
        Invalidate(ScrollPlanDelay);
    }

    // ----- IScrollInfo -----

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; } = true;
    public double ExtentWidth => layout?.ExtentWidth ?? viewportWidth;
    public double ExtentHeight => layout?.ExtentHeight ?? viewportHeight;
    public double ViewportWidth => viewportWidth;
    public double ViewportHeight => viewportHeight;
    public double HorizontalOffset => offset.X;
    public double VerticalOffset => offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetOffset(new(offset.X, offset.Y - WheelStepDip));
    public void LineDown() => SetOffset(new(offset.X, offset.Y + WheelStepDip));
    public void LineLeft() => SetOffset(new(offset.X - WheelStepDip, offset.Y));
    public void LineRight() => SetOffset(new(offset.X + WheelStepDip, offset.Y));

    /// <summary>Single-page mode: previous/next page (spec 4.4). Continuous: one viewport.</summary>
    public void PageUp()
    {
        if (mode == ViewMode.SinglePage) PreviousPage();
        else SetOffset(new(offset.X, offset.Y - viewportHeight * 0.9));
    }

    public void PageDown()
    {
        if (mode == ViewMode.SinglePage) NextPage();
        else SetOffset(new(offset.X, offset.Y + viewportHeight * 0.9));
    }

    public void PageLeft() => SetOffset(new(offset.X - viewportWidth * 0.9, offset.Y));
    public void PageRight() => SetOffset(new(offset.X + viewportWidth * 0.9, offset.Y));

    private double WheelDelta => Math.Max(1, SystemParameters.WheelScrollLines) * WheelStepDip;

    /// <summary>Single-page mode scrolls inside the page first and turns the page only at its edge (spec 4.4).</summary>
    public void MouseWheelUp()
    {
        if (mode == ViewMode.SinglePage && offset.Y <= 0.5 && CurrentPageIndex > 0 && !IsToolActive)
        {
            int previous = CurrentPageIndex - 1;
            GoToPage(previous);
            SetOffset(new(offset.X, double.MaxValue / 4)); // show the bottom of the previous page
            return;
        }
        SetOffset(new(offset.X, offset.Y - WheelDelta));
    }

    public void MouseWheelDown()
    {
        if (mode == ViewMode.SinglePage && offset.Y >= ExtentHeight - viewportHeight - 0.5 && CurrentPageIndex < pages.Count - 1 && !IsToolActive)
        {
            NextPage();
            return;
        }
        SetOffset(new(offset.X, offset.Y + WheelDelta));
    }

    public void MouseWheelLeft() => SetOffset(new(offset.X - WheelDelta, offset.Y));
    public void MouseWheelRight() => SetOffset(new(offset.X + WheelDelta, offset.Y));
    public void SetHorizontalOffset(double value) => SetOffset(new(value, offset.Y));
    public void SetVerticalOffset(double value) => SetOffset(new(offset.X, value));
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
