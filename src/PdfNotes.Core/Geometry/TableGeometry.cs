using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Geometry;

/// <summary>
/// Table geometry (1.4): each cell is a text box as wide as its column at the cell's top-left corner, so the text box's
/// own padding (<see cref="TableAnnotation.CellPadding"/>, the same 4pt) keeps its text off the lines. A row is as tall as
/// its set height or its tallest cell box, whichever is more. Text heights come from a text-bounds provider (Core does not
/// lay out text); PdfNotes.Text's <c>TableLayout</c> passes the real one, and the screen, the PDF and hit tests all use it.
/// </summary>
public static class TableGeometry
{
    /// <summary>Line positions in table-local points: <see cref="ColumnX"/> has Columns + 1 entries, <see cref="RowY"/> Rows + 1.</summary>
    public sealed record Result(ImmutableArray<double> ColumnX, ImmutableArray<double> RowY, Rect2 Bounds);

    public static Result Measure(TableAnnotation table, Func<TextBoxAnnotation, Rect2> textBounds)
    {
        ArgumentNullException.ThrowIfNull(textBounds);
        var columnX = new double[table.Columns + 1];
        for (int c = 0; c < table.Columns; c++) columnX[c + 1] = columnX[c] + table.ColumnWidths[c];
        var rowY = new double[table.Rows + 1];
        for (int r = 0; r < table.Rows; r++)
        {
            double height = table.RowHeights[r];
            for (int c = 0; c < table.Columns; c++) height = Math.Max(height, textBounds(MeasuredBox(table, r, c)).Height);
            rowY[r + 1] = rowY[r] + height;
        }
        return new([.. columnX], [.. rowY], new(0, 0, columnX[^1], rowY[^1]));
    }

    /// <summary>
    /// The cell as a text box placed on the page: deterministic ID (from the table ID and the cell), the table's transform
    /// moved to the cell's corner, width = column width. An empty cell gives a box without runs, like a new text box; its
    /// style is <see cref="CellStyle"/>.
    /// </summary>
    public static TextBoxAnnotation CellBox(TableAnnotation table, Result layout, int row, int column)
    {
        var cell = table.Cell(row, column);
        return new(CellId(table.Id, row, column), table.Locked, Affine.Translation(layout.ColumnX[column], layout.RowY[row]).Then(table.Transform),
            cell.Text, cell.Text.Length == 0 ? [] : cell.Runs, cell.Alignment, table.ColumnWidths[column], TextBoxStyle.Default,
            cell.Text.Length == 0 ? [] : cell.Highlights);
    }

    /// <summary>The style typing in the cell starts with: the empty cell's kept style, or the style of its last character.</summary>
    public static TextRun CellStyle(TableAnnotation table, int row, int column) =>
        (table.Cell(row, column).Runs.LastOrDefault() ?? TextEditing.Default) with { Start = 0, Length = 0 };

    /// <summary>
    /// The cell a (possibly edited) cell box holds. An empty box becomes <see cref="TableCell.Empty"/> with
    /// <paramref name="emptyStyle"/> (the editor's typing style), else the box's last run, else the default style.
    /// </summary>
    public static TableCell FromBox(TextBoxAnnotation box, TextRun? emptyStyle = null) => box.Text.Length == 0
        ? TableCell.Empty(emptyStyle ?? box.Runs.LastOrDefault() ?? TextEditing.Default) with { Alignment = box.Alignment }
        : new(box.Text, box.Runs, box.Alignment, box.Highlights);

    /// <summary>The cell under a table-local point; points on a line belong to the cell after it, the outer frame to the table.</summary>
    public static (int Row, int Column)? CellAt(Result layout, Point2 local)
    {
        int column = Slot(layout.ColumnX, local.X), row = Slot(layout.RowY, local.Y);
        return column < 0 || row < 0 ? null : (row, column);
    }

    /// <summary>
    /// The inner line within <paramref name="tolerance"/> of a table-local point, the nearer one where two cross:
    /// a column line (Column true) or a row line, with Index = the column or row before it (the one dragging it resizes).
    /// The outer frame is the selection frame's, so it is never returned.
    /// </summary>
    public static (bool Column, int Index)? BoundaryAt(Result layout, Point2 local, double tolerance)
    {
        var b = layout.Bounds;
        if (local.X < -tolerance || local.Y < -tolerance || local.X > b.Width + tolerance || local.Y > b.Height + tolerance) return null;
        var (column, dx) = Nearest(layout.ColumnX, local.X);
        var (row, dy) = Nearest(layout.RowY, local.Y);
        bool onColumn = column >= 0 && dx <= tolerance && local.Y >= 0 && local.Y <= b.Height;
        bool onRow = row >= 0 && dy <= tolerance && local.X >= 0 && local.X <= b.Width;
        if (onColumn && (!onRow || dx < dy)) return (true, column - 1);
        return onRow ? (false, row - 1) : null;
    }

    /// <summary>The box a row height is measured on: like <see cref="CellBox"/>, but an empty cell keeps its style run so its font sets the line height.</summary>
    private static TextBoxAnnotation MeasuredBox(TableAnnotation table, int row, int column)
    {
        var cell = table.Cell(row, column);
        return new(CellId(table.Id, row, column), false, Affine.Identity, cell.Text, cell.Runs, cell.Alignment, table.ColumnWidths[column],
            TextBoxStyle.Default, cell.Text.Length == 0 ? [] : cell.Highlights);
    }

    /// <summary>The table ID mixed with a hash of the cell (never zero), so each cell box has its own fixed ID.</summary>
    private static Guid CellId(Guid table, int row, int column)
    {
        Span<byte> bytes = stackalloc byte[16];
        table.TryWriteBytes(bytes);
        ulong key = (ulong)(uint)row << 32 | (uint)column;
        var low = BitConverter.ToUInt64(bytes) ^ Mix(key + 0x9E3779B97F4A7C15);
        var high = BitConverter.ToUInt64(bytes[8..]) ^ Mix(key + 0x3C6EF372FE94F82A);
        BitConverter.TryWriteBytes(bytes, low);
        BitConverter.TryWriteBytes(bytes[8..], high);
        return new Guid(bytes);
    }

    // SplitMix64 finalizer.
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    private static int Slot(ImmutableArray<double> lines, double v)
    {
        if (v < lines[0] || v > lines[^1]) return -1;
        for (int i = 1; i < lines.Length; i++) if (v < lines[i]) return i - 1;
        return lines.Length - 2;
    }

    /// <summary>The inner line (index 1 … Length − 2) nearest to <paramref name="v"/> and its distance, or (-1, ∞) without inner lines.</summary>
    private static (int Index, double Distance) Nearest(ImmutableArray<double> lines, double v)
    {
        (int, double) best = (-1, double.PositiveInfinity);
        for (int i = 1; i < lines.Length - 1; i++) if (Math.Abs(v - lines[i]) < best.Item2) best = (i, Math.Abs(v - lines[i]));
        return best;
    }
}
