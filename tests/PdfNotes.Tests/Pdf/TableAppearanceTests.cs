using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Tests.Core;
using PdfNotes.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>1.4 Task 7: a table saved as a standard /Stamp with its drawn appearance, reopened, flattened, copied and searched.</summary>
public sealed class TableAppearanceTests
{
    internal static readonly TextRun Style = new(0, 0, "Malgun Gothic", 11, RgbColor.Black, false, false, false, false);

    internal static TableCell Cell(string text, bool bold = false) =>
        new(text, [Style with { Length = text.Length, Bold = bold }], TextAlignment.Left, []);

    /// <summary>3 rows × 2 columns at (60, 80): a header row, short text, a cell that wraps and an empty cell.</summary>
    internal static TableAnnotation Sample(Affine? transform = null)
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(60, 80), 3, 2, Style);
        return table with
        {
            Transform = transform ?? table.Transform,
            Cells = table.Cells.SetItem(0, Cell("Name", bold: true)).SetItem(1, Cell("Score", bold: true))
                .SetItem(2, Cell("Alpha beta")).SetItem(3, Cell("12"))
                .SetItem(4, Cell("A longer cell that wraps"))
        };
    }

    internal static DocumentModel Note(int rotation, params Annotation[] annotations)
    {
        var model = DocumentModel.Create([new PageSetup(500, 400, rotation, PageSource.Generated, PageBackground.Blank)]);
        return model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [.. annotations] }) };
    }

    private static int Version(byte[] json) => JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>();

    private static PdfDictionary OnlyAnnotation(PdfDocument doc) =>
        (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;

    private static (byte B, byte G, byte R) Pixel(byte[] pdf, PageSetup setup, Point2 canonical)
    {
        const double scale = 2;
        using var engine = new PdfiumDocument(pdf);
        var page = engine.Render(0, scale, annotations: true);
        var display = setup.Frame.CanonicalToDisplay.Transform(canonical);
        int i = (int)(display.Y * scale) * page.Stride + (int)(display.X * scale) * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }

    private static string Text(byte[] pdf) { using var d = new PdfiumDocument(pdf); return string.Concat(d.ExtractText(0).Select(c => c.Text)); }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void SavedTableReopensWithItsCells(int rotation)
    {
        var table = Sample(Affine.Rigid(0.2, 60, 80));
        var model = Note(rotation, TestModels.Ink(), table);
        var written = EditablePdfWriter.WritePdf(null, null, model);
        Assert.Equal(9, Version(written.Data.Header));
        EditablePdfWriter.Verify(written.Bytes, model);
        var read = EditablePdfReader.Read(written.Bytes, PdfPageGeometryReader.Read(written.Bytes));
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.Equal(2, read.RestoredCount);
        Assert.Equal(0, read.ReadOnlyCount);
        var back = Assert.IsType<TableAnnotation>(read.Model.Pages[0].Annotations[1]);
        Assert.Equal(table.Cells.Select(c => c.Text), back.Cells.Select(c => c.Text));
        var expected = SchemaV9.Serialize(model);
        var actual = SchemaV9.Serialize(read.Model);
        Assert.Equal(expected.Header, actual.Header);
        Assert.Equal(expected.Annotations.Select(a => Convert.ToBase64String(a.Envelope)), actual.Annotations.Select(a => Convert.ToBase64String(a.Envelope)));
        // Saved again from the reopened file: still one table, nothing read-only.
        byte[] again = EditablePdfWriter.Write(written.Bytes, PdfPageGeometryReader.Read(written.Bytes), read.Model);
        EditablePdfWriter.Verify(again, read.Model);
        // A document without tables still writes the version it wrote before (v6 here).
        Assert.Equal(6, Version(EditablePdfWriter.WritePdf(null, null, Note(rotation, TestModels.Ink())).Data.Header));
    }

    [Fact]
    public void AppearanceDrawsLinesAndText()
    {
        var table = Sample();
        var model = Note(0, table);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using (var doc = PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Import))
        {
            var annot = OnlyAnnotation(doc);
            Assert.Equal("/Stamp", annot.Elements.GetName("/Subtype"));
            var form = annot.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!;
            string code = System.Text.Encoding.ASCII.GetString(form.Stream.UnfilteredValue);
            Assert.Contains("0.941176 0.941176 0.941176 rg", code); // header fill
            Assert.Contains(" re\n", code);
            Assert.Contains("0.75 w", code);
            Assert.Contains("S\n", code);
            Assert.True(code.Split(" m\n").Length > 50, "glyph outlines");
            Assert.True(form.Elements.GetBoolean("/PdfNotesTable"));
            // The bounding box holds the outer lines.
            var layout = TableLayout.Measure(table);
            var bbox = form.Elements.GetArray("/BBox")!;
            Assert.True(bbox.Elements.GetReal(0) <= 60 - 0.375 && bbox.Elements.GetReal(2) >= 60 + layout.ColumnX[^1] + 0.375);
        }
        var setup = model.Pages[0].Setup;
        var grid = TableLayout.Measure(table);
        double rowMid = 80 + (grid.RowY[1] + grid.RowY[2]) / 2;
        var line = Pixel(pdf, setup, new(60 + grid.ColumnX[1], rowMid));
        Assert.True(line.R < 120 && line.G < 120 && line.B < 120, $"inner line {line}");
        var header = Pixel(pdf, setup, new(60 + grid.ColumnX[2] - 3, 80 + grid.RowY[1] - 3));
        Assert.InRange(header.R, 225, 245);
        var empty = Pixel(pdf, setup, new(60 + grid.ColumnX[2] - 5, 80 + grid.RowY[3] - 5));
        Assert.True(empty.R > 245 && empty.G > 245 && empty.B > 245, $"empty cell {empty}");
        // The cell text is also a hidden text layer, so other readers select and find it.
        Assert.Contains("Alpha beta", Text(pdf));
        // Reopened: the restored table is not drawn twice (its appearance and text layer leave the background).
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        using var engine = new PdfiumDocument(read.BackgroundPdf);
        var background = engine.Render(0, 1, annotations: true);
        Assert.All(Enumerable.Range(0, background.Bgra.Length / 4), i => Assert.True(background.Bgra[i * 4] > 240 || background.Bgra[i * 4 + 3] == 0));
        Assert.DoesNotContain("Alpha", string.Concat(engine.ExtractText(0).Select(c => c.Text)));
    }

    [Fact]
    public void OlderReaderSeesTheAppearance()
    {
        var model = Note(0, Sample());
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        // A reader that does not know v9 (an older app, or this one meeting a newer version): the header is unknown.
        var data = SchemaV9.Serialize(model);
        Assert.Equal(HeaderStatus.Unsupported, SchemaV8.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null).HeaderStatus);
        using (var doc = PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Modify))
        {
            var header = doc.Internals.Catalog.Elements.GetDictionary(EditablePdfKeys.Header)!;
            string json = System.Text.Encoding.UTF8.GetString(header.Stream.UnfilteredValue).Replace("\"schemaVersion\":9", "\"schemaVersion\":99");
            header.Stream.Value = System.Text.Encoding.UTF8.GetBytes(json); header.Elements.Remove("/Filter");
            using var buffer = new MemoryStream(); doc.Save(buffer, false); pdf = buffer.ToArray();
        }
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(HeaderStatus.Unsupported, read.HeaderStatus);
        Assert.Empty(read.Model.AllAnnotations);
        Assert.Equal(1, read.ReadOnlyCount);
        // The appearance stays in the background, read-only: its lines are drawn.
        var grid = TableLayout.Measure((TableAnnotation)model.Pages[0].Annotations[0]);
        using var engine = new PdfiumDocument(read.BackgroundPdf);
        var page = engine.Render(0, 2, annotations: true);
        var at = new Point2(60 + grid.ColumnX[1], 80 + (grid.RowY[1] + grid.RowY[2]) / 2);
        int i = (int)(at.Y * 2) * page.Stride + (int)(at.X * 2) * 4;
        Assert.True(page.Bgra[i] < 120, "the table line stays visible");
        Assert.Contains("Alpha beta", string.Concat(engine.ExtractText(0).Select(c => c.Text)));
    }

    [Fact]
    public void SearchFindsCellText()
    {
        var table = Sample(Affine.Rigid(0.3, 60, 80));
        var page = Note(0, table).Pages[0];
        var hits = DocumentSearch.SearchPage(page, [], "beta");
        var hit = Assert.Single(hits);
        Assert.Equal(table.Id, hit.BoxId);
        Assert.Equal((1, 0), hit.Cell);
        Assert.Equal((6, 10), (hit.Start, hit.End));
        // The hit marks the characters in the cell, on the turned table.
        var cell = TableLayout.CellBox(table, 1, 0);
        var characters = TextLayout.Measure(cell).Characters;
        var expected = cell.Transform.Transform(new(characters[6].Bounds.X, characters[6].Bounds.Y));
        Assert.Equal(expected.X, hit.Quads[0].UpperLeft.X, 3);
        Assert.Equal(expected.Y, hit.Quads[0].UpperLeft.Y, 3);
        // A header cell and a word on the wrapped line are found too; the empty cell has nothing.
        Assert.Equal((0, 1), Assert.Single(DocumentSearch.SearchPage(page, [], "Score")).Cell);
        Assert.Equal((2, 0), Assert.Single(DocumentSearch.SearchPage(page, [], "wraps")).Cell);
        Assert.Empty(DocumentSearch.SearchPage(page, [], "zzz"));
    }

    [Fact]
    public void TablesAreFlattenedCopiedAndPasted()
    {
        var table = Sample(Affine.Rigid(-0.1, 60, 80));
        var model = Note(90, table, TestModels.Ink());
        byte[] editable = EditablePdfWriter.Write(null, null, model);
        var flat = FlattenWriter.Write(null, null, model);
        FlattenWriter.Verify(flat.Pdf, 1);
        Assert.Equal(2, flat.MergedApp);
        Assert.Contains("Alpha beta", Text(flat.Pdf));
        using (var a = new PdfiumDocument(editable)) using (var b = new PdfiumDocument(flat.Pdf))
        {
            var p = a.Render(0, 1.5, annotations: true); var q = b.Render(0, 1.5, annotations: true);
            Assert.True(p.Bgra.Zip(q.Bgra, (m, n) => Math.Abs(m - n)).Average() < 1);
        }
        // Clipboard (spec 17): the table travels in the packet and pastes as a new table.
        string? packet = AnnotationTransfer.Encode(model, model.Pages[0].Id, [table.Id]);
        Assert.NotNull(packet);
        var clip = AnnotationTransfer.Decode(packet, _ => { });
        var copied = Assert.IsType<TableAnnotation>(Assert.Single(clip.Annotations));
        Assert.Equal(table.Cells.Select(c => c.Text), copied.Cells.Select(c => c.Text));
        var session = new DocumentSession(model);
        var (paste, ids) = clip.Paste(session.Current, model.Pages[0].Id, textBounds: TextLayout.Bounds);
        session.Execute(paste);
        var pasted = Assert.IsType<TableAnnotation>(session.Current.FindAnnotation(ids[0])!.Annotation);
        Assert.NotEqual(table.Id, pasted.Id);
        session.Undo();
        Assert.Null(session.Current.FindAnnotation(ids[0]));
    }
}
