using System.Collections.Immutable;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>
/// Table edits (1.4): each returns a new table with the same id and transform; the caller makes one
/// <c>DocumentEdits.UpdateAnnotations</c> call per edit, so one edit is one Undo step. Sizes are clamped to the table
/// minimums; new cells are empty and keep a neighbour's typing style as a zero-length run (<see cref="TableCell.Empty"/>).
/// </summary>
public static class TableEdits
{
    /// <summary>
    /// Inserts an empty row at <paramref name="at"/> (0..Rows) with <see cref="TableAnnotation.DefaultRowHeight"/>. Each new
    /// cell takes the style of the first cell of the row above (the row below when inserting at the top). A row inserted
    /// at the top of a table with a header row becomes the header and is bold, and the old first row loses the header
    /// bold (as <see cref="SetHeader"/> off); a row inserted under the header is not bold.
    /// </summary>
    public static TableAnnotation InsertRow(TableAnnotation t, int at)
    {
        if ((uint)at > (uint)t.Rows) throw new ArgumentOutOfRangeException(nameof(at));
        int source = at == 0 ? 0 : at - 1;
        var style = StyleOf(t, source, 0);
        if (style is not null && t.Style.HeaderRow && (at == 0 || source == 0)) style = style with { Bold = at == 0 };
        var cell = NewCell(style);
        var cells = t.Cells.InsertRange(at * t.Columns, Enumerable.Repeat(cell, t.Columns));
        if (at == 0 && t.Style.HeaderRow) cells = BoldRow(cells, t.Columns, 1, false);
        return t with { RowHeights = t.RowHeights.Insert(at, TableAnnotation.DefaultRowHeight), Cells = cells };
    }

    /// <summary>
    /// Inserts an empty column at <paramref name="at"/> (0..Columns) with <see cref="TableAnnotation.DefaultColumnWidth"/>.
    /// In each row the new cell takes the style of the cell on its left (the cell on its right when inserting first), so
    /// a header row stays bold.
    /// </summary>
    public static TableAnnotation InsertColumn(TableAnnotation t, int at)
    {
        if ((uint)at > (uint)t.Columns) throw new ArgumentOutOfRangeException(nameof(at));
        int source = at == 0 ? 0 : at - 1;
        var cells = ImmutableArray.CreateBuilder<TableCell>(t.Rows * (t.Columns + 1));
        for (int r = 0; r < t.Rows; r++)
            for (int c = 0; c <= t.Columns; c++)
                cells.Add(c == at ? NewCell(StyleOf(t, r, source)) : t.Cell(r, c < at ? c : c - 1));
        return t with { ColumnWidths = t.ColumnWidths.Insert(at, TableAnnotation.DefaultColumnWidth), Cells = cells.MoveToImmutable() };
    }

