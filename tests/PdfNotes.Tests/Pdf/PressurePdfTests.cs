using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Tests.Pdf;

/// <summary>P3 T2: a pressure stroke's PDF appearance is its tapered shape, and it reopens editable with its pressures (schema v8).</summary>
public sealed class PressurePdfTests
{
    private static (byte B, byte G, byte R) Pixel(byte[] pdf, PageSetup setup, Point2 canonical)
    {
        const double scale = 4;
        using var engine = new PdfiumDocument(pdf);
        var page = engine.Render(0, scale, annotations: true);
        var display = setup.Frame.CanonicalToDisplay.Transform(canonical);
        int i = (int)(display.Y * scale) * page.Stride + (int)(display.X * scale) * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void APressureStrokeIsDrawnTaperedAndReopensWithItsPressures(int rotation)
    {
        var model = DocumentModel.Create([new PageSetup(400, 300, rotation, PageSource.Generated, PageBackground.Blank)]);
        // Width 10: half-width 2 at the thin end (pressure 0), 8 at the thick end (pressure 1).
        var stroke = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(50, 150), new(350, 150)], 10, new(200, 20, 20), 1) { Pressures = [0, 1] };
        model = model with { Pages = [model.Pages[0] with { Annotations = [stroke] }] };
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model);

        var setup = model.Pages[0].Setup;
        Assert.True(Pixel(pdf, setup, new(345, 156)).G < 120, "the thick end reaches 8pt off the line");
        Assert.True(Pixel(pdf, setup, new(55, 156)).G > 230, "the thin end does not");
        Assert.True(Pixel(pdf, setup, new(55, 150.5)).G < 120, "but it is drawn on the line");

        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.Equal(0, read.ReadOnlyCount);
        var back = Assert.IsType<InkStroke>(Assert.Single(read.Model.Pages[0].Annotations));
        Assert.Equal([0, 1], back.Pressures.ToArray());

        // Flatten bakes the same shape into the page.
        byte[] flat = FlattenWriter.Flatten(pdf).Pdf;
        Assert.True(Pixel(flat, setup, new(345, 156)).G < 120);
        Assert.True(Pixel(flat, setup, new(55, 156)).G > 230);
    }
}
