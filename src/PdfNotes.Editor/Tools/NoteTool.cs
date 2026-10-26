using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Sticky note tool (spec 24.9): a click on the page puts a note icon there and opens it for typing. A press on an
/// existing note goes to <paramref name="select"/> (the selection tool selects it and continues the press: a drag moves it).
/// </summary>
public sealed class NoteTool(ICanvasView view, Action<int, Point2> create, Func<CanvasPointer, bool> select) : ICanvasTool
{
    private PageAnchor? start;
    public ToolCursor? Cursor => ToolCursor.Cross;
    public bool IsActive => start is not null;

    public bool PointerDown(CanvasPointer pointer)
    {
        start = null;
        if (select(pointer)) return true;
        start = view.HitTestPage(pointer.Position);
        return IsActive;
    }

    public void PointerMove(CanvasPointer pointer) { }

    public void PointerUp(CanvasPointer pointer)
    {
        var anchor = start; start = null;
        if (anchor is { } a) create(a.PageIndex, a.PagePoint);
    }

    public void Cancel() => start = null;
}
