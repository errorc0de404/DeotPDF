using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

/// <summary>1.4 Task 8: pure table edits (rows, columns, sizes, cells, header).</summary>
public sealed class TableEditsTests
{
    private static readonly TextRun Style = new(0, 0, "Malgun Gothic", 11, RgbColor.Black, false, false, false, false);
    private static readonly TextRun Big = Style with { SizePt = 20, FontFamily = "Arial" };

    private static TableCell Cell(string text, TextRun? style = null, bool bold = false) =>
        new(text, [(style ?? Style) with { Start = 0, Length = text.Length, Bold = bold }], TextAlignment.Left, []);

    /// <summary>3×2 table whose cells read "r,c" (header row bold).</summary>
    private static TableAnnotation Sample()
    {
        var t = TableAnnotation.Create(Guid.NewGuid(), new(10, 20), 3, 2, Style);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 2; c++)
                t = TableEdits.SetCell(t, r, c, Cell($"{r},{c}", bold: r == 0));
        return t;
    }

    private static TableAnnotation Valid(TableAnnotation? t)
    {
        Assert.NotNull(t);
        Assert.Null(ModelValidator.ValidateAnnotation(t, _ => true));
        return t;
    }

    [Fact]
    public void InsertRowAddsADefaultHeightRowWithTheStyleOfTheRowAbove()
    {
        var t = TableEdits.SetCell(Sample(), 1, 0, Cell("1,0", Big));
        var result = Valid(TableEdits.InsertRow(t, 2));
        Assert.Equal(4, result.Rows);
        Assert.Equal(TableAnnotation.DefaultRowHeight, result.RowHeights[2]);
        Assert.Equal("1,0", result.Cell(1, 0).Text);
        Assert.Equal("2,0", result.Cell(3, 0).Text);
        for (int c = 0; c < 2; c++)
        {
            var cell = result.Cell(2, c);
            Assert.Equal("", cell.Text);
            Assert.Equal(20, Assert.Single(cell.Runs).SizePt);
            Assert.Equal("Arial", cell.Runs[0].FontFamily);
            Assert.Equal(0, cell.Runs[0].Length);
        }
        Assert.Equal(t.Id, result.Id);
        Assert.Equal(t.Transform, result.Transform);
    }

    [Fact]
    public void InsertRowBelowTheHeaderIsNotBoldAndAtTheTopBecomesTheBoldHeader()
    {
        var below = Valid(TableEdits.InsertRow(Sample(), 1));
        Assert.False(below.Cell(1, 0).Runs[0].Bold);
        Assert.True(below.Cell(0, 0).Runs[0].Bold);

        var top = Valid(TableEdits.InsertRow(Sample(), 0));
        Assert.Equal(4, top.Rows);
        Assert.True(top.Cell(0, 1).Runs[0].Bold);
        Assert.Equal("0,0", top.Cell(1, 0).Text);

        var noHeader = Valid(TableEdits.InsertRow(TableEdits.SetHeader(Sample(), false), 0));
        Assert.False(noHeader.Cell(0, 0).Runs[0].Bold);

        Valid(TableEdits.InsertRow(Sample(), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.InsertRow(Sample(), 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.InsertRow(Sample(), -1));
    }

    [Fact]
    public void InsertColumnAddsADefaultWidthColumnStyledLikeItsLeftNeighbour()
    {
        var t = TableEdits.SetCell(Sample(), 2, 0, Cell("2,0", Big));
        var result = Valid(TableEdits.InsertColumn(t, 1));
        Assert.Equal(3, result.Columns);
        Assert.Equal(TableAnnotation.DefaultColumnWidth, result.ColumnWidths[1]);
        Assert.Equal("2,0", result.Cell(2, 0).Text);
        Assert.Equal("2,1", result.Cell(2, 2).Text);
        Assert.Equal("", result.Cell(2, 1).Text);
        Assert.Equal(20, result.Cell(2, 1).Runs[0].SizePt);
        Assert.True(result.Cell(0, 1).Runs[0].Bold);
        Assert.False(result.Cell(1, 1).Runs[0].Bold);

        var first = Valid(TableEdits.InsertColumn(t, 0));
        Assert.Equal("", first.Cell(2, 0).Text);
        Assert.Equal(20, first.Cell(2, 0).Runs[0].SizePt);
        Assert.Equal("2,0", first.Cell(2, 1).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.InsertColumn(t, 3));
    }

    [Fact]
    public void InsertIntoCellsWithoutAStyleRunGivesValidEmptyCells()
    {
        var t = Sample();
        t = t with { Cells = [.. t.Cells.Select(_ => new TableCell("", [], TextAlignment.Left, []))] };
        Assert.Empty(Valid(TableEdits.InsertRow(t, 1)).Cell(1, 0).Runs);
        Assert.Empty(Valid(TableEdits.InsertColumn(t, 1)).Cell(1, 1).Runs);
    }

    [Fact]
    public void DeleteRowRemovesItsCellsAndTheLastRowGivesNull()
    {
        var result = Valid(TableEdits.DeleteRow(Sample(), 1));
        Assert.Equal(2, result.Rows);
        Assert.Equal("2,1", result.Cell(1, 1).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.DeleteRow(Sample(), 3));

        var one = Valid(TableEdits.DeleteRow(result, 0));
        Assert.Null(TableEdits.DeleteRow(one, 0));
    }

    [Fact]
    public void DeleteColumnRemovesItsCellsAndTheLastColumnGivesNull()
    {
        var t = TableEdits.ResizeColumn(Sample(), 1, 120);
        var result = Valid(TableEdits.DeleteColumn(t, 0));
        Assert.Equal(1, result.Columns);
        Assert.Equal([120.0], result.ColumnWidths.ToArray());
        Assert.Equal(["0,1", "1,1", "2,1"], result.Cells.Select(c => c.Text));
        Assert.Null(TableEdits.DeleteColumn(result, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.DeleteColumn(t, 2));
    }

    [Fact]
    public void ResizeKeepsTheMinimumSizes()
    {
        var t = Sample();
        Assert.Equal(150, Valid(TableEdits.ResizeColumn(t, 1, 150)).ColumnWidths[1]);
        Assert.Equal(TableAnnotation.MinColumnWidth, Valid(TableEdits.ResizeColumn(t, 1, 3)).ColumnWidths[1]);
        Assert.Equal(TableAnnotation.DefaultColumnWidth, TableEdits.ResizeColumn(t, 1, 3).ColumnWidths[0]);
        Assert.Equal(40, Valid(TableEdits.ResizeRow(t, 2, 40)).RowHeights[2]);
        Assert.Equal(TableAnnotation.MinRowHeight, Valid(TableEdits.ResizeRow(t, 2, -5)).RowHeights[2]);
        Assert.Equal(ModelLimits.MaxLengthPt, Valid(TableEdits.ResizeRow(t, 0, 1e9)).RowHeights[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.ResizeColumn(t, 1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.ResizeRow(t, 3, 30));
    }

    [Fact]
    public void SetCellReplacesOneCell()
    {
        var result = Valid(TableEdits.SetCell(Sample(), 2, 1, Cell("new", Big)));
        Assert.Equal("new", result.Cell(2, 1).Text);
        Assert.Equal("2,0", result.Cell(2, 0).Text);
        Assert.Equal("1,1", result.Cell(1, 1).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.SetCell(Sample(), 0, 2, Cell("x")));
    }

    [Fact]
    public void ScaledChangesSizesButNotFontSizes()
    {
        var t = TableEdits.SetCell(Sample(), 1, 1, Cell("1,1", Big));
        var result = Valid(TableEdits.Scaled(t, 2, 0.5));
        Assert.Equal([160.0, 160.0], result.ColumnWidths.ToArray());
        Assert.Equal([12.0, 12.0, 12.0], result.RowHeights.ToArray());
        Assert.Equal(t.Cells, result.Cells);
        Assert.Equal(20, result.Cell(1, 1).Runs[0].SizePt);
        Assert.Equal(t.Transform, result.Transform);
        Assert.Equal(t.Style, result.Style);

        var tiny = Valid(TableEdits.Scaled(t, 0.01, 0.01));
        Assert.All(tiny.ColumnWidths, w => Assert.Equal(TableAnnotation.MinColumnWidth, w));
        Assert.All(tiny.RowHeights, h => Assert.Equal(TableAnnotation.MinRowHeight, h));
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.Scaled(t, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEdits.Scaled(t, 1, double.PositiveInfinity));
    }

    [Fact]
    public void SetHeaderTurnsTheFirstRowBoldOnAndOff()
    {
        var off = Valid(TableEdits.SetHeader(Sample(), false));
        Assert.False(off.Style.HeaderRow);
        Assert.All(off.Cells, c => Assert.All(c.Runs, r => Assert.False(r.Bold)));

        var on = Valid(TableEdits.SetHeader(off, true));
        Assert.True(on.Style.HeaderRow);
        Assert.All(on.Cells.Take(2), c => Assert.All(c.Runs, r => Assert.True(r.Bold)));
        Assert.All(on.Cells.Skip(2), c => Assert.All(c.Runs, r => Assert.False(r.Bold)));
        Assert.Equal(on.Cells.Select(c => c.Text), Sample().Cells.Select(c => c.Text));

        // Empty header cells keep their bold typing style too.
        var created = TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 2, 2, Style);
        var plain = Valid(TableEdits.SetHeader(created, false));
        Assert.False(plain.Cell(0, 0).Runs[0].Bold);
        var back = Valid(TableEdits.SetHeader(plain, true));
        Assert.Equal(created.Cells.Select(c => c.Runs.Single()), back.Cells.Select(c => c.Runs.Single()));
    }
}
