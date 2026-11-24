using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Text;

/// <summary>
/// Table layout with the registered text engine (1.4): <see cref="TableGeometry"/> measured by <see cref="TextLayout.Bounds"/>,
/// the provider every caller of <see cref="AnnotationGeometry"/> passes, so the screen, the PDF appearance, hit tests and the
/// lasso see the same cell boxes and row heights (and share TableGeometry's per-table cache).
/// </summary>
public static class TableLayout
{
    /// <summary>Line positions (Columns + 1, Rows + 1) and the bounds in table-local points. Row height = max(set height, tallest cell box).</summary>
    public static TableGeometry.Result Measure(TableAnnotation table) => TableGeometry.Measure(table, TextLayout.Bounds);

    /// <summary>The cell as a text box on the page (deterministic ID, the table's transform at the cell corner, column width).</summary>
    public static TextBoxAnnotation CellBox(TableAnnotation table, int row, int column) =>
        TableGeometry.CellBox(table, Measure(table), row, column);

    /// <summary>The style typing in the cell starts with (an empty cell box has no runs).</summary>
    public static TextRun CellStyle(TableAnnotation table, int row, int column) => TableGeometry.CellStyle(table, row, column);

    /// <summary>The cell a cell box holds; an empty box keeps <paramref name="emptyStyle"/> (the editor's typing style, or <see cref="CellStyle"/>).</summary>
    public static TableCell FromBox(TextBoxAnnotation box, TextRun emptyStyle) => TableGeometry.FromBox(box, emptyStyle);

    public static (int Row, int Column)? CellAt(TableAnnotation table, Point2 local) => TableGeometry.CellAt(Measure(table), local);

    public static (bool Column, int Index)? BoundaryAt(TableAnnotation table, Point2 local, double tolerance) =>
        TableGeometry.BoundaryAt(Measure(table), local, tolerance);
}
