using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>D07: editable PDF save/reopen of lines and curves, visible appearance, single opacity, fingerprint.</summary>
public sealed class LinePdfTests
{
    private static readonly RgbColor Blue = new(20, 60, 220);

    private static LineAnnotation Line(LineKind kind, Point2[] polygon, double width = 4, DashStyle dash = DashStyle.Solid,
        bool startArrow = false, bool endArrow = false, double opacity = 1, Affine? transform = null) =>
        LineGeometry.WithPolygon(new LineAnnotation(Guid.NewGuid(), false, transform ?? Affine.Identity, LineKind.Straight, polygon[0], polygon[^1], [],
            width, dash, startArrow, endArrow, Blue, opacity), polygon) is { } line && line.LineKind == kind ? line : throw new ArgumentException("kind");

    private static DocumentModel Note(int rotation, params Annotation[] annotations)
    {
        var model = DocumentModel.Create([new PageSetup(400, 300, rotation, PageSource.Generated, PageBackground.Blank)]);
        var session = new DocumentSession(model);
        foreach (var a in annotations) session.Execute(DocumentEdits.AddAnnotation(session.Current, model.Pages[0].Id, a));
        return session.Current;
    }

    private static (byte B, byte G, byte R) Pixel(byte[] pdf, PageSetup setup, Point2 canonical, bool annotations = true)
    {
        const double scale = 2;
        using var engine = new PdfiumDocument(pdf);
        var page = engine.Render(0, scale, annotations: annotations);
        var display = setup.Frame.CanonicalToDisplay.Transform(canonical);
        int i = (int)(display.Y * scale) * page.Stride + (int)(display.X * scale) * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }

    private static bool IsWhite((byte B, byte G, byte R) p) => p.B > 245 && p.G > 245 && p.R > 245;

    private static bool SameMetadata(DocumentModel a, DocumentModel b)
    {
        var x = SchemaV2.Serialize(a);
        var y = SchemaV2.Serialize(b);
        return x.Header.AsSpan().SequenceEqual(y.Header) && x.Annotations.Length == y.Annotations.Length &&
            x.Annotations.Zip(y.Annotations).All(p => p.First.Envelope.AsSpan().SequenceEqual(p.Second.Envelope));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public void LinesAndCurvesRoundTripWithVisibleAppearanceAndHiddenBackground(int rotation)
    {
        var straight = Line(LineKind.Straight, [new(40, 40), new(360, 40)], dash: DashStyle.Solid, startArrow: true, endArrow: true);
        var quadratic = Line(LineKind.Quadratic, [new(40, 120), new(200, 60), new(360, 120)], dash: DashStyle.Dash, endArrow: true, opacity: .6);
        var cubic = Line(LineKind.Cubic, [new(0, 0), new(60, -60), new(120, 60), new(180, 0)], width: 3, dash: DashStyle.DashDot,
            transform: Affine.Rigid(.4, 120, 200));
        var model = Note(rotation, straight, quadratic, cubic);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model);

        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(3, read.RestoredCount);
        Assert.Equal(0, read.ReadOnlyCount);
        Assert.True(SameMetadata(model, read.Model), "ids, kinds, points, dash, arrows and style are restored exactly");

        var setup = model.Pages[0].Setup;
        var middle = new Point2(200, 40);
        var drawn = Pixel(pdf, setup, middle);
        Assert.True(drawn.B > 150 && drawn.R < 120, $"line is drawn by another renderer: {drawn}");
        Assert.True(IsWhite(Pixel(read.BackgroundPdf, setup, middle)), "restored line is not part of the background (no double drawing)");
        var curvePoint = LineGeometry.Evaluate(LineGeometry.ControlPolygon(quadratic), .5);
        Assert.False(IsWhite(Pixel(pdf, setup, curvePoint)), "the curve passes through its Bézier point, not its control point");
        Assert.True(IsWhite(Pixel(pdf, setup, quadratic.Controls[0])));
    }

    [Fact]
    public void RepeatedSavesKeepOneAppearancePerLine()
    {
        var model = Note(0, Line(LineKind.Cubic, [new(20, 20), new(80, 200), new(200, -50), new(300, 150)], endArrow: true));
        byte[] first = EditablePdfWriter.Write(null, null, model);
        var reopened = EditablePdfReader.Read(first, PdfPageGeometryReader.Read(first));
        byte[] second = EditablePdfWriter.Write(reopened.BackgroundPdf, PdfPageGeometryReader.Read(reopened.BackgroundPdf), reopened.Model);
        Assert.Equal(AnnotationCount(first), AnnotationCount(second));
        Assert.Equal(1, AnnotationCount(second));
        Assert.True(SameMetadata(model, EditablePdfReader.Read(second, PdfPageGeometryReader.Read(second)).Model));
    }

