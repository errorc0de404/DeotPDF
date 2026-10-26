using System.IO;
using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfNotes.Tests.Pdf;

/// <summary>D09 / A09–A10: glyph highlights on real extracted text (rotation, crop offset), save/reopen, single opacity.</summary>
public sealed class TextHighlightPdfTests : IDisposable
{
    private static readonly RgbColor Yellow = new(255, 230, 0);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public TextHighlightPdfTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private static int Find(IReadOnlyList<TextGlyph> glyphs, string text) =>
        string.Concat(glyphs.Select(g => g.Text.Length == 1 ? g.Text : "�")).IndexOf(text, StringComparison.Ordinal);

    private static (byte B, byte G, byte R) Pixel(RenderedPage page, PageSetup setup, Point2 canonical, double scale)
    {
        var display = setup.Frame.CanonicalToDisplay.Transform(canonical);
        int i = (int)(display.Y * scale) * page.Stride + (int)(display.X * scale) * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }

    private static bool Same(DocumentModel a, DocumentModel b) =>
        SchemaV3.Serialize(a).Annotations.Select(x => Convert.ToBase64String(x.Envelope)).SequenceEqual(SchemaV3.Serialize(b).Annotations.Select(x => Convert.ToBase64String(x.Envelope)));

    [Fact]
    public void HighlightsOnExtractedTextFollowRotationAndCropAndRoundTrip()
    {
        string path = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(path, 3); // page 2: crop offset + /Rotate 90 + UserUnit 2, page 3: crop offset + /Rotate 270
        using var source = PdfSourceDocument.Open(path);
        var model = source.CreateModel();
        var session = new DocumentSession(model);
        var created = new List<PdfTextHighlight>();
        for (int i = 0; i < 3; i++)
        {
            var glyphs = PdfTextReader.Glyphs(source.Engine, source.Pages[i]);
            int start = Find(glyphs, "Original");
            Assert.True(start >= 0, $"page {i + 1} text: {TextSelection.TextOf(glyphs, 0, glyphs.Length)}");
            Assert.All(glyphs.Skip(start).Take(8), g => Assert.Equal(1, g.Direction.X, 3)); // horizontal in canonical points on every page rotation
            var quad = Assert.Single(TextSelection.LineQuads(glyphs, start, start + 8));
            Assert.All(glyphs.Skip(start).Take(8), g => Assert.True(TextSelection.InQuad(quad, g.Center)));
            if (i == 0)
            {
                // Drawn after "원본 본문 / " from x = 70 on the baseline y = 120 (no crop, no rotation).
                Assert.InRange(quad.LowerLeft.Y, 115, 130);
                Assert.InRange(quad.UpperLeft.X, 70, 220);
            }
            var h = new PdfTextHighlight(Guid.NewGuid(), false, [quad], TextSelection.TextOf(glyphs, start, start + 8), HighlightHeightMode.Auto, null, Yellow, .4);
            created.Add(h);
            session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[i].Id, h));
        }
        Assert.Equal("Original", created[0].SourceTextHint);

        byte[] pdf = EditablePdfWriter.Write(source.Bytes, source.Pages, session.Current);
        EditablePdfWriter.Verify(pdf, session.Current);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(3, read.RestoredCount);
        Assert.Equal(0, read.ReadOnlyCount);
        Assert.True(Same(session.Current, read.Model));

        // Another renderer shows the highlight over the glyphs; the reopened background no longer contains it.
        using var full = new PdfiumDocument(pdf);
        using var background = new PdfiumDocument(read.BackgroundPdf);
        foreach (int i in new[] { 0, 2 })
        {
            var setup = session.Current.Pages[i].Setup;
            var q = created[i].Quads[0];
            var inside = new Point2((q.UpperLeft.X + q.UpperRight.X) / 2, q.UpperLeft.Y + 1.5); // above the glyph ink, inside the box
            var shown = Pixel(full.Render(i, 2), setup, inside, 2);
            var hidden = Pixel(background.Render(i, 2), setup, inside, 2);
            Assert.True(shown.B < 200 && shown.R > 200 && shown.G > 190, $"page {i + 1}: highlight visible {shown}");
            Assert.True(hidden.B > 240, $"page {i + 1}: restored highlight not drawn twice {hidden}");
        }
    }

    [Fact]
    public void OverlappingManualLinesAreCompositedOnce()
    {
        string path = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(path, 1);
        using var source = PdfSourceDocument.Open(path);
        var glyphs = PdfTextReader.Glyphs(source.Engine, source.Pages[0]);
        var all = TextSelection.LineQuads(glyphs, 0, glyphs.Length);
        Assert.Equal(2, all.Length); // the two text lines of the sample
        var h = new PdfTextHighlight(Guid.NewGuid(), false, all, null, HighlightHeightMode.Manual, 40, Yellow, .5);
        var model = source.CreateModel();
        model = model with { Pages = [model.Pages[0] with { Annotations = [h] }] };
        byte[] pdf = EditablePdfWriter.Write(source.Bytes, source.Pages, model);
        EditablePdfWriter.Verify(pdf, model);
        double c0 = (all[0].UpperLeft.Y + all[0].LowerLeft.Y) / 2, c1 = (all[1].UpperLeft.Y + all[1].LowerLeft.Y) / 2;
        Assert.True(c1 - c0 < 40, "bands overlap between the lines");
        double x = all[1].UpperLeft.X + 30;
        using var engine = new PdfiumDocument(pdf);
        var page = engine.Render(0, 2);
        var setup = model.Pages[0].Setup;
        var overlap = Pixel(page, setup, new(x, (c0 + c1) / 2), 2);
        var single = Pixel(page, setup, new(x, c0 - 18), 2);
        Assert.True(Math.Abs(overlap.B - single.B) <= 3 && overlap.B is > 110 and < 145, $"overlap {overlap} vs single band {single}");
    }

    [Fact]
    public void RotatedTextGetsItsOwnOrientation()
    {
        string sample = Path.Combine(folder, "sample.pdf");
        ProbeSamplePdf.Create(sample, 1); // registers the sample font resolver
        string path = Path.Combine(folder, "rotated.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(400); page.Height = XUnit.FromPoint(400);
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                gfx.RotateAtTransform(-30, new XPoint(100, 300)); // counterclockwise on the page
                gfx.DrawString("Rotated text", new XFont("D01-Malgun", 20, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)),
                    XBrushes.Black, new XPoint(100, 300));
            }
            doc.Save(path);
        }
        using var source = PdfSourceDocument.Open(path);
        var glyphs = PdfTextReader.Glyphs(source.Engine, source.Pages[0]);
        int start = Find(glyphs, "Rotated");
        Assert.True(start >= 0);
        var expected = new Point2(Math.Cos(Math.PI / 6), -Math.Sin(Math.PI / 6));
        Assert.All(glyphs.Skip(start).Take(7), g => Assert.True(g.Direction.DistanceTo(expected) < .02, $"{g.Text}: {g.Direction}"));
        var quad = Assert.Single(TextSelection.LineQuads(glyphs, start, start + 7));
        var top = new Point2(quad.UpperRight.X - quad.UpperLeft.X, quad.UpperRight.Y - quad.UpperLeft.Y);
        double length = Math.Sqrt(top.X * top.X + top.Y * top.Y);
        Assert.True(new Point2(top.X / length, top.Y / length).DistanceTo(expected) < .02, "the quad's top edge follows the text");
        Assert.True(quad.UpperLeft.Y < quad.LowerLeft.Y, "upper side is above (towards -Y) for upright rotated text");
        Assert.All(glyphs.Skip(start).Take(7), g => Assert.True(TextSelection.InQuad(quad, g.Center)));
    }

    [Fact]
    public void TextBoxHighlightsArePaintedBehindTheGlyphsAndReopen()
    {
        var model = DocumentModel.Create([new PageSetup(400, 300, 90, PageSource.Generated, PageBackground.Blank)]);
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Rigid(.3, 60, 80), "강조 텍스트 highlight",
            [new TextRun(0, 16, "Malgun Gothic", 20, RgbColor.Black, false, false, false, false)], TextAlignment.Left, 260, TextBoxStyle.Default, []);
        box = TextHighlights.Add(box, 0, 2, new TextHighlightRange(0, 0, HighlightHeightMode.Auto, null, Yellow, .4));
        box = TextHighlights.Add(box, 7, 9, new TextHighlightRange(0, 0, HighlightHeightMode.Manual, 6, new(0, 200, 255), .5));
        model = model with { Pages = [model.Pages[0] with { Annotations = [box] }] };
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        var restored = Assert.IsType<TextBoxAnnotation>(Assert.Single(read.Model.AllAnnotations));
        Assert.True(box.Highlights.SequenceEqual(restored.Highlights), string.Join(" | ", restored.Highlights));

        // Pixel at the gap between "강" and "조" glyph ink is not reliable; sample the box space just inside the
        // highlight's top edge, where there is no ink: it is yellow over white, composited once (B ≈ 255 · 0.6).
        var lines = TextLayout.HighlightLines(box, TextLayout.Measure(box).Characters).ToArray();
        Assert.Equal(2, lines.Length);
        var auto = lines[0].Lines[0];
        var at = box.Transform.Transform(new(auto.X + auto.Width / 2, auto.Y + 1));
        using var engine = new PdfiumDocument(pdf);
        var p = Pixel(engine.Render(0, 2), model.Pages[0].Setup, at, 2);
        Assert.True(p.R > 240 && p.G > 225 && p.B is > 135 and < 175, $"text box highlight {p}");
        var manual = lines[1].Lines[0];
        Assert.Equal(6, manual.Height, 6);
    }
}
