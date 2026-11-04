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

namespace PdfNotes.Desktop.Canvas;

/// <summary>Paints on one page in page-viewport space (the canvas is already transformed to the page's DIPs).</summary>
public delegate void PagePainter(SKCanvas canvas, int pageIndex, Page page, ViewTransform transform);

/// <summary>
/// The document area of the Avalonia app (roadmap P4 A1): the same layout (<see cref="DocumentLayout"/>), render plan
/// (<see cref="RenderPlanner"/>) and render queue as the WPF <c>DocumentView</c>, drawn with Skia in one custom draw
/// operation: paper, the PDF bitmap (base and detail tiles), the annotation layer, the live preview, then screen marks.
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
    private readonly Dictionary<Guid, (RenderKey Key, SKImage Image)> details = [];

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
        planTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScrollPlanDelay };
        planTimer.Tick += (_, _) => { planTimer.Stop(); PlanRenders(); };
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

    /// <summary>The annotation layer (inverted with the page on dark pages).</summary>
    public PagePainter? OverlayPainter { get; set; }

    /// <summary>Screen marks above the page content (selection, search): never inverted.</summary>
    public PagePainter? MarksPainter { get; set; }

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

    // ----- zoom and navigation (the WPF DocumentView's rules) -----

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

    public void NextPage() => GoToPage(CurrentPageIndex + 1);
    public void PreviousPage() => GoToPage(CurrentPageIndex - 1);

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

    public void ShowPagePoint(int index, Point2 pagePoint)
    {
        GoToPage(index);
        if (EnsureLayout() is null) return;
        var p = PageTransform(index).PageToDip.Transform(pagePoint);
        SetOffset(new(offset.X, offset.Y + p.Y - DocumentLayout.MarginDip));
    }

    public PageAnchor? CenterAnchor => pages.Count == 0 || viewportWidth <= 0 || EnsureLayout() is not { } l ? null
        : l.Anchor(new(viewportWidth / 2, viewportHeight / 2), offset);

    public void LineUp() => SetOffset(new(offset.X, offset.Y - WheelStepDip));
    public void LineDown() => SetOffset(new(offset.X, offset.Y + WheelStepDip));

    public bool ArrowSideways(int direction, bool repeat)
    {
        if (pages.Count == 0 || EnsureLayout() is not { } l) return false;
        var r = l.PageRect(CurrentPageIndex);
        double max = Math.Max(0, ExtentWidth - viewportWidth);
        bool atEdge = direction < 0
            ? offset.X <= 0.5 || r.X - DocumentLayout.MarginDip >= offset.X - 0.5
            : offset.X >= max - 0.5 || r.X + r.Width + DocumentLayout.MarginDip <= offset.X + viewportWidth + 0.5;
        if (!atEdge)
        {
            var before = offset;
            SetOffset(new(offset.X + direction * WheelStepDip, offset.Y));
            return offset != before;
        }
        int target = CurrentPageIndex + direction;
        if (target < 0 || target >= pages.Count || repeat) return false;
        GoToPage(target);
        return true;
    }

    public void ScrollBy(double dx, double dy) => SetOffset(new(offset.X + dx, offset.Y + dy));

    public bool IsInView(int page, IEnumerable<Quad> quads) => ViewportExtent(page, quads) is { } r &&
        r.MinX >= 0 && r.MinY >= 0 && r.MaxX <= viewportWidth && r.MaxY <= viewportHeight;

    public bool IsPartlyInView(int page, IEnumerable<Quad> quads) => ViewportExtent(page, quads) is { } r &&
        r.MaxX >= 0 && r.MaxY >= 0 && r.MinX <= viewportWidth && r.MinY <= viewportHeight;

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
    public void PlanRendersNow()
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
        Redraw();
        planTimer.Stop();
        planTimer.Interval = planDelay;
        planTimer.Start();
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

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = Math.Max(0, finalSize.Width - BarSize), height = Math.Max(0, finalSize.Height - BarSize);
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
        if (pages.Count == 0) return;
        var at = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+wheel zooms around the pointer (spec 4.4). One notch = ×1.1.
            ZoomBy(Math.Pow(1.1, e.Delta.Y), new(at.X, at.Y));
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ScrollBy(-e.Delta.Y * WheelNotchDip, 0);
        else ScrollBy(-e.Delta.X * WheelNotchDip, -e.Delta.Y * WheelNotchDip);
        e.Handled = true;
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
            var wantedDetail = RenderPlanner.DetailKey(l, page, index, viewport, scale);
            if (wantedDetail is null) details.Remove(page.Id);
            else if (queue.TryGet(wantedDetail, out var detail)) details[page.Id] = (wantedDetail, detail);
        }
        queue.Request(plan);
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
        else if (Math.Abs(key.PixelsPerPoint - target) < 1e-9) details[page.Id] = (key, image);
        Redraw();
    }

    private static double Distance(RenderKey key, RenderKey wanted) => Math.Abs(Math.Log(key.PixelsPerPoint / wanted.PixelsPerPoint));

    /// <summary>The control the pages are drawn on (a panel cannot draw itself in Avalonia 12); below the scroll bars.</summary>
    private sealed class Surface(DocumentCanvas owner) : Control
    {
        public override void Render(DrawingContext context) => owner.RenderPages(context);
    }

    private void Redraw() => surface.InvalidateVisual();

    private void RenderPages(DrawingContext context)
    {
        if (layout is null || pages.Count == 0) return;
        var viewport = new Rect2(offset.X, offset.Y, viewportWidth, viewportHeight);
        var visible = layout.VisiblePages(viewport);
        var shots = new List<PageShot>(visible.Count);
        foreach (int index in visible)
        {
            var page = pages[index];
            var transform = PageTransform(index);
            var b = bases.TryGetValue(page.Id, out var bb) && bb.Key.Setup.Equals(page.Setup) ? bb : default;
            var d = details.TryGetValue(page.Id, out var dd) && dd.Key.Setup.Equals(page.Setup) ? dd : default;
            shots.Add(new(index, page, transform, b.Image, d.Key, d.Image));
        }
        context.Custom(new CanvasDrawOperation(new Rect(0, 0, viewportWidth, viewportHeight), shots, this.Snapshot()));
    }

    /// <summary>One visible page as it is drawn this frame (immutable: the draw runs on the render thread).</summary>
    internal sealed record PageShot(int Index, Page Page, ViewTransform Transform, SKImage? Base, RenderKey? DetailKey, SKImage? Detail);

    /// <summary>What the render thread needs from the control for one frame.</summary>
    internal sealed record CanvasLook(bool DarkPages, double PageNumberSize, SKColor Paper, SKColor Shadow, SKColor Edge, SKColor PageNumber,
        PagePainter? Overlay, PagePainter? Preview, int PreviewPage, PagePainter? Marks, PagePainter? MarkPreview);

    private CanvasLook Snapshot()
    {
        SKColor Color(string key, SKColor fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out var v) && v is ISolidColorBrush brush
                ? new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, (byte)(brush.Color.A * brush.Opacity)) : fallback;
        return new(darkPages, pageNumberSize, Color("Paper", SKColors.White), Color("Page.Shadow", new SKColor(0, 0, 0, 0x30)),
            Color("Page.Border", new SKColor(0xC8, 0xC8, 0xC8)), Color("Overlay.PageNumber", new SKColor(0x80, 0x80, 0x80, 0x99)),
            OverlayPainter, previewIsMark ? null : previewPainter, previewPage, MarksPainter, previewIsMark ? previewPainter : null);
    }
}
