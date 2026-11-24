using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;

namespace PdfNotes.Tests.Core;

/// <summary>1.4 Task 5: the table annotation in the model, the validator and schema v9.</summary>
public sealed class TableModelTests
{
    private static readonly TextRun Style = new(0, 0, "Malgun Gothic", 11, RgbColor.Black, false, false, false, false);

    private static DocumentModel With(params Annotation[] annotations)
    {
        var model = TestModels.Empty();
        return model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [.. annotations] }) };
    }

    private static TableCell Cell(string text, bool bold = false, ImmutableArray<TextHighlightRange> highlights = default) =>
        new(text, [Style with { Length = text.Length, Bold = bold }], TextAlignment.Left, highlights.IsDefault ? [] : highlights);

    private static DocumentReadResult Read(SerializedDocument data) =>
        SchemaV9.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null);

    private static byte[] WithVersion(byte[] json, int version)
    {
        var node = JsonNode.Parse(json)!;
        node["schemaVersion"] = version;
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static int Version(byte[] json) => JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>();

    [Fact]
    public void TableRoundTripsThroughV9()
    {
        var created = TableAnnotation.Create(Guid.NewGuid(), new(40, 60), 3, 2, Style);
        var red = new RgbColor(200, 20, 20);
        var cells = created.Cells
            .SetItem(0, Cell("이름", bold: true))
            .SetItem(3, new TableCell("ab", [Style with { Length = 1, Italic = true }, Style with { Start = 1, Length = 1, Color = red, SizePt = 14 }],
                TextAlignment.Center, []))
            .SetItem(5, Cell("형광", highlights: [new(0, 2, HighlightHeightMode.Manual, 9, new(255, 230, 0), 0.4)]));
        var table = created with
        {
            Transform = Affine.Rigid(0.3, 40, 60), Cells = cells, ColumnWidths = [120, 64.5], RowHeights = [24, 30, 12],
            Style = new TableStyle(new(0x1E, 0x4F, 0x91), 1.5, false)
        };
        var model = With(TestModels.Ink(), table);

        var data = SchemaV9.Serialize(model);
        Assert.Equal(9, Version(data.Header));
        Assert.All(data.Annotations, a => Assert.Equal(9, Version(a.Envelope)));
        Assert.NotNull(JsonNode.Parse(data.Header)!["tags"]);
        var read = Read(data);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.All(read.Annotations, a => Assert.Equal(AnnotationReadStatus.Restored, a.Status));
        var back = Assert.IsType<TableAnnotation>(read.Model!.Pages[0].Annotations[1]);
        Assert.Equal((3, 2), (back.Rows, back.Columns));
        Assert.Equal(table.Transform, back.Transform);
        Assert.Equal([120, 64.5], back.ColumnWidths.ToArray());
        Assert.Equal([24, 30, 12], back.RowHeights.ToArray());
        Assert.Equal(table.Style, back.Style);
        Assert.Equal("ab", back.Cell(1, 1).Text);
        Assert.Equal(TextAlignment.Center, back.Cell(1, 1).Alignment);
        Assert.Equal(table.Cell(1, 1).Runs.ToArray(), back.Cell(1, 1).Runs.ToArray());
        Assert.Equal(table.Cell(2, 1).Highlights.ToArray(), back.Cell(2, 1).Highlights.ToArray());
        Assert.Equal(table.Cell(2, 0).Runs.ToArray(), back.Cell(2, 0).Runs.ToArray()); // an empty cell keeps its typing style
        // Writing what was read gives the same bytes.
        var again = SchemaV9.Serialize(read.Model);
        Assert.Equal(data.Header, again.Header);
        Assert.Equal(data.Annotations.Select(a => a.Envelope), again.Annotations.Select(a => a.Envelope));

        // Tags and pressures still travel in v9.
        var wedge = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(0, 0), new(10, 0)], 2, RgbColor.Black, 1) { Pressures = [0.25, 0.75] };
        var mixed = With(wedge, table);
        mixed = mixed with { Tags = mixed.Tags.Add(table.Id, new AnnotationTag("ai", null, "t1", null)) };
        var mixedRead = Read(SchemaV9.Serialize(mixed));
        Assert.Equal("t1", mixedRead.Model!.TagOf(table.Id)?.SourceKey);
        Assert.Equal([0.25, 0.75], mixedRead.Model.Pages[0].Annotations.OfType<InkStroke>().Single().Pressures.ToArray());

        // Older readers see an unknown version; older writers refuse tables.
        Assert.Equal(HeaderStatus.Unsupported, SchemaV8.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null).HeaderStatus);
        Assert.Throws<InvalidDataException>(() => SchemaV8.Serialize(model));
    }

    [Fact]
    public void DocumentsWithoutTablesStayV8()
    {
        Assert.Equal(6, Version(SchemaV9.Serialize(With(TestModels.Ink())).Header));
        var wedge = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(0, 0), new(10, 0)], 2, RgbColor.Black, 1) { Pressures = [0.5, 0.5] };
        var pressured = SchemaV9.Serialize(With(wedge));
        Assert.Equal(8, Version(pressured.Header));
        // v9 still reads what older versions wrote.
        var read = Read(pressured);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.True(read.Model!.Pages[0].Annotations.OfType<InkStroke>().Single().HasPressure);
        // A table in a v8 file is not part of v8: that envelope is rejected.
        var table = SchemaV9.Serialize(With(TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 1, 1, Style)));
        byte[] header = WithVersion(table.Header, 8);
        var v8 = SchemaV9.Read(header, [WithVersion(table.Annotations[0].Envelope, 8)], _ => null);
        Assert.Equal(AnnotationReadStatus.Rejected, v8.Annotations[0].Status);
    }

    [Fact]
    public void ValidatorRejectsCellCountMismatch()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(10, 10), 2, 3, Style);
        Assert.Null(ModelValidator.ValidateAnnotation(table, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(table with { Cells = table.Cells.RemoveAt(0) }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(table with { ColumnWidths = [80, 80] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(table with { RowHeights = [], ColumnWidths = [], Cells = [] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(table with { ColumnWidths = [80, TableAnnotation.MinColumnWidth - 1, 80] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(table with { RowHeights = [24, TableAnnotation.MinRowHeight - 1] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(table with { Style = table.Style with { LineWidthPt = 0 } }, _ => true));
        // Cell runs follow the text box rules.
        var gap = new TableCell("abc", [Style with { Length = 1 }, Style with { Start = 2, Length = 1 }], TextAlignment.Left, []);
        Assert.NotNull(ModelValidator.ValidateAnnotation(table with { Cells = table.Cells.SetItem(4, gap) }, _ => true));
        var noRuns = new TableCell("abc", [], TextAlignment.Left, []);
        Assert.NotNull(ModelValidator.ValidateAnnotation(table with { Cells = table.Cells.SetItem(4, noRuns) }, _ => true));
        // A bad cell in a file rejects that table, not the document.
        var data = SchemaV9.Serialize(With(table));
        var node = JsonNode.Parse(data.Annotations[0].Envelope)!;
        node["annotation"]!["cells"]!.AsArray().RemoveAt(0);
        var read = SchemaV9.Read(data.Header, [System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())], _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.Equal(AnnotationReadStatus.Rejected, read.Annotations[0].Status);
    }

    [Fact]
    public void CellChecksItsRowAndColumn()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 2, 3, Style);
        Assert.Same(table.Cells[5], table.Cell(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Cell(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Cell(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Cell(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Cell(-1, 0));
    }

    [Fact]
    public void AnEmptyCellCannotHaveHighlights()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 1, 1, Style);
        var highlighted = TableCell.Empty(Style) with { Highlights = [new(0, 1, HighlightHeightMode.Auto, null, RgbColor.Black, 0.5)] };
        Assert.Equal("An empty table cell cannot have highlights.",
            ModelValidator.ValidateAnnotation(table with { Cells = [highlighted] }, _ => true));
    }

    [Fact]
    public void AnEmptyTableIsKept()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(10, 10), 2, 2, Style);
        Assert.All(table.Cells, c => Assert.Equal("", c.Text));
        var read = Read(SchemaV9.Serialize(With(table)));
        Assert.Equal(AnnotationReadStatus.Restored, Assert.Single(read.Annotations).Status);
        var back = Assert.IsType<TableAnnotation>(Assert.Single(read.Model!.Pages[0].Annotations));
        Assert.Equal(table.Id, back.Id);
        Assert.All(back.Cells, c => Assert.Equal("", c.Text));
    }

    [Fact]
    public void CreateMakesBoldHeaderCells()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(30, 50), 3, 4, Style);
        Assert.Equal((3, 4), (table.Rows, table.Columns));
        Assert.Equal(Affine.Translation(30, 50), table.Transform);
        Assert.Equal(TableStyle.Default, table.Style);
        Assert.All(table.ColumnWidths, w => Assert.Equal(TableAnnotation.DefaultColumnWidth, w));
        Assert.All(table.RowHeights, h => Assert.Equal(TableAnnotation.DefaultRowHeight, h));
        Assert.Equal(12, table.Cells.Length);
        for (int c = 0; c < 4; c++)
        {
            Assert.True(table.Cell(0, c).Runs.Single().Bold);
            Assert.False(table.Cell(1, c).Runs.Single().Bold);
            Assert.Equal(Style.FontFamily, table.Cell(2, c).Runs.Single().FontFamily);
        }
        Assert.Equal(TableCell.Empty(Style).Runs.ToArray(), table.Cell(2, 3).Runs.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => TableAnnotation.Create(Guid.NewGuid(), new(0, 0), 0, 2, Style));
    }
}
