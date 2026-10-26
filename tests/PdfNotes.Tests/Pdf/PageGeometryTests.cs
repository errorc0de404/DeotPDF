using System.IO;
using PdfNotes.Core.Geometry;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>Verifies the Core coordinate service against the PDFium renderer on real PDF pages.</summary>
public class PageGeometryTests
{
    /// <summary>D01 sample (0°, 90°+CropBox+UserUnit 2, 270°+CropBox) plus a fourth page at 180° with an odd CropBox.</summary>
    private static string CreateSample(string folder)
    {
        string source = Path.Combine(folder, "source.pdf"), result = Path.Combine(folder, "geometry.pdf");
        ProbeSamplePdf.Create(source, 4);
        using (var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            doc.Pages[3].Rotate = 180;
            doc.Pages[3].CropBox = new PdfRectangle(new XPoint(15.5, 20.25), new XPoint(580.75, 770.125));
            doc.Save(result);
        }
        return result;
    }

    private static void WithSample(Action<string> test)
    {
        string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { test(CreateSample(folder)); }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ReaderMatchesRendererBoxesAndRotations() => WithSample(path =>
    {
        var pages = PdfPageGeometryReader.Read(path);
        Assert.Equal([0, 90, 270, 180], pages.Select(p => p.Rotation));
        Assert.Equal(new PdfPageBox(40, 50, 590, 740, 2), pages[1].Box);
        Assert.Equal(new PdfPageBox(15.5, 20.25, 580.75, 770.125), pages[3].Box);
        using var renderer = new PdfiumDocument(path);
        Assert.InRange(PdfPageGeometryReader.CompareWithRenderer(renderer, pages), 0, 1e-3);
        // The D01 storage prototype measured the same frames.
        var probe = ProbePdfStore.GetMetrics(path);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(probe[i].WidthPt, pages[i].Frame.WidthPt, 9);
            Assert.Equal(probe[i].HeightPt, pages[i].Frame.HeightPt, 9);
            Assert.Equal(probe[i].Rotation, pages[i].Rotation);
        }
    });

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(8.0, 2.0)]
    [InlineData(0.1, 2.0)]
    public void CanonicalToDevicePixelsMatchesPdfiumPageToDevice(double zoom, double dpi) => WithSample(path =>
    {
        var pages = PdfPageGeometryReader.Read(path);
        using var renderer = new PdfiumDocument(path);
        foreach (var page in pages)
        {
            var view = new ViewTransform(page.Frame, zoom, dpi, new(0, 0));
            int width = (int)Math.Round(view.PageWidthDip * dpi), height = (int)Math.Round(view.PageHeightDip * dpi);
            // Rounding the bitmap size can stretch the page by up to half a pixel at the far edge.
            double tolerance = 1.5;
            foreach (var canonical in new Point2[] { new(0, 0), new(page.Frame.WidthPt, 0), new(0, page.Frame.HeightPt),
                         new(page.Frame.WidthPt * 0.31, page.Frame.HeightPt * 0.77), new(12.5, 600) })
            {
                var user = page.Box.CanonicalToPdfUser.Transform(canonical);
                var expected = renderer.PageToPixel(page.PageIndex, new(user.X, user.Y), width, height);
                var actual = view.PageToDevice.Transform(canonical);
                Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance,
                    $"Page {page.PageIndex + 1} ({page.Rotation}°) zoom {zoom} dpi {dpi}: PDFium {expected}, Core {actual}.");
            }
        }
    });

    [Fact]
    public void ExtractedGlyphQuadsBecomeCanonicalPagePoints() => WithSample(path =>
    {
        var pages = PdfPageGeometryReader.Read(path);
        using var renderer = new PdfiumDocument(path);
        var plain = PdfTextReader.ExtractCanonical(renderer, pages[0]);
        var cropped = PdfTextReader.ExtractCanonical(renderer, pages[1]);
        Assert.Contains("원본 본문", string.Concat(plain.Select(c => c.Text)));
        Assert.Equal(plain.Length, cropped.Length);
        // Same content stream; page 2 has CropBox (40,50)-(590,740) and UserUnit 2, page 1 the full 612x792 MediaBox.
        // PDFium orders lines by the rotated reading direction, so page 2 lists the lines in a different order:
        // match each glyph by text and expected position instead of by index.
        int matched = 0;
        foreach (var glyph in plain.Where(c => !string.IsNullOrWhiteSpace(c.Text) && !char.IsDigit(c.Text[0])))
        {
            var expected = new Point2((glyph.Quad.UpperLeft.X - 40) * 2, (glyph.Quad.UpperLeft.Y - 52) * 2);
            var counterpart = cropped.SingleOrDefault(c => c.Text == glyph.Text && c.Quad.UpperLeft.DistanceTo(expected) < 1e-4);
            Assert.NotNull(counterpart);
            Assert.Equal((glyph.Quad.LowerRight.X - 40) * 2, counterpart.Quad.LowerRight.X, 4);
            Assert.Equal((glyph.Quad.LowerRight.Y - 52) * 2, counterpart.Quad.LowerRight.Y, 4);
            matched++;
        }
        Assert.True(matched >= 30, $"Only {matched} glyphs compared.");
        // Canonical +Y is down: the first text line (baseline y=120 from the top) sits above the second (y=154).
        var first = plain.First(c => c.Text == "원");
        Assert.InRange(first.Quad.LowerLeft.Y, 100, 125);
        Assert.True(first.Quad.UpperLeft.Y < first.Quad.LowerLeft.Y);
    });
}
