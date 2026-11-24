using System.Globalization;
using System.IO;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Render.Skia;
using PdfNotes.Tests.Pdf;
using PdfNotes.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace PdfNotes.Tests.TextEngine;

/// <summary>
/// 1.4 Task 7: the screen painter and the PDF appearance put a table's cell glyphs in the same place — the place the cell's
/// text box puts them. (Separate from TableAppearanceTests, which also runs in PdfNotes.Cli.Tests without the Skia painter.)
/// </summary>
public sealed class TableAppearanceScreenTests
{
    /// <summary>The start point of every path figure of a content stream, in PDF user space (q/Q and cm followed).</summary>
    private static List<Point2> FigureStarts(string code)
    {
        var starts = new List<Point2>();
        var stack = new Stack<Affine>();
        var ctm = Affine.Identity;
        var operands = new List<double>();
        foreach (string token in code.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) { operands.Add(value); continue; }
            switch (token)
            {
                case "q": stack.Push(ctm); break;
                case "Q": ctm = stack.Pop(); break;
                case "cm": ctm = Affine.FromArray(operands).Then(ctm); break;
                case "m": starts.Add(ctm.Transform(new(operands[0], operands[1]))); break;
            }
            operands.Clear();
        }
        return starts;
    }

    /// <summary>The same figure starts of a text layout's paint, in the order the appearance writes them (clip, fill, stroke).</summary>
    private static void PaintStarts(TextPaint paint, Affine toUser, List<Point2> starts)
    {
        if (paint is TextPaintGroup group)
        {
            var inner = group.Transform.Then(toUser);
            if (group.Clip is { } clip) starts.AddRange(clip.Select(f => inner.Transform(f.Points[0])));
            foreach (var child in group.Children) PaintStarts(child, inner, starts);
            return;
        }
        var shape = (TextPaintShape)paint;
        if (shape.Fill is not null) starts.AddRange(shape.Figures.Select(f => toUser.Transform(f.Points[0])));
        if (shape.Stroke is not null) starts.AddRange(shape.Figures.Select(f => toUser.Transform(f.Points[0])));
    }

    [Fact]
    public void ScreenAndPdfPlaceGlyphsAlike()
    {
        var table = TableAppearanceTests.Sample(Affine.Rigid(0.25, 60, 80));
        var model = TableAppearanceTests.Note(90, table);

        // PDF: every glyph outline of every cell starts where the cell box's own layout puts it (within 0.01pt).
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        var box = PdfPageGeometryReader.Read(pdf)[0].Box;
        string code;
        using (var doc = PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Import))
        {
            var annot = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;
            code = System.Text.Encoding.ASCII.GetString(annot.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Stream.UnfilteredValue);
        }
        var expected = new List<Point2>();
        for (int r = 0; r < table.Rows; r++)
            for (int c = 0; c < table.Columns; c++)
            {
                if (table.Cell(r, c).Text.Length == 0) continue; // an empty cell draws nothing
                var cell = TableLayout.CellBox(table, r, c);
                PaintStarts(TextLayout.Measure(cell).Paint, cell.Transform.Then(box.CanonicalToPdfUser), expected);
            }
        var actual = FigureStarts(code);
        Assert.True(expected.Count > 50 && actual.Count >= expected.Count, $"{expected.Count} glyph figures, {actual.Count} in the appearance");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(Math.Abs(expected[i].X - actual[i].X) < 0.01 && Math.Abs(expected[i].Y - actual[i].Y) < 0.01,
                $"figure {i}: {actual[i]} vs {expected[i]}");
        }

        // Screen: inside a body cell the table paints exactly what the cell's text box paints.
        var page = model.Pages[0];
        var transform = new ViewTransform(page.Setup.Frame, 2, 1, default); // 500pt × 2 × 4/3 < 1400 px
        var info = new SKImageInfo(1400, 1400, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var whole = SKSurface.Create(info);
        using var alone = SKSurface.Create(info);
        whole.Canvas.Clear(SKColors.White); alone.Canvas.Clear(SKColors.White);
        SkiaAnnotationPainter.PaintPage(whole.Canvas, page, transform);
        var body = TableLayout.CellBox(table, 2, 0);
        SkiaAnnotationPainter.Paint(alone.Canvas, body, transform);
        using var a = whole.Snapshot(); using var b = alone.Snapshot();
        using var pa = a.PeekPixels(); using var pb = b.PeekPixels();
        var grid = TableLayout.Measure(table);
        var toDip = table.Transform.Then(transform.PageToDip);
        int inked = 0;
        for (double y = grid.RowY[2] + 1.5; y < grid.RowY[3] - 1.5; y += 0.25)
            for (double x = grid.ColumnX[0] + 1.5; x < grid.ColumnX[1] - 1.5; x += 0.25)
            {
                var p = toDip.Transform(new(x, y));
                var ca = pa.GetPixelColor((int)p.X, (int)p.Y); var cb = pb.GetPixelColor((int)p.X, (int)p.Y);
                Assert.Equal(cb, ca);
                if (cb.Red < 128) inked++;
            }
        Assert.True(inked > 50, "the body cell has text to compare");
    }
}
