using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Tools;
using PdfNotes.Render.Skia;
using Page = PdfNotes.Core.Models.Page;

namespace PdfNotes.UI.Canvas;

/// <summary>
/// The editor tools' view of the canvas (<see cref="ICanvasView"/>, D14c): hit tests, transforms and previews. A tool's
/// preview marks are taken on the UI thread when a frame is built (tools change them while the pointer moves) and drawn
/// by <see cref="SkiaMarkPainter"/> on the render thread.
/// </summary>
public sealed partial class DocumentCanvas : ICanvasView
{
    private int previewPage = -1;
    private ICanvasPreview? preview;
    private bool previewIsMark;
    private readonly StrokeLayer strokeLayer = new();

    /// <summary>Colors of the marks on the paper (the window passes its theme resources).</summary>
    public MarkColors MarkColors { get; set; } = MarkColors.Default;

    /// <summary>
    /// Screen marks of a page for this frame (selection, search, text selection), taken on the UI thread; null for none.
    /// Drawn above the content and never inverted.
    /// </summary>
    public Func<int, Page, ViewTransform, PagePainter?>? MarksFor { get; set; }

    double ICanvasView.DpiScale => DpiScale;
    Point2 ICanvasView.Offset => offset;

    public void SetHorizontalOffset(double value) => SetOffset(new(value, offset.Y));
    public void SetVerticalOffset(double value) => SetOffset(new(offset.X, value));

    public void SetPreview(int pageIndex, ICanvasPreview? newPreview, bool mark = false)
    {
        // The same preview again (the eraser circle on every move): only the preview is drawn again, the rest of the
        // frame is kept (roadmap P5d A1).
        if (ReferenceEquals(newPreview, preview) && previewIsMark == mark && previewPage == (newPreview is null ? -1 : pageIndex))
        {
            RedrawPreview();
            return;
        }
        previewIsMark = mark;
        previewPage = newPreview is null ? -1 : pageIndex;
        preview = newPreview;
        Redraw();
    }

    public void RefreshPreview() => RedrawPreview();
    public void RefreshLive() => RedrawPreview();

    /// <summary>
    /// The annotations a drag is changing (roadmap P5d A2): the overlay leaves them out and <see cref="LiveFor"/> paints
    /// them, so the frame cache keeps everything else while they move. A change of this set makes the cache again.
    /// </summary>
    public Func<IReadOnlyCollection<Guid>>? LiveIds { get; set; }

    /// <summary>
    /// The live annotations of a page as they look now (drawn inside the page, above the overlay). The set is
    /// <see cref="LiveIds"/>, asked once per frame (plan C6) and given to <see cref="OverlayFor"/> too.
    /// </summary>
    public Func<int, Page, ViewTransform, IReadOnlyCollection<Guid>, PagePainter?>? LiveFor { get; set; }

    private readonly HashSet<Guid> lastLive = [];

    /// <summary>Whether a drag changes annotations now; the set changing means the kept frame is out of date.</summary>
    private bool UpdateLive()
    {
        var live = LiveIds?.Invoke() ?? [];
        if (!lastLive.SetEquals(live))
        {
            lastLive.Clear();
            lastLive.UnionWith(live);
            baseVersion++;
        }
        return lastLive.Count > 0;
    }
    public void RefreshOverlays() => Redraw();
    public void RefreshOverlay(int pageIndex) => Redraw();

    /// <summary>The preview of this frame as a painter over a fixed list of marks (null without one).</summary>
    private PagePainter? PreviewPainter(int index, Page page, ViewTransform transform)
    {
        if (preview is null || index != previewPage) return null;
        var marks = preview.Marks(index, page, transform).ToArray();
        var colors = MarkColors;
        var strokes = strokeLayer; // drawn on the render thread only
        return (canvas, _, _, t) => SkiaMarkPainter.Paint(canvas, marks, t, colors, strokes);
    }
}
