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
        previewIsMark = mark;
        previewPage = newPreview is null ? -1 : pageIndex;
        preview = newPreview;
        Redraw();
    }

    public void RefreshPreview() => Redraw();
    public void RefreshOverlays() => Redraw();
    public void RefreshOverlay(int pageIndex) => Redraw();

    /// <summary>The preview of this frame as a painter over a fixed list of marks (null without one).</summary>
    private PagePainter? PreviewPainter(int index, Page page, ViewTransform transform)
    {
        if (preview is null || index != previewPage) return null;
        var marks = preview.Marks(index, page, transform).ToArray();
        var colors = MarkColors;
        return (canvas, _, _, t) => SkiaMarkPainter.Paint(canvas, marks, t, colors);
    }
}
