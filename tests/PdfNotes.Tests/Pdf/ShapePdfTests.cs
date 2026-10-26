using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Tests.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>D11: editable PDF save/reopen of shapes, standard appearance (fill and border opacity), fingerprint.</summary>
public sealed class ShapePdfTests
{
    private static DocumentModel Note(int rotation, params Annotation[] annotations)
    {
        var model = DocumentModel.Create([new PageSetup(500, 400, rotation, PageSource.Generated, PageBackground.Blank)]);
        var session = new DocumentSession(model);
        foreach (var a in annotations) session.Execute(DocumentEdits.AddAnnotation(session.Current, model.Pages[0].Id, a));
        return session.Current;
    }

    private static (byte B, byte G, byte R) Pixel(byte[] pdf, PageSetup setup, Point2 canonical)
    {
        const double scale = 2;
        using var engine = new PdfiumDocument(pdf);
        var page = engine.Render(0, scale, annotations: true);
        var display = setup.Frame.CanonicalToDisplay.Transform(canonical);
        int i = (int)(display.Y * scale) * page.Stride + (int)(display.X * scale) * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void ShapesRoundTripWithTheirFillAndBorderVisible(int rotation)
    {
        var shapes = ShapeTests.AllShapes();
        var model = Note(rotation, shapes);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(0, read.ReadOnlyCount);
        Assert.True(ShapeTests.SameMetadata(model, read.Model));
        var setup = model.Pages[0].Setup;
        // Rectangle: 30% transparent light-blue fill over white, dark blue solid border.
        var fill = Pixel(pdf, setup, new(80, 50));
        Assert.InRange(fill.R, 170, 195); Assert.InRange(fill.G, 205, 225); Assert.InRange(fill.B, 238, 250);
        var border = Pixel(pdf, setup, new(80, 20));
        Assert.True(border.B < 170 && border.R < 80, $"{border}");
        // Triangle: opaque fill without border; its box corner stays white.
        var solid = Pixel(pdf, setup, new(350, 90));
        Assert.True(solid.R < 170 && solid.B > 220, $"{solid}");
        var corner = Pixel(pdf, setup, new(305, 25));
        Assert.True(corner.R > 245 && corner.G > 245 && corner.B > 245, $"{corner}");
        // Hollow polygon: its inside stays white.
        var inside = Pixel(pdf, setup, new(100, 230));
        Assert.True(inside.R > 245 && inside.G > 245 && inside.B > 245, $"{inside}");
        // The editable copy shows no app appearance under the restored model (no double drawing).
        using var engine = new PdfiumDocument(read.BackgroundPdf);
        var background = engine.Render(0, 1, annotations: true);
        Assert.All(Enumerable.Range(0, background.Bgra.Length / 4), i => Assert.True(background.Bgra[i * 4] > 240 || background.Bgra[i * 4 + 3] == 0));
    }

    [Fact]
    public void AChangedShapeAppearanceIsKeptReadOnly()
    {
        var model = Note(0, ShapeTests.AllShapes()[0]);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using (var input = new MemoryStream(pdf)) using (var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var annot = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;
            var states = annot.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ExtGState")!;
            states.Elements.GetDictionary("/GF")!.Elements.SetReal("/ca", .05);
            using var output = new MemoryStream(); doc.Save(output, false); pdf = output.ToArray();
        }
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(1, read.ReadOnlyCount);
        Assert.Empty(read.Model.AllAnnotations);
    }

    [Fact]
    public void SavedShapesUseSchemaV5()
    {
        var model = Note(0, ShapeTests.AllShapes()[1]);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using var stream = new MemoryStream(pdf);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        var header = doc.Internals.Catalog.Elements.GetDictionary(EditablePdfKeys.Header)!;
        var text = System.Text.Encoding.UTF8.GetString(header.Stream.UnfilteredValue);
        Assert.Contains("\"schemaVersion\":6", text); // the writer uses the newest schema (v6 since D13i)
    }
}
