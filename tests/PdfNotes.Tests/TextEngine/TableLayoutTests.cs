using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Tests.TextEngine;

/// <summary>1.4 Task 6: cell boxes and row heights of a table, and the table as one rectangle for hit and lasso.</summary>
public sealed class TableLayoutTests
{
    private const double Pad = TableAnnotation.CellPadding;
    private static readonly TextRun Style = new(0, 0, "Malgun Gothic", 12, RgbColor.Black, false, false, false, false);

    private static TableCell Text(string text, TextRun? style = null) =>
        new(text, [(style ?? Style) with { Start = 0, Length = text.Length }], TextAlignment.Left, []);

    private static TableAnnotation With(TableAnnotation table, int row, int column, TableCell cell) =>
        table with { Cells = table.Cells.SetItem(row * table.Columns + column, cell) };

    private const string Long = "칸 글자가 열 너비보다 길면 줄이 바뀌고 그 행이 글자만큼 커진다.";

    [Fact]
    public void LongTextGrowsItsRow()
    {
        var table = With(TableAnnotation.Create(Guid.NewGuid(), new(50, 70), 3, 3, Style), 1, 1, Text(Long));
        var layout = TableLayout.Measure(table);

        Assert.Equal([0, 80, 160, 240], layout.ColumnX.ToArray());
        double grown = TextLayout.Bounds(TableLayout.CellBox(table, 1, 1)).Height;
        Assert.True(grown > 2 * TableAnnotation.DefaultRowHeight, $"long text is {grown}pt tall");
        Assert.Equal(0, layout.RowY[0]);
        Assert.Equal(24, layout.RowY[1], 6);
        Assert.Equal(24 + grown, layout.RowY[2], 6);
        Assert.Equal(24 + grown + 24, layout.RowY[3], 6);
        // The outer lines paint half their width outside the grid.
        Assert.Equal(new Rect2(-0.375, -0.375, 240.75, layout.RowY[3] + 0.75), layout.Bounds);
        // The other cells of the grown row get the same height: their boxes start on the same line, the next row below all.
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(table.Transform.Transform(new(layout.ColumnX[c], 24)), TableLayout.CellBox(table, 1, c).Transform.Transform(new(0, 0)));
            Assert.Equal(table.Transform.Transform(new(layout.ColumnX[c], 24 + grown)), TableLayout.CellBox(table, 2, c).Transform.Transform(new(0, 0)));
        }
    }

    [Fact]
    public void RowNeverShrinksBelowItsSetHeight()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 2, 2, Style) with { RowHeights = [60, 30] };
        table = With(table, 0, 0, Text("짧은 글"));
        var layout = TableLayout.Measure(table);
        Assert.Equal([0, 60, 90], layout.RowY.ToArray());
        // An empty row of a big font is as tall as its font line, though nothing is typed in it.
        var big = TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 2, 1, Style with { SizePt = 30 });
        var bigLayout = TableLayout.Measure(big);
        Assert.True(bigLayout.RowY[2] - bigLayout.RowY[1] > 30 + 2 * Pad, $"an empty 30pt row is {bigLayout.RowY[2] - bigLayout.RowY[1]}pt");
    }

    [Fact]
    public void CellBoxSitsInsideThePadding()
    {
        var transform = Affine.Rotation(0.3).Then(Affine.Translation(120, 200));
        var table = TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 2, 3, Style) with { Transform = transform, ColumnWidths = [80, 120, 60] };
        table = With(table, 1, 1, Text(Long) with { Alignment = TextAlignment.Right });
        var layout = TableLayout.Measure(table);
        var box = TableLayout.CellBox(table, 1, 1);

        Assert.Equal(Long, box.Text);
        Assert.Equal(TextAlignment.Right, box.Alignment);
        Assert.Equal(TextBoxStyle.Default, box.BoxStyle);
        Assert.Equal(120, box.WidthPt);
        Assert.Equal(1, box.LayoutScale);
        Assert.Equal(Affine.Translation(80, layout.RowY[1]).Then(transform), box.Transform);
        Assert.Null(ModelValidator.ValidateAnnotation(box, _ => true));
        // Every glyph lies inside the cell, CellPadding away from its lines.
        double rowHeight = layout.RowY[2] - layout.RowY[1];
        var characters = TextLayout.Measure(box).Characters.Where(ch => !char.IsWhiteSpace(ch.Text[0])).ToArray();
        Assert.NotEmpty(characters);
        Assert.All(characters, ch =>
        {
            Assert.True(ch.Bounds.X >= Pad - 1e-6 && ch.Bounds.X + ch.Bounds.Width <= 120 - Pad + 1e-6, $"'{ch.Text}' at x {ch.Bounds.X}");
            Assert.True(ch.Bounds.Y >= Pad - 1e-6 && ch.Bounds.Y + ch.Bounds.Height <= rowHeight - Pad + 1e-6, $"'{ch.Text}' at y {ch.Bounds.Y}");
        });
        // IDs are fixed per table and cell, so a cell box is the same box on screen, in the PDF and in the editor.
        Assert.Equal(box.Id, TableLayout.CellBox(table, 1, 1).Id);
        var ids = Enumerable.Range(0, 6).Select(i => TableLayout.CellBox(table, i / 3, i % 3).Id).Append(table.Id).ToHashSet();
        Assert.Equal(7, ids.Count);
        Assert.NotEqual(box.Id, TableLayout.CellBox(table with { Id = Guid.NewGuid() }, 1, 1).Id);
        // The box turns back into the cell.
        var cell = TableLayout.FromBox(box, TableLayout.CellStyle(table, 1, 1));
        Assert.Equal(table.Cell(1, 1).Text, cell.Text);
        Assert.Equal(table.Cell(1, 1).Runs.ToArray(), cell.Runs.ToArray());
        Assert.Equal(TextAlignment.Right, cell.Alignment);
    }

    [Fact]
    public void EmptyCellsKeepTheirStyleThroughTheirBox()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 2, 2, Style);
        var header = TableLayout.CellBox(table, 0, 1);
        // The empty box is one the text box code accepts: no runs. Its style is the typing style.
        Assert.Equal("", header.Text);
        Assert.Empty(header.Runs);
        Assert.Null(ModelValidator.ValidateAnnotation(header, _ => true));
        var style = TableLayout.CellStyle(table, 0, 1);
        Assert.True(style.Bold);
        Assert.Equal((0, 0), (style.Start, style.Length));
        Assert.False(TableLayout.CellStyle(table, 1, 0).Bold);

        var back = TableLayout.FromBox(header, style);
        Assert.Equal(TableCell.Empty(Style with { Bold = true }).Runs.ToArray(), back.Runs.ToArray());
        Assert.Equal("", back.Text);
        Assert.Empty(back.Highlights);
        var restored = With(table, 0, 1, back);
        Assert.Null(ModelValidator.ValidateAnnotation(restored, _ => true));
        // A cell whose text was deleted while editing is empty again, with the style it was typed in.
        var typed = TableLayout.CellBox(With(table, 1, 0, Text("지울 글", Style with { Italic = true })), 1, 0);
        var cleared = TableLayout.FromBox(typed with { Text = "", Runs = [] }, Style with { Italic = true, Start = 3, Length = 4 });
        Assert.Equal("", cleared.Text);
        Assert.Equal(TableCell.Empty(Style with { Italic = true }).Runs.ToArray(), cleared.Runs.ToArray());
        Assert.Null(ModelValidator.ValidateAnnotation(With(table, 1, 0, cleared), _ => true));
    }

    [Fact]
    public void CellAtFindsTheCell()
    {
        var table = With(TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 3, 2, Style), 1, 0, Text(Long));
        var layout = TableLayout.Measure(table);
        Assert.Equal((0, 0), TableLayout.CellAt(table, new(1, 1)));
        Assert.Equal((0, 1), TableLayout.CellAt(table, new(150, 10)));
        Assert.Equal((1, 1), TableLayout.CellAt(table, new(100, layout.RowY[2] - 1)));
        Assert.Equal((2, 0), TableLayout.CellAt(table, new(5, layout.RowY[2] + 1)));
        Assert.Equal((2, 1), TableLayout.CellAt(table, new(160, layout.RowY[3])));
        Assert.Null(TableLayout.CellAt(table, new(-1, 10)));
        Assert.Null(TableLayout.CellAt(table, new(161, 10)));
        Assert.Null(TableLayout.CellAt(table, new(10, layout.RowY[3] + 0.5)));
    }

    [Fact]
    public void BoundaryAtFindsInnerLines()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 3, 3, Style);
        // Inner lines and the outer right and bottom lines; the index is the column or row before the line (the one it sizes).
        Assert.Equal((true, 0), TableLayout.BoundaryAt(table, new(81.5, 30), 2));
        Assert.Equal((true, 1), TableLayout.BoundaryAt(table, new(159, 70), 2));
        Assert.Equal((false, 0), TableLayout.BoundaryAt(table, new(40, 23), 2));
        Assert.Equal((false, 1), TableLayout.BoundaryAt(table, new(200, 49), 2));
        // Where two lines cross, the nearer one.
        Assert.Equal((false, 0), TableLayout.BoundaryAt(table, new(81.5, 24.5), 2));
        Assert.Equal((true, 0), TableLayout.BoundaryAt(table, new(80.5, 25.5), 2));
        Assert.Equal((true, 2), TableLayout.BoundaryAt(table, new(239.5, 30), 2));
        Assert.Equal((true, 2), TableLayout.BoundaryAt(table, new(241.5, 30), 2));
        Assert.Equal((false, 2), TableLayout.BoundaryAt(table, new(40, 72), 2));
        Assert.Equal((false, 2), TableLayout.BoundaryAt(table, new(239, 73), 2));
        // Not near a line, on the left or top line (they size nothing) or past the table's ends.
        Assert.Null(TableLayout.BoundaryAt(table, new(40, 36), 2));
        Assert.Null(TableLayout.BoundaryAt(table, new(0.5, 30), 2));
        Assert.Null(TableLayout.BoundaryAt(table, new(40, 0.5), 2));
        Assert.Null(TableLayout.BoundaryAt(table, new(80, 76), 2));
        Assert.Null(TableLayout.BoundaryAt(table, new(-5, 24), 2));
    }

    [Fact]
    public void MissingFontFallsBackAndRelayouts()
    {
        const string missing = "No Such Font For Tables";
        Assert.False(TextLayout.FontAvailable(missing));
        var font = Style with { FontFamily = missing, SizePt = 28 };
        var table = With(TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 2, 2, Style), 1, 1, Text("대체 글꼴로 줄을 바꾼다", font));
        var layout = TableLayout.Measure(table);
        var box = TableLayout.CellBox(table, 1, 1);
        // The font name is kept (decision 20); the row is as tall as the text laid out in the fallback font.
        Assert.Equal(missing, box.Runs.Single().FontFamily);
        var fallback = box with { Runs = [box.Runs[0] with { FontFamily = TextLayout.FallbackFont }] };
        double height = TextLayout.Bounds(fallback).Height;
        Assert.True(height > TableAnnotation.DefaultRowHeight);
        Assert.Equal(height, layout.RowY[2] - layout.RowY[1], 6);
        Assert.Equal(missing, TableLayout.FromBox(box, TableLayout.CellStyle(table, 1, 1)).Runs.Single().FontFamily);
    }

    [Fact]
    public void ATableIsItsRectangleForHitAndLasso()
    {
        var table = With(TableAnnotation.Create(Guid.NewGuid(), new(100, 100), 2, 2, Style), 1, 1, Text(Long));
        var layout = TableLayout.Measure(table);
        Assert.Equal(layout.Bounds, AnnotationGeometry.LocalBounds(table, TextLayout.Bounds));
        Assert.Throws<NotSupportedException>(() => AnnotationGeometry.LocalBounds(table));
        Assert.True(AnnotationGeometry.Hit(table, new(110, 100 + layout.RowY[2] - 2), 0, TextLayout.Bounds));
        Assert.False(AnnotationGeometry.Hit(table, new(110, 100 + layout.RowY[2] + 2), 0, TextLayout.Bounds));

        var page = PdfNotes.Tests.Core.TestModels.Empty().Pages[0] with { Annotations = [table] };
        double bottom = 100 + layout.RowY[2];
        Point2[] corner = [new(250, bottom - 5), new(270, bottom - 5), new(270, bottom + 5), new(250, bottom + 5)];
        Assert.Equal([table.Id], Lasso.Select(page, corner, TextLayout.Bounds));
        Point2[] below = [new(250, bottom + 2), new(270, bottom + 2), new(270, bottom + 9), new(250, bottom + 9)];
        Assert.Empty(Lasso.Select(page, below, TextLayout.Bounds));

        // Moving and turning keep the cells; a scale multiplies column widths and row heights, not the text.
        var turned = Assert.IsType<TableAnnotation>(AnnotationGeometry.Similarity(table, new(100, 100), 1, 0.5, new(10, 0)));
        Assert.Equal(table.Cells, turned.Cells);
        Assert.Equal(table.ColumnWidths.ToArray(), turned.ColumnWidths.ToArray());
        Assert.Equal(table.RowHeights.ToArray(), turned.RowHeights.ToArray());
        Assert.Equal(new Point2(110, 100), turned.Transform.Transform(new(0, 0)));
        var doubled = Assert.IsType<TableAnnotation>(AnnotationGeometry.Similarity(table, new(100, 100), 2));
        Assert.Equal([160, 160], doubled.ColumnWidths.ToArray());
        Assert.Equal([48, 48], doubled.RowHeights.ToArray());
        Assert.Equal(12, doubled.Cell(1, 1).Runs.Single().SizePt);
        Assert.Equal(new Point2(100, 100), doubled.Transform.Transform(new(0, 0)));
        var small = Assert.IsType<TableAnnotation>(AnnotationGeometry.Similarity(table, new(100, 100), 0.1));
        Assert.All(small.ColumnWidths, w => Assert.Equal(TableAnnotation.MinColumnWidth, w));
        Assert.All(small.RowHeights, h => Assert.Equal(TableAnnotation.MinRowHeight, h));
    }

    [Fact]
    public void OneTableIsMeasuredOnce()
    {
        var table = With(TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 2, 2, Style), 1, 1, Text(Long));
        var first = TableGeometry.Measure(table, TextLayout.Bounds);
        // Hit tests, the lasso and the painter measure through the same cache (a new delegate of the same method counts as the same).
        Assert.Same(first, TableGeometry.Measure(table, TextLayout.Bounds));
        Assert.Same(first, TableLayout.Measure(table));
        Assert.Equal(first.Bounds, AnnotationGeometry.LocalBounds(table, TextLayout.Bounds));
        Assert.Same(TableLayout.CellBox(table, 1, 1), TableLayout.CellBox(table, 1, 1));
        // Another measurer gets its own measurement; a changed table is measured again.
        var tall = TableGeometry.Measure(table, b => new(0, 0, b.WidthPt, 100));
        Assert.Equal(200, tall.RowY[2]);
        Assert.NotSame(first, TableLayout.Measure(table with { RowHeights = [30, 30] }));
    }

    [Fact]
    public void APageScaleShrinksTheTextToo()
    {
        var table = With(TableAnnotation.Create(Guid.NewGuid(), new(100, 100), 2, 2, Style with { SizePt = 20 }), 1, 1,
            Text("글", Style with { SizePt = 20 }) with { Highlights = [new(0, 1, HighlightHeightMode.Manual, 16, RgbColor.Black, 0.5)] });
        var change = Affine.Scale(0.5).Then(Affine.Translation(10, 20));
        var placed = Assert.IsType<TableAnnotation>(PdfNotes.Core.Commands.PageTransforms.Transform(table, change, 0.5));
        Assert.Equal([40, 40], placed.ColumnWidths.ToArray());
        Assert.Equal([12, 12], placed.RowHeights.ToArray());
        Assert.All(placed.Cells, c => Assert.Equal(10, c.Runs.Single().SizePt));
        Assert.Equal(8, placed.Cell(1, 1).Highlights.Single().ManualHeightPt);
        Assert.Equal(0.375, placed.Style.LineWidthPt);
        Assert.Equal(new Point2(60, 70), placed.Transform.Transform(new(0, 0)));
        Assert.Null(ModelValidator.ValidateAnnotation(placed, _ => true));
    }

    /// <summary>1.4 Task 10: the size handles multiply widths and heights (text keeps its size); a single table stretches from the fixed corner.</summary>
    [Fact]
    public void HandleResizeScalesCellsAndKeepsTheOppositeCorner()
    {
        var table = With(TableAnnotation.Create(Guid.NewGuid(), new(50, 70), 2, 2, Style), 1, 1, Text("가"))
            with { Transform = Affine.Rotation(Math.PI / 6).Then(Affine.Translation(50, 70)) };
        var even = (TableAnnotation)AnnotationGeometry.Similarity(table, new(50, 70), 1.5);
        Assert.Equal([120, 120], even.ColumnWidths.ToArray());
        Assert.Equal([36, 36], even.RowHeights.ToArray());
        Assert.Equal(12, even.Cell(1, 1).Runs[0].SizePt);

        var before = TableLayout.Measure(table).Bounds;
        var anchor = new Point2(before.X + before.Width, before.Y + before.Height); // the bottom-right corner stays
        var stretched = AnnotationGeometry.ResizeTable(table, anchor, 2, 0.5, TextLayout.Bounds);
        Assert.Equal([160, 160], stretched.ColumnWidths.ToArray());
        Assert.Equal([TableAnnotation.MinRowHeight, TableAnnotation.MinRowHeight], stretched.RowHeights.ToArray());
        var after = TableLayout.Measure(stretched).Bounds;
        var fixedBefore = table.Transform.Transform(anchor);
        var fixedAfter = stretched.Transform.Transform(new(after.X + after.Width, after.Y + after.Height));
        Assert.Equal(fixedBefore.X, fixedAfter.X, 6);
        Assert.Equal(fixedBefore.Y, fixedAfter.Y, 6);
        Assert.Equal(12, stretched.Cell(1, 1).Runs[0].SizePt);
        Assert.Null(ModelValidator.ValidateAnnotation(stretched, _ => true));
    }
}
