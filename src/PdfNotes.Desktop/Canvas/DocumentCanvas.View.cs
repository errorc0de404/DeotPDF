using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Tools;

namespace PdfNotes.Desktop.Canvas;

/// <summary>The editor tools' view of the canvas (<see cref="ICanvasView"/>, D14c): hit tests, transforms and previews.</summary>
public sealed partial class DocumentCanvas : ICanvasView
{
    private int previewPage = -1;
    private PagePainter? previewPainter;
    private bool previewIsMark;

    /// <summary>Turns a tool's preview into a painter (the Render.Skia mark painter; set by the window).</summary>
    public Func<ICanvasPreview, PagePainter>? PreviewPainterFor { get; set; }

    double ICanvasView.DpiScale => DpiScale;
    Point2 ICanvasView.Offset => offset;

    public void SetHorizontalOffset(double value) => SetOffset(new(value, offset.Y));
    public void SetVerticalOffset(double value) => SetOffset(new(offset.X, value));

    public void SetPreview(int pageIndex, ICanvasPreview? preview, bool mark = false)
    {
        previewIsMark = mark;
        previewPage = preview is null ? -1 : pageIndex;
        previewPainter = preview is null || PreviewPainterFor is null ? null : PreviewPainterFor(preview);
        Redraw();
    }

    public void RefreshPreview() => Redraw();
    public void RefreshOverlays() => Redraw();
    public void RefreshOverlay(int pageIndex) => Redraw();
}
