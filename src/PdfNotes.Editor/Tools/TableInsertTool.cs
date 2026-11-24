using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Session;
using PdfNotes.Text;

namespace PdfNotes.Editor.Tools;

/// <summary>Rows × columns of a table to insert (the 표 grid, at most <see cref="TableAnnotation.MaxPick"/> each).</summary>
public readonly record struct TableSize(int Rows, int Columns);

/// <summary>
/// The armed table insert (1.4 plan 1.3 넣기): a click (or a tap) on a page puts a table of the armed size there, its top-left
/// corner at the click and pulled inside the page, as one Undo unit (<c>undo.addTable</c>); <paramref name="placed"/> then
/// selects it with the selection tool. One table per arming: <see cref="ToolSwitch.Table"/> holds the size, and Esc or another
/// tool clears it. Cell text starts in <paramref name="style"/> (the text box default style).
/// </summary>
public sealed class TableInsertTool(ICanvasView view, Func<DocumentSession?> session, Func<TableSize?> armed, Func<TextRun> style, Action<Guid> placed)
    : ICanvasTool
{
    private PageAnchor? start;

    public ToolCursor? Cursor => ToolCursor.Cross;
    public bool IsActive => start is not null;

    public bool PointerDown(CanvasPointer pointer)
    {
        start = armed() is not null && session() is not null ? view.HitTestPage(pointer.Position) : null;
        return IsActive;
    }

    public void PointerMove(CanvasPointer pointer) { }

    public void PointerUp(CanvasPointer pointer)
    {
        var anchor = start; start = null;
        if (anchor is not { } a || armed() is not { } size || session() is not { } s) return;
        var model = s.Current;
        if ((uint)a.PageIndex >= (uint)model.Pages.Count) return;
        var page = model.Pages[a.PageIndex];
        var table = Place(page, a.PagePoint, size, style());
        s.Execute(DocumentEdits.AddAnnotation(model, page.Id, table, label: "undo.addTable"));
        placed(table.Id);
    }

    public void Cancel() => start = null;

    /// <summary>
    /// A new table of <paramref name="size"/> with its top-left corner at <paramref name="at"/>, moved so its bounds (outer
    /// lines included) stay on the page; along a side where the table is larger than the page, it starts at the page's edge.
    /// </summary>
    public static TableAnnotation Place(Page page, Point2 at, TableSize size, TextRun style)
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), at, size.Rows, size.Columns, style);
        var bounds = TableLayout.Measure(table).Bounds;
        double x = Inside(at.X, bounds.X, bounds.Width, page.Setup.WidthPt);
        double y = Inside(at.Y, bounds.Y, bounds.Height, page.Setup.HeightPt);
        return x == at.X && y == at.Y ? table : table with { Transform = Affine.Translation(x, y) };
    }

    /// <summary>The origin that keeps [origin + start, origin + start + length] inside [0, page], or at 0 when it does not fit.</summary>
    private static double Inside(double origin, double start, double length, double page) =>
        length >= page ? -start : Math.Clamp(origin, -start, page - start - length);
}
