using System.IO;
using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Rendering;
using PdfNotes.Pdf.Writing;
using PdfNotes.Tests.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

public sealed class PageTransformPdfTests : IDisposable
{
    [Fact]
    public void FreePlacementSurvivesSaveReopenAndFurtherResizeWithoutRasterizingText()
    {
        string path = Path.Combine(folder, "free-source.pdf"); ProbeSamplePdf.Create(path, 3);
        using var source = PdfSourceDocument.Open(path); var model = source.CreateModel();
        var session = new DocumentSession(model);
        session.Execute(PageTransforms.Shrink(model, model.Pages.Select(p => p.Id), 67, freePosition: new(.13, .08)));
        var bytes = Save(source, session.Current);
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes)); Same(session.Current, read.Model);
        Assert.Contains("Original PDF", Text(bytes, 0)); Assert.Equal(0, read.ReadOnlyCount);
        session = new DocumentSession(read.Model);
        session.Execute(PageTransforms.Resize(read.Model, read.Model.Pages.Select(p => p.Id), 250, 320, PageResizeMode.ClipContent, freePosition: new(.06, -.02)));
        bytes = EditablePdfWriter.Write(bytes, PdfPageGeometryReader.Read(bytes), session.Current);
        EditablePdfWriter.Verify(bytes, session.Current);
        Same(session.Current, EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes)).Model);
        Assert.Contains("Original PDF", Text(bytes, 0));
    }
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    public PageTransformPdfTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static string Text(byte[] bytes, int index) { using var d = new PdfiumDocument(bytes); return string.Concat(d.ExtractText(index).Select(c => c.Text)); }
    private static byte[] Save(PdfSourceDocument source, DocumentModel model) { var bytes = EditablePdfWriter.Write(source.Bytes, source.Pages, model); EditablePdfWriter.Verify(bytes, model); return bytes; }
    private static void Same(DocumentModel a, DocumentModel b)
    {
        var x = SchemaV4.Serialize(a); var y = SchemaV4.Serialize(b); Assert.Equal(x.Header, y.Header);
        Assert.Equal(x.Annotations.Select(a => Convert.ToBase64String(a.Envelope)), y.Annotations.Select(a => Convert.ToBase64String(a.Envelope)));
    }
    [Fact]
    public void AllKindsCopyRotateMoveDeleteUndoAndTenSavesPreserveEditableContent()
    {
        string path = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(path, 3);
        using var source = PdfSourceDocument.Open(path); var model = source.CreateModel();
        var all = TestModels.AllKinds(out var resource); var kinds = all.AllAnnotations.ToImmutableList();
        var text = kinds.OfType<TextBoxAnnotation>().Single(); text = TextEditingTests.Box("한글 English 😀\n같은 줄", 180) with { Id = text.Id, Locked = true,
            Highlights = [new(0, 2, HighlightHeightMode.Manual, 12, new(255, 230, 0), .4)] };
        kinds = kinds.Select(a => a is TextBoxAnnotation ? text : a).ToImmutableList();
        var fragment = TestModels.Ink() with { Points = [], Outline = [[new(350, 350), new(400, 350), new(400, 400), new(350, 400)]] };
        model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = kinds.Add(fragment) }), Resources = model.Resources.Add(resource.Id, resource) };
        var s = new DocumentSession(model); var first = model.Pages[0].Id;
        s.Execute(PageTransforms.Shrink(model, [first], 80));
        var (copy, ids) = PageClipboard.Capture(s.Current, [first], PageCopyMode.WithAnnotations).Paste(s.Current, 1); s.Execute(copy);
        s.Execute(PageEdits.RotatePages(s.Current, ids, true)); s.Execute(PageEdits.MovePages(s.Current, ids, s.Current.Pages.Count));
        var beforeDelete = s.Current; s.Execute(DocumentEdits.DeletePages(s.Current, [first])); s.Undo(); Same(beforeDelete, s.Current);
        byte[] pdf = Save(source, s.Current); Assert.Contains("Original PDF", Text(pdf, 0)); Assert.Contains("한글", Text(pdf, 0));
        var expected = s.Current; var sizes = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf)); Same(expected, read.Model); Assert.Equal(0, read.ReadOnlyCount);
            sizes.Add(pdf.Length); pdf = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), read.Model); EditablePdfWriter.Verify(pdf, expected);
        }
        Assert.True(sizes.Max() - sizes.Min() < 128, string.Join(",", sizes));
        File.WriteAllBytes(Path.Combine(folder, "saved.pdf"), pdf);
        using var reopened = PdfSourceDocument.Open(Path.Combine(folder, "saved.pdf")); Same(expected, reopened.CreateModel());
        var edit = new DocumentSession(reopened.CreateModel()); var fresh = TestModels.Ink(150);
        edit.Execute(DocumentEdits.AddAnnotation(edit.Current, first, fresh)); Assert.Same(fresh, edit.Current.FindAnnotation(fresh.Id)!.Annotation); Save(reopened, edit.Current);
    }
    [Theory, InlineData(0), InlineData(90), InlineData(180), InlineData(270)]
    public void OffsetCropUserUnitAndForeignAppearanceMatchScreenAndSavedPdf(int rotation)
    {
        string path = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(path, 3);
        using var source = PdfSourceDocument.Open(path); var model = source.CreateModel();
        model = model with { Pages = [model.Pages[1] with { Setup = model.Pages[1].Setup with { Rotation = rotation } }] };
        var s = new DocumentSession(model); s.Execute(PageTransforms.Shrink(model, [model.Pages[0].Id], 80, 8));
        byte[] saved = Save(source, s.Current); var setup = s.Current.Pages[0].Setup;
        using var renderer = new PageRenderer(source); var screen = renderer.Render(setup, 1, null, CancellationToken.None);
        using var engine = new PdfiumDocument(saved); var actual = engine.Render(0, 1, annotations: true);
        Assert.Equal(screen.Width, actual.Width); Assert.Equal(screen.Height, actual.Height); Assert.Equal(screen.Bgra, actual.Bgra);
        Assert.Contains("Original PDF", Text(saved, 0));
        var originalGlyph = PdfTextReader.Glyphs(source.Engine, source.Pages[1]).First(g => g.Text == "O");
        var savedGlyph = PdfTextReader.Glyphs(engine, PdfPageGeometryReader.Read(saved)[0]).First(g => g.Text == "O");
        var expected = setup.Content!.Transform.Transform(originalGlyph.Center);
        Assert.InRange(Math.Sqrt(Math.Pow(savedGlyph.Center.X - expected.X, 2) + Math.Pow(savedGlyph.Center.Y - expected.Y, 2)), 0, .5);
        using var document = PdfReader.Open(new MemoryStream(saved), PdfDocumentOpenMode.Import);
        Assert.Single(document.Pages[0].Elements.GetArray("/Annots")!.Elements); // foreign annotation retained
    }
    [Fact]
    public void CorruptedOriginalArchiveKeepsAllPhysicalAppearancesReadOnly()
    {
        string path = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(path, 3);
        using var source = PdfSourceDocument.Open(path); var session = new DocumentSession(source.CreateModel());
        var page = session.Current.Pages[0]; session.Execute(DocumentEdits.AddAnnotation(session.Current, page.Id, TestModels.Ink()));
        session.Execute(PageTransforms.Shrink(session.Current, [page.Id], 80)); byte[] bytes = Save(source, session.Current);
        using var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        doc.Internals.Catalog.Elements.SetString("/PdfNotesOriginalHash", "corrupted");
        using var output = new MemoryStream(); doc.Save(output, false); bytes = output.ToArray();
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        Assert.Equal(HeaderStatus.Invalid, read.HeaderStatus); Assert.Equal(0, read.RestoredCount); Assert.Equal(1, read.ReadOnlyCount);
        Assert.Null(read.SourcePdf); Assert.Same(bytes, read.BackgroundPdf); Assert.Empty(read.Model.AllAnnotations);
    }
    [Fact]
    public void CopiesOfOneSourceWithDifferentPlacementsHaveIndependentForeignAppearances()
    {
        string path = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(path, 3);
        using var source = PdfSourceDocument.Open(path); var model = source.CreateModel();
        model = model with { Pages = [model.Pages[1], model.Pages[1] with { Id = Guid.NewGuid() }] };
        var s = new DocumentSession(model); s.Execute(PageTransforms.Shrink(model, [model.Pages[0].Id], 80, 0));
        s.Execute(PageTransforms.Shrink(s.Current, [model.Pages[1].Id], 60, 8)); byte[] bytes = Save(source, s.Current);
        using var renderer = new PageRenderer(source); using var engine = new PdfiumDocument(bytes);
        for (int i = 0; i < 2; i++) Assert.Equal(renderer.Render(s.Current.Pages[i].Setup, 1, null, CancellationToken.None).Bgra, engine.Render(i, 1).Bgra);
    }
    [Fact]
    public void MissingAppearanceAtOriginalCropBoundaryIsRejectedBeforeUiMutation()
    {
        string path = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(path, 3);
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var a = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)doc.Pages[1].Elements.GetArray("/Annots")!.Elements[0]).Value;
            a.Elements.Remove("/AP"); a.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(20), new PdfInteger(500), new PdfInteger(60), new PdfInteger(550));
            doc.Save(Path.Combine(folder, "unsupported.pdf"));
        }
        using var source = PdfSourceDocument.Open(Path.Combine(folder, "unsupported.pdf"));
        var session = new DocumentSession(source.CreateModel());
        Assert.Throws<NotSupportedException>(() => PagePlacementValidation.Validate(source, session.Current.Pages.Select(p => p.Setup)));
        Assert.Equal(0, session.UndoCount); Assert.False(session.IsDirty);
    }
    [Fact]
    public void ExternalBackgroundChangesArePreservedInsteadOfReplacedByArchive()
    {
        string path = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(path, 3);
        using var source = PdfSourceDocument.Open(path); var session = new DocumentSession(source.CreateModel());
        var id = session.Current.Pages[0].Id; session.Execute(PageTransforms.Shrink(session.Current, [id], 80));
        byte[] bytes = Save(source, session.Current);
        using var changed = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        using (var graphics = XGraphics.FromPdfPage(changed.Pages[0], XGraphicsPdfPageOptions.Append)) graphics.DrawRectangle(XBrushes.Red, 20, 20, 30, 30);
        using var output = new MemoryStream(); changed.Save(output, false); bytes = output.ToArray();
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus); Assert.Null(read.SourcePdf); Assert.Null(read.Model.Pages[0].Setup.Content);
        Assert.Contains(read.Warnings, w => w.Contains("externally"));
        using var before = new PdfiumDocument(bytes);
        byte[] saved = EditablePdfWriter.Write(bytes, PdfPageGeometryReader.Read(bytes), read.Model); EditablePdfWriter.Verify(saved, read.Model);
        using var after = new PdfiumDocument(saved); Assert.Equal(before.Render(0, 1).Bgra, after.Render(0, 1).Bgra);
    }
    [Fact]
    public void ClipSaveReopenGrowRestoresOutsideContentButNotOriginalCropHiddenInk()
    {
        string path = Path.Combine(folder, "source.pdf");
        using (var doc = new PdfDocument())
        {
            var p = doc.AddPage(); p.Width = XUnit.FromPoint(600); p.Height = XUnit.FromPoint(800);
            using (var g = XGraphics.FromPdfPage(p)) { g.DrawRectangle(XBrushes.Red, 150, 150, 40, 40); g.DrawRectangle(XBrushes.Blue, 450, 650, 40, 40); g.DrawRectangle(XBrushes.Green, 20, 20, 20, 20); }
            p.CropBox = new PdfRectangle(new XPoint(100, 100), new XPoint(550, 750)); doc.Save(path);
        }
        using var source = PdfSourceDocument.Open(path); var model = source.CreateModel(); var s = new DocumentSession(model); var id = model.Pages[0].Id;
        var ink = TestModels.Ink() with { Points = [new(350, 550), new(400, 550)], WidthPt = 10, Locked = true };
        s.Execute(DocumentEdits.AddAnnotation(s.Current, id, ink));
        s.Execute(PageTransforms.Resize(s.Current, [id], 60, 80, PageResizeMode.ClipContent, 0));
        string savedPath = Path.Combine(folder, "clipped.pdf"); File.WriteAllBytes(savedPath, Save(source, s.Current));
        using var reopened = PdfSourceDocument.Open(savedPath); var r = new DocumentSession(reopened.CreateModel());
        Assert.True(((InkStroke)r.Current.FindAnnotation(ink.Id)!.Annotation).Points[0].Y > r.Current.Pages[0].Setup.HeightPt);
        r.Execute(PageTransforms.Resize(r.Current, [id], Units.PointsToMillimeters(450), Units.PointsToMillimeters(650), PageResizeMode.ClipContent, 0));
        byte[] grown = Save(reopened, r.Current); using var actual = new PdfiumDocument(grown); using var expected = new PageRenderer(source);
        var a = actual.Render(0, 1, annotations: false); var b = expected.Render(model.Pages[0].Setup, 1, null, CancellationToken.None); Assert.Equal(b.Bgra, a.Bgra);
        // Enlarge beyond the original visible crop: the green rectangle at (20,20) must remain clipped.
        r.Execute(PageTransforms.Resize(r.Current, [id], 250, 300, PageResizeMode.ClipContent, 4));
        using var final = new PdfiumDocument(Save(reopened, r.Current)); var pixels = final.Render(0, 1, annotations: false);
        for (int i = 0; i < pixels.Bgra.Length; i += 4) Assert.False(pixels.Bgra[i + 1] > pixels.Bgra[i + 2] + 30 && pixels.Bgra[i + 1] > pixels.Bgra[i] + 30);
    }
}
