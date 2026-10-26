using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App.Canvas;

/// <summary>Draws the editing overlay of one page. Coordinates: <c>transform</c> maps canonical page points to the page element.</summary>
public delegate void PageOverlayPainter(DrawingContext context, int pageIndex, Page page, ViewTransform transform);

/// <summary>
/// One realized page: the PDF/generated background bitmap(s) and, above it, a separate overlay layer for app
/// annotations and temporary marks. Only pages near the viewport exist; <see cref="DocumentView"/> recycles them.
/// Layers (D13i): the content group — paper and background bitmaps, annotations, the drawing preview — takes the
/// <c>Page.Effect</c> resource (the reading dark page inverts it, spec 24.12); the paper edge, the marks (selection,
/// search, text selection) and mark previews (eraser circle, lasso path) are drawn above it unchanged.
/// </summary>
public sealed class PageVisual : FrameworkElement
{
    // The paper is white in every theme; its shadow and edge follow the theme (Page.Shadow, Page.Border).
    private static readonly Brush PaperBrush = Theme.Brush("Paper");
    private static readonly Brush PaperTextBrush = Theme.Brush("Paper.Text");
    public static readonly DependencyProperty ShadowProperty = DependencyProperty.Register(nameof(Shadow), typeof(Brush), typeof(PageVisual),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EdgeProperty = DependencyProperty.Register(nameof(Edge), typeof(Brush), typeof(PageVisual),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((PageVisual)d).edgePen = null));
    private Pen? edgePen;
    public static readonly DependencyProperty PageEffectProperty = DependencyProperty.Register(nameof(PageEffect), typeof(Effect), typeof(PageVisual),
        new FrameworkPropertyMetadata(null, (d, e) => ((PageVisual)d).content.Effect = (Effect?)e.NewValue));
    private readonly ContainerVisual content = new();
    private readonly DrawingVisual background = new();
    private readonly DrawingVisual overlay = new();
    private readonly DrawingVisual preview = new();
    private readonly DrawingVisual frame = new();
    private readonly DrawingVisual marks = new();
    private readonly DrawingVisual markPreview = new();
    private PageOverlayPainter? painter;
    private PageOverlayPainter? marksPainter;
    private PageOverlayPainter? previewPainter;
    private bool previewIsMark;

    public PageVisual()
    {
        IsHitTestVisible = false;
        content.Children.Add(background);
        content.Children.Add(overlay);
        content.Children.Add(preview);
        AddVisualChild(content);
        AddVisualChild(frame);
        AddVisualChild(marks);
        AddVisualChild(markPreview);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SetResourceReference(ShadowProperty, "Page.Shadow");
        SetResourceReference(EdgeProperty, "Page.Border");
        SetResourceReference(PageEffectProperty, "Page.Effect");
    }

    /// <summary>Effect of the page content: none, or the reading dark page inversion (spec 24.12).</summary>
    public Effect? PageEffect { get => (Effect?)GetValue(PageEffectProperty); set => SetValue(PageEffectProperty, value); }

    public Brush? Shadow { get => (Brush?)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }
    public Brush? Edge { get => (Brush?)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }

    public int Index { get; private set; } = -1;
    public Page? Page { get; private set; }
    public double DipsPerPoint { get; private set; }
    public double DpiScale { get; private set; } = 1;
    public RenderKey? BaseKey { get; private set; }
    public RenderKey? DetailKey { get; private set; }
    public string? Error { get; private set; }
    public BitmapSource? BaseBitmap { get; private set; }
    private BitmapSource? detailBitmap;

    /// <summary>Canonical page points → this element's local DIPs.</summary>
    public ViewTransform LocalTransform => new(Page!.Setup.Frame, DipsPerPoint / Units.DipsPerPoint, DpiScale, new(0, 0));

    internal void Assign(int index, Page page, double dipsPerPoint, double dpiScale, PageOverlayPainter? overlayPainter, PageOverlayPainter? markPainter = null)
    {
        if (Index == index && ReferenceEquals(Page, page) && DipsPerPoint == dipsPerPoint && DpiScale == dpiScale && painter == overlayPainter && marksPainter == markPainter)
            return;
        marksPainter = markPainter;
        bool samePage = Page is not null && Page.Id == page.Id && Page.Setup.Equals(page.Setup);
        Index = index;
        Page = page;
        DipsPerPoint = dipsPerPoint;
        DpiScale = dpiScale;
        painter = overlayPainter;
        if (!samePage)
        {
            BaseKey = DetailKey = null;
            BaseBitmap = detailBitmap = null;
            Error = null;
        }
        InvalidateVisual();
        RefreshOverlay();
        RefreshPreview();
    }

    internal void Release()
    {
        Index = -1;
        Page = null;
        BaseKey = DetailKey = null;
        BaseBitmap = detailBitmap = null;
        Error = null;
        painter = null;
        marksPainter = null;
        previewPainter = null;
        foreach (var layer in new[] { background, overlay, preview, frame, marks, markPreview }) using (layer.RenderOpen()) { }
    }

