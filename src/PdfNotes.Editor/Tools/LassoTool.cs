using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Lasso selection (spec 5.4–5.5). The path is drawn freely and closed back to its start; on release the unlocked
/// annotations with any painted area inside (even-odd) are selected. A drag shorter than 4 screen pixels is a click
/// that picks the annotation under it. Shift adds to the selection of the same page. A non-empty result raises
/// <see cref="Selected"/> (the window switches to the selection tool). Pressing on the current selection — a handle,
/// a selected annotation or inside its frame — moves, scales or rotates it through the selection tool.
/// </summary>
public sealed class LassoTool(ICanvasView view, Func<DocumentSession?> session, SelectTool selection) : ICanvasTool, IHoverTool, ICanvasPreview
{
    private ToolCursor? overSelection;

    private readonly List<Point2> path = [];
    private int pageIndex = -1;
    private Guid pageId;
    private Point2 start;
    private double reachDip;
    private bool additive, editing;

    public ToolCursor? Cursor => editing ? selection.Cursor : pageIndex < 0 && overSelection is { } c ? c : ToolCursor.Cross;

    /// <summary>Over the current selection the cursor shows what a press does there (move, resize, rotate).</summary>
    public void PointerHover(Point2? position, InputModifiers modifiers) =>
        overSelection = position is { } p ? selection.CursorAt(p) ?? (selection.OverLocked(p) ? ToolCursor.Lock(view.DpiScale) : null) : null;
    public bool IsActive => pageIndex >= 0 || editing;

    /// <summary>Raised after a lasso or click selected at least one annotation.</summary>
    public event EventHandler? Selected;

    public bool PointerDown(CanvasPointer pointer)
    {
        switch (selection.HitSelection(pointer.Position))
        {
            case SelectionHit.Handle or SelectionHit.Annotation:
                editing = selection.PointerDown(pointer with { Modifiers = pointer.Modifiers & ~InputModifiers.Shift });
                return editing;
            case SelectionHit.Frame:
                editing = selection.BeginMove(pointer.Position);
                return editing;
        }
        if (session() is not { } s || view.HitTestPage(pointer.Position) is not { } anchor) return false;
        pageIndex = anchor.PageIndex;
        pageId = s.Current.Pages[pageIndex].Id;
        additive = pointer.Modifiers.HasFlag(InputModifiers.Shift);
        start = pointer.Position;
        reachDip = 0;
        path.Clear();
        path.Add(anchor.PagePoint);
        view.SetPreview(pageIndex, this, mark: true);
        return true;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        if (editing) { selection.PointerMove(pointer); return; }
        if (pageIndex < 0) return;
        var transform = view.PageTransform(pageIndex);
        var p = transform.DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        reachDip = Math.Max(reachDip, pointer.Position.DistanceTo(start));
        if (p.DistanceTo(path[^1]) < transform.DevicePixelLengthToPoints(StrokeTool.MinStepPixels)) return;
        path.Add(p);
        view.RefreshPreview();
    }

    public void PointerUp(CanvasPointer pointer)
    {
        if (editing) { editing = false; selection.PointerUp(pointer); return; }
        if (pageIndex < 0) return;
        PointerMove(pointer);
        var points = path.ToArray();
        Guid target = pageId;
        bool click = reachDip < Lasso.MinDragDip;
        Reset();
        if (session() is not { } s || s.Current.IndexOfPage(target) is not (>= 0 and var index)) return;
        IEnumerable<Guid> chosen = click
            ? selection.Pick(pointer.Position) is { } id ? [id] : []
            : Lasso.Select(s.Current.Pages[index], points, PdfNotes.Text.TextLayout.Bounds);
        if (additive && selection.PageId == target) chosen = selection.SelectedIds.Concat(chosen).ToArray();
        selection.SetSelection(chosen);
        if (selection.SelectedIds.Count > 0) Selected?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        if (editing) { editing = false; selection.Cancel(); }
        Reset();
    }

    private void Reset()
    {
        pageIndex = -1;
        path.Clear();
        view.SetPreview(-1, null);
    }

    /// <summary>The path so far, closed from the last point back to the start like the selection will be.</summary>
    IEnumerable<CanvasMark> ICanvasPreview.Marks(int index, Page page, ViewTransform transform)
    {
        if (index != pageIndex || path.Count < 2) yield break;
        yield return new LassoMark(path.ToArray());
    }
}
