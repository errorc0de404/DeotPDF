using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Text;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Stroke and area eraser (spec 7.3–7.4). The circle under the mouse is the real erase range in page points, so it
/// grows with the zoom. Pressing starts an <see cref="EraseGesture"/> on that page; the overlay shows the result live
/// (<see cref="Preview"/>) without touching the document, and releasing commits the whole rub as one Undo unit.
/// Esc, another button or lost capture discards it.
/// </summary>
public sealed class EraserTool(ICanvasView view, Func<DocumentSession?> session, Func<double> diameterPt, EraserMode mode) : ICanvasTool, IHoverTool, ICanvasPreview
{
    private EraseGesture? gesture;
    private int pageIndex = -1;
    private int circlePage = -1;
    private Point2 circleCenter;

    public EraserMode Mode { get; } = mode;
    private bool overLocked;

    /// <summary>A cross, or the lock over a locked annotation, which the eraser leaves (spec 5.2, 7.3).</summary>
    public ToolCursor? Cursor => overLocked && gesture is null ? ToolCursor.Lock(view.DpiScale) : ToolCursor.Cross;
    public bool IsActive => gesture is not null;

    /// <summary>Raised after an erase was committed.</summary>
    public event EventHandler? Committed;

    /// <summary>The live result for annotations of <paramref name="pageId"/> touched by the gesture in progress, or null.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<Annotation>>? Preview(Guid pageId) =>
        gesture is { } g && g.PageId == pageId ? g.Preview : null;

    public bool PointerDown(CanvasPointer pointer)
    {
        if (session() is not { } s || view.HitTestPage(pointer.Position) is not { } anchor) return false;
        pageIndex = anchor.PageIndex;
        gesture = new EraseGesture(s.Current, s.Current.Pages[pageIndex].Id, Mode, Diameter,
            box => TextLayout.HighlightLines(box, TextLayout.Measure(box).Characters));
        Extend(anchor.PagePoint);
        return true;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        if (gesture is null) return;
        var p = view.PageTransform(pageIndex).DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        Extend(p);
    }

    public void PointerUp(CanvasPointer pointer)
    {
        if (gesture is null) return;
        PointerMove(pointer);
        var command = gesture.Commit();
        gesture = null;
        view.RefreshOverlay(pageIndex);
        if (!command.IsEmpty && session() is { } s && s.Execute(command)) Committed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        if (gesture is null) return;
        gesture = null;
        view.RefreshOverlay(pageIndex);
    }

    public void PointerHover(Point2? position, InputModifiers modifiers)
    {
        overLocked = false;
        if (position is { } p && view.HitTestPage(p) is { } anchor)
        {
            double tolerance = view.PageTransform(anchor.PageIndex).DipLengthToPoints(SelectTool.PickToleranceDip);
            overLocked = session() is { } s && s.Current.Pages[anchor.PageIndex].Annotations.LastOrDefault(a =>
                AnnotationGeometry.Hit(a, anchor.PagePoint, tolerance, TextLayout.Bounds)) is { Locked: true };
            ShowCircle(anchor.PageIndex, anchor.PagePoint);
        }
        else if (circlePage >= 0)
        {
            circlePage = -1;
            view.SetPreview(-1, null);
        }
    }

    private double Diameter => double.IsFinite(diameterPt()) ? Math.Clamp(diameterPt(), EraserSizes.Min, EraserSizes.Max) : EraserSizes.Default(Mode);

    private void Extend(Point2 pagePoint)
    {
        ShowCircle(pageIndex, pagePoint);
        if (gesture!.AddPoint(pagePoint)) view.RefreshOverlay(pageIndex);
    }

    private void ShowCircle(int index, Point2 center)
    {
        circleCenter = center;
        circlePage = index;
        view.SetPreview(index, this, mark: true); // no-op when already shown there
        view.RefreshPreview();
    }

    IEnumerable<CanvasMark> ICanvasPreview.Marks(int index, Page page, ViewTransform transform)
    {
        if (index != circlePage) yield break;
        yield return new EraserCircleMark(circleCenter, Diameter);
    }
}