    internal void SetBase(RenderKey key, BitmapSource bitmap)
    {
        BaseKey = key;
        BaseBitmap = bitmap;
        Error = null;
        InvalidateVisual();
    }

    internal void SetDetail(RenderKey? key, BitmapSource? bitmap)
    {
        DetailKey = key;
        detailBitmap = bitmap;
        InvalidateVisual();
    }

    internal void SetError(string error)
    {
        Error = error;
        InvalidateVisual();
    }

    internal void SetOverlayPainter(PageOverlayPainter? overlayPainter, PageOverlayPainter? markPainter)
    {
        painter = overlayPainter;
        marksPainter = markPainter;
        RefreshOverlay();
    }

    /// <summary>
    /// Not clipped here: the painter clips page content (annotations) to the page itself, while selection handles may
    /// lie outside the page — for example a curve's control point above the top edge must stay visible and draggable.
    /// </summary>
    public const int CachedOverlayAnnotations = 200;
    public const double MaxCachedOverlayPixels = 4096;

    public void RefreshOverlay()
    {
        using (var context = overlay.RenderOpen())
            if (painter is not null && Page is not null) painter(context, Index, Page, LocalTransform);
        // D13f: with many annotations WPF re-rasterized all of them on every pen move (the preview above them is in the
        // same group) — 1000 strokes took drawing to ~30 fps. A bitmap cache keeps them rasterized until this redraw.
        bool cache = Page is not null && Page.Annotations.Count >= CachedOverlayAnnotations && Math.Max(Width, Height) * DpiScale <= MaxCachedOverlayPixels;
        if (cache != overlay.CacheMode is BitmapCache) overlay.CacheMode = cache ? new BitmapCache() : null;
        using (var context = marks.RenderOpen())
            if (marksPainter is not null && Page is not null) marksPainter(context, Index, Page, LocalTransform);
    }

    /// <summary>
    /// Temporary input mark above the annotations (a stroke being drawn). Redrawn alone on every pointer move, so the
    /// background bitmap and the annotation overlay are not touched while drawing (spec 16.1).
    /// </summary>
    /// <param name="mark">A screen mark (eraser circle, lasso path) drawn above the content, never inverted (spec 24.12).</param>
    internal void SetPreview(PageOverlayPainter? previewOverlay, bool mark = false)
    {
        if (previewPainter == previewOverlay && previewIsMark == mark) return;
        if (previewIsMark != mark) using ((previewIsMark ? markPreview : preview).RenderOpen()) { }
        previewPainter = previewOverlay;
        previewIsMark = mark;
        RefreshPreview();
    }

    public void RefreshPreview()
    {
        using var context = (previewIsMark ? markPreview : preview).RenderOpen();
        if (previewPainter is null || Page is null) return;
        context.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height)));
        previewPainter(context, Index, Page, LocalTransform);
        context.Pop();
    }

    protected override int VisualChildrenCount => 4;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => content,
        1 => frame,
        2 => marks,
        3 => markPreview,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>The shadow is this element's own drawing; paper, bitmaps and the edge go to their layers.</summary>
    protected override void OnRender(DrawingContext shadow)
    {
        if (Page is null) return;
        var page = new Rect(0, 0, Width, Height);
        shadow.DrawRectangle(Shadow, null, new Rect(2, 2, page.Width, page.Height));
        using (var edge = frame.RenderOpen()) edge.DrawRectangle(null, edgePen ??= Freeze(new Pen(Edge, 1)), page);
        using var context = background.RenderOpen();
        context.DrawRectangle(PaperBrush, null, page);
        context.PushClip(new RectangleGeometry(page));
        // Bitmaps are drawn at their own resolution: a bitmap rendered at the current zoom maps 1:1 to device
        // pixels; an older one is stretched until the sharper result replaces it (spec 16.1).
        if (BaseBitmap is not null && BaseKey is not null)
            context.DrawImage(BaseBitmap, PixelRectToLocal(BaseKey, 0, 0, BaseBitmap.PixelWidth, BaseBitmap.PixelHeight));
        if (detailBitmap is not null && DetailKey?.Region is { } r)
            context.DrawImage(detailBitmap, PixelRectToLocal(DetailKey, r.X, r.Y, r.Width, r.Height));
        context.Pop();
        if (Error is not null)
        {
            var text = new FormattedText(Strings.Get("page.renderFailed"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Malgun Gothic"), 14, PaperTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            context.DrawText(text, new Point(Math.Max(4, (page.Width - text.Width) / 2), Math.Max(4, (page.Height - text.Height) / 2)));
        }
    }

    private Rect PixelRectToLocal(RenderKey key, int x, int y, int width, int height)
    {
        double scale = DipsPerPoint / key.PixelsPerPoint; // DIPs per bitmap pixel
        return new(x * scale, y * scale, width * scale, height * scale);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
