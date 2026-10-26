using System.Collections.Immutable;
using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>D04: editable PDF save/reopen of pen and highlighter strokes, appearance, read-only fallback, safe replace.</summary>
public sealed class EditablePdfTests : IDisposable
{
    private static readonly RgbColor Red = new(220, 30, 30);
    private static readonly RgbColor Yellow = new(255, 230, 0);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public EditablePdfTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(folder)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, true);
    }

    private static InkStroke Ink(params Point2[] points) =>
        new(Guid.NewGuid(), false, Affine.Identity, [.. points], 4, Red, 1);

    private static FreeHighlightStroke Highlight(double opacity, params Point2[] points) =>
        new(Guid.NewGuid(), false, Affine.Identity, [.. points], 14, Yellow, opacity);

    private static DocumentModel With(DocumentModel model, int page, params Annotation[] annotations)
    {
        foreach (var a in annotations) model = Apply(model, DocumentEdits.AddAnnotation(model, model.Pages[page].Id, a));
        return model;
    }

    private static DocumentModel Apply(DocumentModel model, EditCommand command)
    {
        var session = new DocumentSession(model);
        session.Execute(command);
        return session.Current;
    }

    private static DocumentModel NewNote() => DocumentModel.Create([
        new PageSetup(Units.MillimetersToPoints(210), Units.MillimetersToPoints(297), 0, PageSource.Generated, PageBackground.Grid),
        new PageSetup(Units.MillimetersToPoints(210), Units.MillimetersToPoints(297), 90, PageSource.Generated, PageBackground.Lined)]);

    private static bool SameMetadata(DocumentModel a, DocumentModel b)
    {
        var x = SchemaV1.Serialize(a);
        var y = SchemaV1.Serialize(b);
        return x.Header.AsSpan().SequenceEqual(y.Header) && x.Annotations.Length == y.Annotations.Length &&
            x.Annotations.Zip(y.Annotations).All(p => p.First.Envelope.AsSpan().SequenceEqual(p.Second.Envelope));
    }

    /// <summary>BGRA of the displayed page pixel under a canonical point, rendered like a third-party viewer (annotations on).</summary>
    private static (byte B, byte G, byte R) PixelAt(byte[] pdf, int pageIndex, PageSetup setup, Point2 canonical, double scale = 2, double userUnit = 1)
    {
        using var engine = new PdfiumDocument(pdf);
        var page = engine.Render(pageIndex, scale, annotations: true, userUnit: userUnit);
        var display = setup.Frame.CanonicalToDisplay.Transform(canonical);
        int x = (int)(display.X * scale), y = (int)(display.Y * scale);
        int i = y * page.Stride + x * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }

    private static PdfDictionary? Resolve(PdfItem item) => item is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;

    private static bool IsWhite((byte B, byte G, byte R) p) => p.B > 245 && p.G > 245 && p.R > 245;

    private static int OwnAnnotationCount(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        int count = 0;
        foreach (PdfPage page in document.Pages)
            if (page.Elements.GetArray("/Annots") is { } annots)
                for (int i = 0; i < annots.Elements.Count; i++)
                    if (Resolve(annots.Elements[i])?.Elements.GetString("/NM").StartsWith(EditablePdfKeys.NamePrefix) == true) count++;
        return count;
    }

    [Fact]
    public void NewNoteStrokesRoundTripExactlyAndAreVisibleToOtherViewers()
    {
        var dot = Ink(new Point2(300, 300));
        var ink = Ink(new(50, 60), new(120.125, 64.5), new(200, 140.333333333));
        var marker = Highlight(0.4, new(60, 400), new(260, 400));
        var rotated = Ink(new(100, 100), new(160, 130));
        var model = With(With(NewNote(), 0, ink, marker, dot), 1, rotated);

        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model); // throws on any difference

        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.Equal(4, read.RestoredCount);
        Assert.Equal(0, read.ReadOnlyCount);
        Assert.True(SameMetadata(model, read.Model));
        Assert.Equal(model.Id, read.Model.Id);
        Assert.Equal(PageSource.Generated, read.Model.Pages[1].Setup.Source); // v4 retains paper background identity
        Assert.Equal(PageBackgroundKind.Lined, read.Model.Pages[1].Setup.Background.Kind);

        // Other viewers draw the appearance (also on the 90° page and for a single click dot) …
        Assert.Equal((Red.B, Red.G, Red.R), Round(PixelAt(pdf, 0, model.Pages[0].Setup, new(120.125, 64.5))));
        Assert.Equal((Red.B, Red.G, Red.R), Round(PixelAt(pdf, 0, model.Pages[0].Setup, new(300, 300))));
        Assert.Equal((Red.B, Red.G, Red.R), Round(PixelAt(pdf, 1, model.Pages[1].Setup, new(130, 115))));
        // … while the app's background has them removed, so reopening does not draw them twice.
        Assert.True(IsWhite(PixelAt(read.BackgroundPdf, 0, model.Pages[0].Setup, new(120.125, 64.5))));
        Assert.True(IsWhite(PixelAt(read.BackgroundPdf, 1, model.Pages[1].Setup, new(130, 115))));
    }

    private static (byte, byte, byte) Round((byte B, byte G, byte R) p) => (Snap(p.B, Red.B), Snap(p.G, Red.G), Snap(p.R, Red.R));
    private static byte Snap(byte value, byte expected) => Math.Abs(value - expected) <= 3 ? expected : value;

    [Fact]
    public void HighlighterSelfOverlapIsCompositedOnceButSeparateStrokesDarken()
    {
        // Goes right to x=300 and back to x=150: x 150..300 is covered twice by the same stroke.
        var back = Highlight(0.4, new(100, 200), new(300, 200), new(150, 200));
        var first = Highlight(0.4, new(100, 300), new(300, 300));
        var second = Highlight(0.4, new(200, 300), new(400, 300));
        var model = With(NewNote(), 0, back, first, second);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        var setup = model.Pages[0].Setup;
        var once = PixelAt(pdf, 0, setup, new(120, 200));
        var self = PixelAt(pdf, 0, setup, new(220, 200));
        var single = PixelAt(pdf, 0, setup, new(150, 300));
        var both = PixelAt(pdf, 0, setup, new(250, 300));
        Assert.InRange(Math.Abs(once.B - self.B), 0, 2);
        Assert.InRange(once.B, 140, 165); // 255·(1-0.4) = 153 on white
        Assert.InRange(Math.Abs(single.B - once.B), 0, 2);
        Assert.InRange(both.B, 80, 100);  // two separate strokes: 255·0.6·0.6 = 92
    }

    [Fact]
    public void StrokesOnRotatedCroppedAndUserUnitPagesLandWhereTheModelSays()
    {
        string mixed = Path.Combine(folder, "mixed.pdf");
        ProbeSamplePdf.CreateMixed(mixed);
        byte[] source = File.ReadAllBytes(mixed);
        var pages = PdfPageGeometryReader.Read(source);
        var model = EditablePdfReader.Read(source, pages).Model;
        for (int i = 0; i < model.Pages.Count; i++) model = With(model, i, Ink(new(40, 50), new(90, 70)));

        byte[] pdf = EditablePdfWriter.Write(source, pages, model);
        EditablePdfWriter.Verify(pdf, model);
        var written = PdfPageGeometryReader.Read(pdf);
        for (int i = 0; i < model.Pages.Count; i++)
        {
            var setup = model.Pages[i].Setup;
            Assert.Equal(setup.Frame, written[i].Frame);
            var p = PixelAt(pdf, i, setup, new(65, 60), scale: 1, userUnit: written[i].Box.UserUnit);
            Assert.True(p.R > 150 && p.G < 120 && p.B < 120, $"page {i + 1}: {p}");
            var outside = PixelAt(pdf, i, setup, new(65, 85), scale: 1, userUnit: written[i].Box.UserUnit);
            Assert.False(outside.R > 150 && outside.G < 120, $"page {i + 1} stroke drawn too wide");
        }
    }

    [Fact]
    public void RepeatedSavesDoNotAccumulateAnnotationsOrGrowTheFile()
    {
        var model = With(NewNote(), 0, Ink(new(10, 10), new(100, 100)), Highlight(0.4, new(50, 50), new(150, 50)));
        byte[] current = EditablePdfWriter.Write(null, null, model);
        var sizes = new List<long>();
        for (int i = 0; i < 4; i++)
        {
            var geometry = PdfPageGeometryReader.Read(current);
            var reopened = EditablePdfReader.Read(current, geometry).Model;
            Assert.True(SameMetadata(model, reopened));
            current = EditablePdfWriter.Write(current, geometry, reopened);
            Assert.Equal(2, OwnAnnotationCount(current));
            sizes.Add(current.Length);
        }
        Assert.InRange(sizes.Max() - sizes.Min(), 0, 64);
    }

    [Fact]
    public void ForeignAnnotationsAndUnverifiableOwnDataStayVisibleAndReadOnly()
    {
        var keep = Ink(new(50, 50), new(150, 50));
        var tamper = Ink(new(50, 200), new(150, 200));
        var model = With(NewNote(), 0, keep, tamper);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);

        // Another app recolours one stroke's appearance and adds its own annotation.
        using var stream = new MemoryStream();
        using (var input = new MemoryStream(pdf))
        using (var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var annots = document.Pages[0].Elements.GetArray("/Annots")!;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var annot = Resolve(annots.Elements[i])!;
                if (annot.Elements.GetString("/NM") != EditablePdfKeys.NamePrefix + tamper.Id.ToString("D")) continue;
                var form = annot.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!;
                string code = System.Text.Encoding.ASCII.GetString(form.Stream.UnfilteredValue).Replace("0.862745 0.117647 0.117647 RG", "0 0 1 RG");
                form.Elements.Remove("/Filter");
                form.Stream.Value = System.Text.Encoding.ASCII.GetBytes(code);
            }
            // A foreign stamp with its own appearance: a filled green square at user space 300..400.
            var squareForm = new PdfDictionary(document);
            squareForm.CreateStream(System.Text.Encoding.ASCII.GetBytes("0 0.6 0 rg 300 300 100 100 re f"));
            squareForm.Elements.SetName("/Type", "/XObject");
            squareForm.Elements.SetName("/Subtype", "/Form");
            squareForm.Elements["/BBox"] = new PdfArray(document, new PdfReal(300), new PdfReal(300), new PdfReal(400), new PdfReal(400));
            document.Internals.AddObject(squareForm);
            var ap = new PdfDictionary(document);
            ap.Elements["/N"] = squareForm.Reference!;
            var foreign = new PdfDictionary(document);
            foreign.Elements.SetName("/Type", "/Annot");
            foreign.Elements.SetName("/Subtype", "/Stamp");
            foreign.Elements.SetString("/NM", "other-app-1");
            foreign.Elements["/Rect"] = new PdfArray(document, new PdfReal(300), new PdfReal(300), new PdfReal(400), new PdfReal(400));
            foreign.Elements["/AP"] = ap;
            foreign.Elements.SetInteger("/F", 4);
            document.Internals.AddObject(foreign);
            annots.Elements.Add(foreign.Reference!);
            document.Save(stream, false);
        }
        byte[] edited = stream.ToArray();

        var geometry = PdfPageGeometryReader.Read(edited);
        var read = EditablePdfReader.Read(edited, geometry);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.Equal(1, read.RestoredCount);
        Assert.Equal(1, read.ReadOnlyCount);
        Assert.Equal(keep.Id, Assert.Single(read.Model.Pages[0].Annotations).Id);
        var setup = model.Pages[0].Setup;
        var blue = PixelAt(read.BackgroundPdf, 0, setup, new(100, 200));
        Assert.True(blue.B > 200 && blue.R < 60, $"tampered stroke must stay visible: {blue}");
        Assert.True(IsWhite(PixelAt(read.BackgroundPdf, 0, setup, new(100, 50))));

        // Saving keeps the read-only appearance and the foreign annotation; the restored stroke is written once.
        byte[] resaved = EditablePdfWriter.Write(edited, geometry, read.Model);
        var again = EditablePdfReader.Read(resaved, PdfPageGeometryReader.Read(resaved));
        Assert.Equal(1, again.RestoredCount);
        Assert.Equal(1, again.ReadOnlyCount);
        Assert.True(PixelAt(again.BackgroundPdf, 0, setup, new(100, 200)).B > 200);
        var square = PixelAt(again.BackgroundPdf, 0, setup, PdfUser(setup, 350, 350));
        Assert.True(square.G > 100 && square.R < 60, $"foreign annotation lost: {square}");
    }

    private static Point2 PdfUser(PageSetup setup, double x, double y) => new(x, setup.HeightPt - y);

    [Fact]
    public void MissingHeaderMisplacedOrUnsupportedDataIsNotRestored()
    {
        var a = Ink(new(50, 50), new(150, 50));
        var b = Ink(new(50, 100), new(150, 100));
        var model = With(NewNote(), 0, a, b);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);

        byte[] noHeader = Modify(pdf, d => d.Internals.Catalog.Elements.Remove(EditablePdfKeys.Header));
        var read = EditablePdfReader.Read(noHeader, PdfPageGeometryReader.Read(noHeader));
        Assert.Equal(HeaderStatus.Invalid, read.HeaderStatus);
        Assert.Equal(2, read.ReadOnlyCount);
        Assert.All(read.Model.Pages, p => Assert.Empty(p.Annotations));
        Assert.Same(noHeader, read.BackgroundPdf);

        // A stroke moved to another page by another tool does not match its recorded page.
        byte[] moved = Modify(pdf, d =>
        {
            var from = d.Pages[0].Elements.GetArray("/Annots")!;
            var item = from.Elements[1];
            from.Elements.RemoveAt(1);
            var to = new PdfArray(d);
            to.Elements.Add(item);
            d.Pages[1].Elements["/Annots"] = to;
        });
        read = EditablePdfReader.Read(moved, PdfPageGeometryReader.Read(moved));
        Assert.Equal(1, read.RestoredCount);
        Assert.Equal(1, read.ReadOnlyCount);

        // Valid metadata of a kind without an appearance writer (here: a PDF text highlight, D09) is kept read-only, never hidden.
        var line = new PdfTextHighlight(b.Id, false, [new Quad(new(0, 0), new(10, 0), new(0, 10), new(10, 10))], null, HighlightHeightMode.Auto, null, Red, 0.4);
        var lineModel = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [a, line] }) };
        byte[] lineEnvelope = SchemaV2.Serialize(lineModel).Annotations.Single(x => x.AnnotationId == b.Id).Envelope;
        byte[] swapped = Modify(pdf, d =>
        {
            var annots = d.Pages[0].Elements.GetArray("/Annots")!;
            var annot = Resolve(annots.Elements[1])!;
            var envelope = annot.Elements.GetDictionary(EditablePdfKeys.Envelope)!;
            envelope.Elements.Remove("/Filter");
            envelope.Stream.Value = lineEnvelope;
        });
        read = EditablePdfReader.Read(swapped, PdfPageGeometryReader.Read(swapped));
        Assert.Equal(1, read.RestoredCount);
        Assert.Equal(1, read.ReadOnlyCount);
        Assert.DoesNotContain(read.Model.AllAnnotations, x => x.Kind == AnnotationKind.PdfTextHighlight);
    }

    private static byte[] Modify(byte[] pdf, Action<PdfDocument> change)
    {
        using var input = new MemoryStream(pdf);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        change(document);
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    [Fact]
    public void PagesAreWrittenInModelOrderWithGeneratedPagesInserted()
    {
        string path = Path.Combine(folder, "three.pdf");
        ProbeSamplePdf.Create(path, 3);
        byte[] source = File.ReadAllBytes(path);
        var geometry = PdfPageGeometryReader.Read(source);
        var original = EditablePdfReader.Read(source, geometry).Model;
        var generated = new PageSetup(300, 400, 0, PageSource.Generated, PageBackground.Grid);
        var model = original with
        {
            Pages = [original.Pages[2], Page.Create(generated), original.Pages[0]]
        };
        model = With(model, 0, Ink(new(20, 20), new(80, 20)));
        byte[] pdf = EditablePdfWriter.Write(source, geometry, model);
        EditablePdfWriter.Verify(pdf, model);
        using var engine = new PdfiumDocument(pdf);
        using var sourceEngine = new PdfiumDocument(source);
        Assert.Equal(3, engine.PageCount);
        string Text(PdfiumDocument d, int i) => string.Concat(d.ExtractText(i).Select(c => c.Text));
        Assert.Equal(Text(sourceEngine, 2), Text(engine, 0));
        Assert.Equal(Text(sourceEngine, 0), Text(engine, 2));
        Assert.Equal(300, PdfPageGeometryReader.Read(pdf)[1].Box.WidthPt, 6);
        // D10: a source page used twice is a page copy and is saved (PageCopyPdfTests).
        var copied = original with { Pages = [original.Pages[0], original.Pages[0] with { Id = Guid.NewGuid() }] };
        EditablePdfWriter.Verify(EditablePdfWriter.Write(source, geometry, copied), copied);
    }

    [Fact]
    public void EveryKindHasAnAppearanceAndRoundTrips()
    {
        // D09 registered the last kind (PDF text highlights); the writer still refuses any kind without an appearance.
        Assert.All(Enum.GetValues<AnnotationKind>(), kind => Assert.True(AnnotationAppearance.IsSupported(kind), kind.ToString()));
        var model = PdfNotes.Tests.Core.TestModels.AllKinds(out _);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(model.AllAnnotations.Count(), read.RestoredCount);
        Assert.Equal(0, read.ReadOnlyCount);
    }

    [Fact]
    public void OpeningASavedFileRestoresStrokesAndRendersTheBackgroundWithoutThem()
    {
        var ink = Ink(new(100, 100), new(200, 100));
        var model = With(NewNote(), 0, ink);
        string path = Path.Combine(folder, "note.pdf");
        File.WriteAllBytes(path, EditablePdfWriter.Write(null, null, model));
        using var source = PdfSourceDocument.Open(path);
        Assert.True(SameMetadata(model, source.CreateModel()));
        var page = source.Engine.Render(0, 2);
        int i = 200 * page.Stride + 300 * 4;
        Assert.True(page.Bgra[i] > 245 && page.Bgra[i + 2] > 245, "own stroke must not be part of the background");
        Assert.NotSame(source.Bytes, source.AppData.BackgroundPdf);
    }

    // ----- safe replacement -----

    [Fact]
    public void SafeSaveReplacesOnlyAfterVerification()
    {
        string target = Path.Combine(folder, "target.pdf");
        File.WriteAllBytes(target, new byte[] { 1, 2, 3 });
        SafeFileSaver.Save(target, new byte[] { 4, 5, 6 }, bytes => Assert.Equal(new byte[] { 4, 5, 6 }, bytes));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(target));

        var failure = Assert.Throws<SaveFailedException>(() =>
            SafeFileSaver.Save(target, new byte[] { 7 }, _ => throw new InvalidDataException("bad")));
        Assert.Equal(SaveFailure.VerificationFailed, failure.Failure);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(target));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void SafeSaveFailuresKeepTheOriginalBytes()
    {
        string target = Path.Combine(folder, "locked.pdf");
        File.WriteAllBytes(target, new byte[] { 1, 2, 3 });
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var locked = Assert.Throws<SaveFailedException>(() => SafeFileSaver.Save(target, new byte[] { 9, 9 }, _ => { }));
            Assert.Equal(SaveFailure.Locked, locked.Failure);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));

        File.SetAttributes(target, FileAttributes.ReadOnly);
        var readOnly = Assert.Throws<SaveFailedException>(() => SafeFileSaver.Save(target, new byte[] { 9, 9 }, _ => { }));
        Assert.Equal(SaveFailure.AccessDenied, readOnly.Failure);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));

        var missing = Assert.Throws<SaveFailedException>(() => SafeFileSaver.Save(Path.Combine(folder, "nope", "x.pdf"), new byte[] { 1 }, _ => { }));
        Assert.Equal(SaveFailure.FolderMissing, missing.Failure);
        Assert.Single(Directory.GetFiles(folder));
    }
}