    /// <summary>
    /// Removes a row; null when it is the last one (the caller deletes the table). Removing the first row of a table with
    /// a header row makes the new first row the bold header.
    /// </summary>
    public static TableAnnotation? DeleteRow(TableAnnotation t, int row)
    {
        if ((uint)row >= (uint)t.Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if (t.Rows == 1) return null;
        var cells = t.Cells.RemoveRange(row * t.Columns, t.Columns);
        if (row == 0 && t.Style.HeaderRow) cells = BoldRow(cells, t.Columns, 0, true);
        return t with { RowHeights = t.RowHeights.RemoveAt(row), Cells = cells };
    }

    /// <summary>Removes a column; null when it is the last one (the caller deletes the table).</summary>
    public static TableAnnotation? DeleteColumn(TableAnnotation t, int column)
    {
        if ((uint)column >= (uint)t.Columns) throw new ArgumentOutOfRangeException(nameof(column));
        if (t.Columns == 1) return null;
        var cells = t.Cells.Where((_, i) => i % t.Columns != column).ToImmutableArray();
        return t with { ColumnWidths = t.ColumnWidths.RemoveAt(column), Cells = cells };
    }

    /// <summary>Sets a column width, at least <see cref="TableAnnotation.MinColumnWidth"/>.</summary>
    public static TableAnnotation ResizeColumn(TableAnnotation t, int column, double width)
    {
        if ((uint)column >= (uint)t.Columns) throw new ArgumentOutOfRangeException(nameof(column));
        return t with { ColumnWidths = t.ColumnWidths.SetItem(column, Clamp(width, TableAnnotation.MinColumnWidth, nameof(width))) };
    }

    /// <summary>Sets a row's minimum height, at least <see cref="TableAnnotation.MinRowHeight"/>.</summary>
    public static TableAnnotation ResizeRow(TableAnnotation t, int row, double height)
    {
        if ((uint)row >= (uint)t.Rows) throw new ArgumentOutOfRangeException(nameof(row));
        return t with { RowHeights = t.RowHeights.SetItem(row, Clamp(height, TableAnnotation.MinRowHeight, nameof(height))) };
    }

    /// <summary>Replaces one cell.</summary>
    public static TableAnnotation SetCell(TableAnnotation t, int row, int column, TableCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if ((uint)row >= (uint)t.Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)column >= (uint)t.Columns) throw new ArgumentOutOfRangeException(nameof(column));
        return t with { Cells = t.Cells.SetItem(row * t.Columns + column, cell) };
    }

    /// <summary>
    /// Selection-handle resize: column widths × <paramref name="sx"/>, row heights × <paramref name="sy"/>, each kept at
    /// least its minimum. Font sizes, line width and transform stay (page scaling uses <c>TableGeometry.ScaledText</c>).
    /// </summary>
    public static TableAnnotation Scaled(TableAnnotation t, double sx, double sy)
    {
        if (!double.IsFinite(sx) || sx <= 0) throw new ArgumentOutOfRangeException(nameof(sx));
        if (!double.IsFinite(sy) || sy <= 0) throw new ArgumentOutOfRangeException(nameof(sy));
        return t with
        {
            ColumnWidths = [.. t.ColumnWidths.Select(w => Clamp(w * sx, TableAnnotation.MinColumnWidth, nameof(sx)))],
            RowHeights = [.. t.RowHeights.Select(h => Clamp(h * sy, TableAnnotation.MinRowHeight, nameof(sy)))],
        };
    }

    /// <summary>Turns the header row on or off; the first row's runs (including empty cells' typing style) become bold or not.</summary>
    public static TableAnnotation SetHeader(TableAnnotation t, bool on) =>
        t with { Style = t.Style with { HeaderRow = on }, Cells = BoldRow(t.Cells, t.Columns, 0, on) };

    /// <summary>Sets <c>Bold</c> on every run of one row (including empty cells' typing style).</summary>
    private static ImmutableArray<TableCell> BoldRow(ImmutableArray<TableCell> cells, int columns, int row, bool on)
    {
        var b = cells.ToBuilder();
        for (int c = row * columns; c < (row + 1) * columns; c++)
            b[c] = b[c] with { Runs = [.. b[c].Runs.Select(r => r with { Bold = on })] };
        return b.MoveToImmutable();
    }

    private static double Clamp(double value, double min, string name) =>
        double.IsNaN(value) ? throw new ArgumentOutOfRangeException(name) : Math.Clamp(value, min, ModelLimits.MaxLengthPt);

    /// <summary>The first run of the given cell, else the first run in its row, else any run in the table; null when no cell has one.</summary>
    private static TextRun? StyleOf(TableAnnotation t, int row, int column)
    {
        var cell = t.Cell(row, column);
        if (cell.Runs.Length > 0) return cell.Runs[0];
        for (int c = 0; c < t.Columns; c++)
            if (t.Cell(row, c).Runs is { Length: > 0 } runs) return runs[0];
        return t.Cells.FirstOrDefault(x => x.Runs.Length > 0)?.Runs[0];
    }

    private static TableCell NewCell(TextRun? style) => style is null ? new("", [], TextAlignment.Left, []) : TableCell.Empty(style);
}
