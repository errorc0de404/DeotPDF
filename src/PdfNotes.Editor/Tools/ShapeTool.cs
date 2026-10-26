using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Shape tool (spec 8 preface, D11). Rectangle, ellipse and triangle are dragged from corner to corner (Shift: square
/// and circle); a drag shorter than 4 screen pixels draws nothing. The free polygon adds a corner per click and is
/// finished by a double-click, a click on its first corner or Enter; Esc, the right button or another tool discard it.
/// Shapes are axis-aligned on the shown page (their local frame is the rotated display frame). A new shape is one
/// Undo unit and becomes the selection, so its style can be changed at once. A click (no drag) on an existing unlocked
/// shape or line hands it to <paramref name="select"/> for editing with the selection tool (spec 5.4, D12); a drag that
/// starts on it still draws, and the clicks of a polygon in progress stay corners.
/// </summary>
public sealed class ShapeTool(ICanvasView view, Func<DocumentSession?> session, SelectTool selection, Func<ShapeKind> kind, Func<ShapeStyle> style,
    Action<Guid> select) : ICanvasTool, IHoverTool, IMultiClickTool, ICanvasPreview
{
    public const double MinDragDip = 4, CloseDistanceDip = 8;

    /// <summary>What a click of the shape group tools (line, curve, shape) selects (spec 5.4, 2026-10-13 user request).</summary>
    public static bool IsShapeGroup(Annotation annotation) => annotation is LineAnnotation or ShapeAnnotation;

    private int pageIndex = -1;
    private Guid pageId;
    private PageFrame frame;
    private Point2 start, current;
    private Point2 startDip;
    private double reachDip;
    private bool dragging, square, editing;
    private Guid? pressed; // existing shape under the first press
    private readonly List<Point2> vertices = [];

    public ToolCursor? Cursor => editing ? selection.Cursor : ToolCursor.Cross;
    public bool IsActive => dragging || editing || vertices.Count > 0;

    /// <summary>True while a free polygon has corners but is not finished.</summary>
    public bool IsBuildingPolygon => vertices.Count > 0;

    public bool PointerDown(CanvasPointer pointer)
    {
        if (vertices.Count > 0) return AddCorner(pointer);
        // The just-drawn shape keeps its handles: dragging them resizes or rotates it like the selection tool.
        if (selection.HitSelection(pointer.Position) == SelectionHit.Handle)
        {
            editing = selection.PointerDown(pointer with { Modifiers = pointer.Modifiers & ~InputModifiers.Shift });
            return editing;
        }
        if (session() is not { } s || view.HitTestPage(pointer.Position) is not { } at) return false;
        pageIndex = at.PageIndex;
        var page = s.Current.Pages[pageIndex];
        pageId = page.Id;
        frame = new PageFrame(page.Setup.WidthPt, page.Setup.HeightPt, page.Setup.Rotation);
        start = current = frame.CanonicalToDisplay.Transform(at.PagePoint);
        startDip = pointer.Position; reachDip = 0;
        pressed = selection.Pick(pointer.Position) is { } hit && page.Annotations.FirstOrDefault(a => a.Id == hit) is { } under && IsShapeGroup(under) ? hit : null;
        selection.Select(null);
        if (kind() == ShapeKind.Polygon) vertices.Add(start);
        else dragging = true;
        view.SetPreview(pageIndex, this);
        return true;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        if (editing) { selection.PointerMove(pointer); return; }
        if (pageIndex < 0) return;
        current = Local(pointer.Position);
        square = pointer.Modifiers.HasFlag(InputModifiers.Shift);
        reachDip = Math.Max(reachDip, pointer.Position.DistanceTo(startDip));
        view.RefreshPreview();
    }

    public void PointerUp(CanvasPointer pointer)
    {
        if (editing) { editing = false; selection.PointerUp(pointer); return; }
        if (!dragging)
        {
            // A polygon corner: the tool stays active for the next click. Its first press, kept still on a shape, selects that shape.
            if (vertices.Count == 1 && pressed is { } first && reachDip < MinDragDip) { Reset(); select(first); }
            return;
        }
        PointerMove(pointer);
        dragging = false;
        if (reachDip < MinDragDip)
        {
            var clicked = pressed;
            Reset();
            if (clicked is { } id) select(id); // edit the shape that was clicked
            return;
        }
        var box = ShapeGeometry.BoxBetween(start, current, square);
        Commit(new ShapeAnnotation(Guid.NewGuid(), false, frame.DisplayToCanonical, kind(), box, [], style()));
    }

    public void PointerHover(Point2? position, InputModifiers modifiers)
    {
        if (vertices.Count == 0 || position is not { } p || pageIndex < 0) return;
        current = Local(p);
        view.RefreshPreview();
    }

    public void Cancel()
    {
        if (editing) { editing = false; selection.Cancel(); }
        Reset();
    }

    /// <summary>Enter: finishes a polygon with at least three corners (fewer: discarded).</summary>
    public void Finish()
    {
        var corners = Distinct(vertices);
        if (corners.Count >= 3 && pageIndex >= 0)
        {
            var points = corners.ToImmutableArray();
            Commit(new ShapeAnnotation(Guid.NewGuid(), false, frame.DisplayToCanonical, ShapeKind.Polygon, default, points, style()));
        }
        else Reset();
    }

    private bool AddCorner(CanvasPointer pointer)
    {
        var p = Local(pointer.Position);
        var first = view.PageTransform(pageIndex).PageToDip.Transform(frame.DisplayToCanonical.Transform(vertices[0]));
        bool closing = Distinct(vertices).Count >= 3 && first.DistanceTo(new(pointer.Position.X, pointer.Position.Y)) <= CloseDistanceDip;
        if (pointer.ClickCount >= 2 || closing) { Finish(); return false; }
        vertices.Add(p);
        current = p;
        view.RefreshPreview();
        return true;
    }

    /// <summary>Corners without repeats (a double-click puts its first click on the last corner).</summary>
    private static List<Point2> Distinct(List<Point2> points)
    {
        var result = new List<Point2>();
        foreach (var p in points)
            if (result.Count == 0 || result[^1].DistanceTo(p) > 0.01) result.Add(p);
        if (result.Count > 1 && result[0].DistanceTo(result[^1]) <= 0.01) result.RemoveAt(result.Count - 1);
        return result;
    }

    private Point2 Local(Point2 viewportPoint) =>
        frame.CanonicalToDisplay.Transform(view.PageTransform(pageIndex).DipToPage.Transform(new(viewportPoint.X, viewportPoint.Y)));

    private void Commit(ShapeAnnotation shape)
    {
        var target = pageId;
        Reset();
        if (session() is not { } s || s.Current.IndexOfPage(target) < 0 || ModelValidator.ValidateAnnotation(shape, _ => false) is not null) return;
        if (s.Execute(DocumentEdits.AddAnnotation(s.Current, target, shape, label: "undo.shape"))) selection.Select(shape.Id);
    }

    private void Reset()
    {
        pageIndex = -1; dragging = false; pressed = null;
        vertices.Clear();
        view.SetPreview(-1, null);
    }

    IEnumerable<CanvasMark> ICanvasPreview.Marks(int index, Page page, ViewTransform transform)
    {
        if (index != pageIndex) yield break;
        if (dragging)
        {
            var box = ShapeGeometry.BoxBetween(start, current, square);
            if (box.Width > 0 && box.Height > 0)
                yield return new AnnotationMark(new ShapeAnnotation(Guid.Empty, false, frame.DisplayToCanonical, kind(), box, [], style()));
            yield break;
        }
        var corners = Distinct(vertices);
        if (corners.Count >= 2)
            yield return new AnnotationMark(new ShapeAnnotation(Guid.Empty, false, frame.DisplayToCanonical, ShapeKind.Polyline, default, [.. corners],
                style() with { BorderEnabled = true }));
        yield return new RubberBandMark(frame.DisplayToCanonical, vertices[^1], current, vertices[0]);
    }
}