    [Fact]
    public void ReopenedLinesCanBeEditedAndSavedOverTheOriginalFile()
    {
        // The app saves from the original bytes (with our annotations), like OpenDocument.SaveAsync.
        var cubic = Line(LineKind.Cubic, [new(450, 120), new(500, -20), new(560, 260), new(620, 120)], endArrow: true);
        var model = Note(0, Line(LineKind.Straight, [new(10, 10), new(200, 90)]), cubic);
        byte[] first = EditablePdfWriter.Write(null, null, model);
        var pages = PdfPageGeometryReader.Read(first);
        var reopened = EditablePdfReader.Read(first, pages).Model;
        var edited = (LineAnnotation)reopened.FindAnnotation(cubic.Id)!.Annotation;
        edited = LineGeometry.Convert(edited, LineKind.Quadratic) with { Dash = DashStyle.Dot, StartArrow = true };
        var session = new DocumentSession(reopened);
        session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [edited]));
        byte[] second = EditablePdfWriter.Write(first, pages, session.Current);
        EditablePdfWriter.Verify(second, session.Current);
        Assert.Equal(2, AnnotationCount(second));
    }

    [Fact]
    public void ArbitraryCoordinatesAndOpacitiesStillMatchTheirFingerprintAfterSaving()
    {
        // Irrational bounds and alphas: a real rounded twice (7 then 6 decimals) would make some appearances look changed.
        var random = new Random(7);
        double R(double scale) => random.NextDouble() * scale;
        var lines = Enumerable.Range(0, 40).Select(_ => (Annotation)Line(LineKind.Cubic,
            [new(R(380) + Math.PI, R(280) + Math.E), new(R(400), R(300)), new(R(400), R(300)), new(R(380) + 1 / 3.0, R(280) + Math.Sqrt(2))],
            width: 0.5 + R(8), startArrow: true, endArrow: true, opacity: 0.05 + R(0.9), transform: Affine.Rigid(R(6), R(10), R(10)))).ToArray();
        var text = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Rigid(1 / 7.0, Math.PI * 10, Math.E * 10), "한글 text",
            [new TextRun(0, 7, "Malgun Gothic", 13.37, new(10, 20, 30), false, false, false, false)], TextAlignment.Left, 123.456789,
            new TextBoxStyle(new(250, 250, 0), 0.3333333, true, new(0, 0, 255), 0.7777777), []);
        var model = Note(0, [.. lines, text]);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(41, read.RestoredCount);
        Assert.Equal(0, read.ReadOnlyCount);
    }

    [Fact]
    public void OpacityIsAppliedOnceOverBodyAndArrowHead()
    {
        // Black, 50% opacity: the body runs into the head, but the overlap must not be darker than the body alone.
        var line = Line(LineKind.Straight, [new(40, 150), new(360, 150)], width: 8, endArrow: true, opacity: .5) with { Color = RgbColor.Black };
        var model = Note(0, line);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        var setup = model.Pages[0].Setup;
        double length = LineGeometry.ArrowLength(line.WidthPt);
        var body = Pixel(pdf, setup, new(200, 150));
        var overlap = Pixel(pdf, setup, new(360 - length * .75, 150));
        var headOnly = Pixel(pdf, setup, new(360 - length * .35, 150));
        Assert.InRange(body.G, 100, 155);
        Assert.InRange(Math.Abs(overlap.G - body.G), 0, 10);
        Assert.InRange(Math.Abs(headOnly.G - body.G), 0, 10);
    }

    [Fact]
    public void DashedLinesLeaveGapsAndSolidLinesDoNot()
    {
        var solid = Line(LineKind.Straight, [new(20, 60), new(380, 60)], width: 3);
        var dashed = Line(LineKind.Straight, [new(20, 160), new(380, 160)], width: 3, dash: DashStyle.Dash);
        var model = Note(0, solid, dashed);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using var engine = new PdfiumDocument(pdf);
        var page = engine.Render(0, 2);
        bool Inked(double x, double y) { int i = (int)(y * 2) * page.Stride + (int)(x * 2) * 4; return page.Bgra[i + 2] < 200; }
        var samples = Enumerable.Range(0, 300).Select(i => 40 + i).ToArray();
        Assert.All(samples, x => Assert.True(Inked(x, 60)));
        int dashInk = samples.Count(x => Inked(x, 160));
        Assert.InRange(dashInk, 150, 280);
    }

    [Fact]
    public void ChangedLineDrawingIsKeptReadOnly()
    {
        var model = Note(0, Line(LineKind.Quadratic, [new(40, 120), new(200, 20), new(360, 120)], endArrow: true));
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using var input = new MemoryStream(pdf);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var annot = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)document.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;
        var group = annot.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Elements.GetDictionary("/Resources")!
            .Elements.GetDictionary("/XObject")!.Elements.GetDictionary("/L")!;
        group.Elements.Remove("/Filter");
        group.Stream.Value = "q 1 0 0 rg 0 0 400 300 re f Q\n"u8.ToArray();
        using var output = new MemoryStream();
        document.Save(output, false);
        byte[] changed = output.ToArray();
        var read = EditablePdfReader.Read(changed, PdfPageGeometryReader.Read(changed));
        Assert.Equal(0, read.RestoredCount);
        Assert.Equal(1, read.ReadOnlyCount);
    }

    private static int AnnotationCount(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return document.Pages[0].Elements.GetArray("/Annots")?.Elements.Count ?? 0;
    }
}
