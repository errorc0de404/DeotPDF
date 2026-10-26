using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Text tool (spec 6.1, 5.4). A click or drag on empty page space creates a box (drag: its width). A press on an existing
/// unlocked box goes to <paramref name="select"/>, which selects it with the selection tool and continues the press there
/// (a drag moves it); it returns whether that press is now in progress.
/// </summary>
public sealed class TextTool(ICanvasView view, Action<int, Point2, double> create, Func<CanvasPointer, bool> select) : ICanvasTool
{
    private PageAnchor? start;
    public ToolCursor? Cursor => ToolCursor.IBeam;
    public bool IsActive => start is not null;
    public bool PointerDown(CanvasPointer pointer)
    {
        start = null;
        if (select(pointer)) return true;
        start = view.HitTestPage(pointer.Position); return IsActive;
    }
    public void PointerMove(CanvasPointer pointer) { }
    public void PointerUp(CanvasPointer pointer)
    {
        var anchor = start; start = null;
        if (anchor is not { } a) return;
        var end = view.PageTransform(a.PageIndex).DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        double width = Math.Abs(end.X - a.PagePoint.X);
        create(a.PageIndex, width >= 8 ? new(Math.Min(end.X, a.PagePoint.X), a.PagePoint.Y) : a.PagePoint, width >= 8 ? width : 180);
    }
    public void Cancel() => start = null;
}
