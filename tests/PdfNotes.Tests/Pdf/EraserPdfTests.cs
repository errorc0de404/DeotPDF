using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Tests.Pdf;

/// <summary>D08 / A07: erased pieces save as v3, reopen editable, and show the background through the erased part.</summary>
public sealed class EraserPdfTests
{
    private static readonly RgbColor Red = new(220, 30, 30);
    private static readonly RgbColor Yellow = new(255, 220, 0);

    private static (byte B, byte G, byte R) Pixel(byte[] pdf, PageSetup setup, Point2 canonical)
    {
        const double scale = 2;
        using var engine = new PdfiumDocument(pdf);
        var page = engine.Render(0, scale, annotations: true);
        var display = setup.Frame.CanonicalToDisplay.Transform(canonical);
        int i = (int)(display.Y * scale) * page.Stride + (int)(display.X * scale) * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }

    private static DocumentModel Erased(int rotation, out InkStroke ink, out FreeHighlightStroke highlight)
    {
        var model = DocumentModel.Create([new PageSetup(400, 300, rotation, PageSource.Generated, PageBackground.Grid)]);
        ink = new InkStroke(Guid.NewGuid(), false, Affine.Rigid(.2, 40, 60), [new(0, 0), new(150, 0), new(300, 20)], 12, Red, 1);
        highlight = new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [new(50, 200), new(350, 200)], 30, Yellow, .5);
        model = model with { Pages = [model.Pages[0] with { Annotations = [ink, highlight] }] };
        var session = new DocumentSession(model);
        var gesture = new EraseGesture(session.Current, model.Pages[0].Id, EraserMode.Area, 24);
        foreach (var p in new Point2[] { new(200, 0), new(200, 300) }) gesture.AddPoint(p); // vertical cut through both
        session.Execute(gesture.Commit());
        var edge = new EraseGesture(session.Current, model.Pages[0].Id, EraserMode.Area, 10);
        foreach (var p in new Point2[] { new(60, 183), new(150, 183) }) edge.AddPoint(p); // only the top edge of the highlighter
        session.Execute(edge.Commit());
        return session.Current;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void ErasedPiecesRoundTripEditableAndShowTheBackgroundThroughTheCut(int rotation)
    {
        var model = Erased(rotation, out var ink, out _);
        Assert.Equal(4, model.Pages[0].Annotations.Count);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model);

        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.Equal(4, read.RestoredCount);
        Assert.Equal(0, read.ReadOnlyCount);
        Assert.All(read.Model.Pages[0].Annotations.Cast<StrokeAnnotation>(), s => Assert.True(s.IsOutlined));
        Assert.Equal(model.Pages[0].Annotations.Select(a => a.Id), read.Model.Pages[0].Annotations.Select(a => a.Id));

        var setup = model.Pages[0].Setup;
        var cut = new Point2(200, 200); // on the highlighter centerline, inside the vertical cut
        var kept = new Point2(300, 205);
        var keptPixel = Pixel(pdf, setup, kept);
        Assert.True(keptPixel.R > 200 && keptPixel.G > 180 && keptPixel.B < 200, $"highlighter piece is drawn: {keptPixel}");
        var cutPixel = Pixel(pdf, setup, cut);
        Assert.True(cutPixel.R > 240 && cutPixel.G > 240 && cutPixel.B > 240, $"background shows through the cut, nothing painted over it: {cutPixel}");
        // A grid line of the page itself runs through the cut (x = 14 · 5mm): it stays visible there, so the cut is
        // a real hole, not a white patch painted over the background.
        var gridLine = Pixel(pdf, setup, new(14 * Units.MillimetersToPoints(5) + .1, 200));
        Assert.True(gridLine.B < 245 && Math.Abs(gridLine.R - gridLine.B) < 25, $"grid line visible inside the cut: {gridLine}");
        var background = Pixel(read.BackgroundPdf, setup, kept);
        Assert.True(background.B > 240, "restored pieces are not part of the background (no double drawing)");
        var edgeCut = Pixel(pdf, setup, new(100, 186));
        Assert.True(edgeCut.B > 240, $"the erased top edge of the highlighter is empty: {edgeCut}");
        var below = Pixel(pdf, setup, new(100, 190));
        Assert.True(below.B < 200, $"just below the erased edge the highlighter remains: {below}");
        // 50% yellow over white once: blue channel ≈ 255 * 0.5, not darker as for a double layer.
        Assert.InRange((int)keptPixel.B, 110, 145);

        var inkPoint = ink.Transform.Transform(new(75, 0));
        Assert.True(Pixel(pdf, setup, inkPoint) is { R: > 180, G: < 90 }, "ink piece is drawn");

        // Reopened pieces stay editable: move one and save again without duplicates.
        var piece = read.Model.Pages[0].Annotations[0];
        var session = new DocumentSession(read.Model);
        session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [AnnotationGeometry.Similarity(piece, default, move: new(5, 5))]));
        byte[] again = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), session.Current);
        EditablePdfWriter.Verify(again, session.Current);
        Assert.Equal(4, EditablePdfReader.Read(again, PdfPageGeometryReader.Read(again)).RestoredCount);
    }
}
