using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Rendering;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>
/// D13g / A33 (spec 11.9): pages of another PDF appended to the document's one source — shown, searchable, saved, reopened
/// and flattened like the document's own pages; undone imports are left out of the saved source.
/// </summary>
public sealed class PageImportPdfTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public PageImportPdfTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private static string Text(PdfiumDocument d, int i) => string.Concat(d.ExtractText(i).Select(c => c.Text));

    private string Sample(string name, int pages = 3)
    {
        string path = Path.Combine(folder, name);
        ProbeSamplePdf.Create(path, pages);
        return path;
    }

    private string Mixed(string name)
    {
        string path = Path.Combine(folder, name);
        ProbeSamplePdf.CreateMixed(path);
        return path;
    }

    private static string[] ForeignNames(byte[] pdf, int page)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var annots = doc.Pages[page].Elements.GetArray("/Annots");
        if (annots is null) return [];
        return [.. annots.Elements.Select(e => e is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : e).OfType<PdfDictionary>().Select(a => a.Elements.GetString("/NM")).Where(n => n.StartsWith("Foreign"))];
    }

    private static int StoredSourcePages(byte[] saved)
    {
        var read = EditablePdfReader.Read(saved, PdfPageGeometryReader.Read(saved));
        return PdfPageGeometryReader.Read(read.SourcePdf ?? throw new InvalidDataException("No stored source.")).Length;
    }

    private static (DocumentModel Model, byte[] Pdf) Save(DocumentSources sources, DocumentModel current)
    {
        var (main, model, imported) = sources.ForSave(current);
        byte[] pdf = EditablePdfWriter.Write(main, sources.Main?.Pages, model, imported);
        EditablePdfWriter.Verify(pdf, model);
        return (model, pdf);
    }

    private static Guid[] Import(DocumentSources sources, DocumentSession session, PdfSourceDocument file, int[] pages, int at)
    {
        var import = PdfPageImport.Build(file, pages, sources.NextIndex);
        if (import.Pdf is { } pdf) sources.Append(pdf);
        var (command, ids) = PageImports.Insert(session.Current, at, import.Pages, import.Resources);
        Assert.True(session.Execute(command));
        return ids;
    }

    [Fact]
    public void ImportedPagesKeepTheirContentAndSurviveSaveAndReopen()
    {
        using var main = PdfSourceDocument.Open(Sample("a.pdf"));
        using var other = PdfSourceDocument.Open(Mixed("b.pdf"));
        using var sources = new DocumentSources(main, ownsMain: false);
        var session = new DocumentSession(main.CreateModel());
        var own = session.Current.Pages.Select(p => p.Id).ToArray();

        // Rotated, cropped and UserUnit pages, in file order, after page 1 of the document.
        var ids = Import(sources, session, other, [1, 3, 4], 1);
        var model = session.Current;
        Assert.Equal([own[0], ids[0], ids[1], ids[2], own[1], own[2]], model.Pages.Select(p => p.Id));
        Assert.Equal([3, 4, 5], ids.Select(id => model.GetPage(id).Setup.Source.PdfPageIndex!.Value));
        Assert.Equal(other.CreateModel().Pages[1].Setup with { Source = PageSource.Pdf(3) }, model.GetPage(ids[0]).Setup);

        // Shown and searchable before any save.
        using (var renderer = new PageRenderer(sources))
        {
            var page = renderer.Render(model.GetPage(ids[1]).Setup, 0.5, null, CancellationToken.None);
            Assert.Contains(page.Bgra.Where((_, i) => i % 4 == 0), b => b < 128);
        }
        Assert.Contains("Page 4", string.Concat(sources.Glyphs(4).Select(g => g.Text)));
        Assert.Contains("Page 2", string.Concat(sources.Glyphs(3).Select(g => g.Text)));

        var (saved, pdf) = Save(sources, model);
        Assert.Same(model, saved);
        Assert.Equal(6, StoredSourcePages(pdf));
        using var engine = new PdfiumDocument(pdf);
        using var original = new PdfiumDocument(main.Bytes);
        using var imported = new PdfiumDocument(File.ReadAllBytes(Path.Combine(folder, "b.pdf")));
        Assert.Equal([Text(original, 0), Text(imported, 1), Text(imported, 3), Text(imported, 4), Text(original, 1), Text(original, 2)],
            Enumerable.Range(0, 6).Select(i => Text(engine, i)));
        Assert.Contains("Foreign-D01", ForeignNames(pdf, 0));
        Assert.Contains("Foreign-D01", ForeignNames(pdf, 4));

        // Reopened, it is an ordinary single-source document: same pages, same sources.
        string path = Path.Combine(folder, "saved.pdf");
        File.WriteAllBytes(path, pdf);
        using var reopened = PdfSourceDocument.Open(path);
        Assert.Equal(model.Pages.Select(p => p.Id), reopened.CreateModel().Pages.Select(p => p.Id));
        Assert.Equal(model.Pages.Select(p => p.Setup), reopened.CreateModel().Pages.Select(p => p.Setup));
        Assert.Equal(6, reopened.Pages.Count);
        byte[] again = EditablePdfWriter.Write(reopened.Bytes, reopened.Pages, reopened.CreateModel());
        EditablePdfWriter.Verify(again, reopened.CreateModel());
        Assert.Equal(6, StoredSourcePages(again));
    }

    [Fact]
    public void UndoneAndDeletedImportsAreLeftOutOfTheSavedSource()
    {
        using var main = PdfSourceDocument.Open(Sample("a.pdf"));
        using var other = PdfSourceDocument.Open(Mixed("b.pdf"));
        using var sources = new DocumentSources(main, ownsMain: false);
        var session = new DocumentSession(main.CreateModel());
        var first = Import(sources, session, other, [0, 1], 3);      // source 3–4
        var second = Import(sources, session, other, [5], 0);        // source 5
        var third = Import(sources, session, other, [2], 1);         // source 6
        Assert.Equal(7, sources.Pages.Count);

        // Undo the last import and delete the first one's pages: only the second import is still used.
        Assert.True(session.Undo());
        session.Execute(DocumentEdits.DeletePages(session.Current, first));
        var (model, pdf) = Save(sources, session.Current);
        Assert.Equal(4, StoredSourcePages(pdf));
        Assert.Equal(3, model.GetPage(second[0]).Setup.Source.PdfPageIndex);
        Assert.Equal(5, session.Current.GetPage(second[0]).Setup.Source.PdfPageIndex); // the open document keeps its numbering
        using var engine = new PdfiumDocument(pdf);
        using var imported = new PdfiumDocument(File.ReadAllBytes(Path.Combine(folder, "b.pdf")));
        Assert.Equal(Text(imported, 5), Text(engine, 0));

        Assert.Equal(-1, session.Current.IndexOfPage(third[0]));
    }

    [Fact]
    public void ANewNoteGetsASourceFromItsFirstImport()
    {
        using var other = PdfSourceDocument.Open(Sample("a.pdf"));
        using var sources = new DocumentSources(null);
        var session = new DocumentSession(NewPageSpec.Default.CreateDocument());
        var ids = Import(sources, session, other, [0, 2], 1);
        Assert.Equal([0, 1], ids.Select(id => session.Current.GetPage(id).Setup.Source.PdfPageIndex!.Value));
        var (model, pdf) = Save(sources, session.Current);
        Assert.Equal(2, StoredSourcePages(pdf));
        using var engine = new PdfiumDocument(pdf);
        Assert.Equal(3, engine.PageCount);
        Assert.Contains("Page 1", Text(engine, 1));
        Assert.Contains("Page 3", Text(engine, 2));
        Assert.Contains("Foreign-D01", ForeignNames(pdf, 1));
        var flat = FlattenWriter.Write(null, null, model, sources.ForSave(session.Current).Imported);
        FlattenWriter.Verify(flat.Pdf, 3);
    }

    [Fact]
    public void PagesOfAFileThisAppSavedBringTheirAnnotationsPlacementAndNotePages()
    {
        // The other file: ink on page 1, page 3 shrunk (content placement), a grid page added at the end.
        using var source = PdfSourceDocument.Open(Sample("b.pdf"));
        var edit = new DocumentSession(source.CreateModel());
        var p = edit.Current.Pages.Select(x => x.Id).ToArray();
        var ink = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 300), new(300, 300)], 6, new(220, 30, 30), 1);
        edit.Execute(DocumentEdits.AddAnnotation(edit.Current, p[0], ink));
        edit.Execute(PageTransforms.Shrink(edit.Current, [p[2]], 70));
        edit.Execute(DocumentEdits.InsertPage(edit.Current, 3, NewPageSpec.FromPreset(PaperSize.A5, PageOrientation.Landscape, PageBackground.Grid).ToPageSetup()));
        var gridLine = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(20, 20), new(80, 90)], 2, new(10, 10, 200), 1);
        edit.Execute(DocumentEdits.AddAnnotation(edit.Current, edit.Current.Pages[3].Id, gridLine));
        string appFile = Path.Combine(folder, "app.pdf");
        File.WriteAllBytes(appFile, EditablePdfWriter.Write(source.Bytes, source.Pages, edit.Current));

        using var main = PdfSourceDocument.Open(Sample("a.pdf", 2));
        using var file = PdfSourceDocument.Open(appFile);
        using var sources = new DocumentSources(main, ownsMain: false);
        var session = new DocumentSession(main.CreateModel());
        var ids = Import(sources, session, file, [0, 2, 3], 2);
        var model = session.Current;
        var inkCopy = Assert.IsType<InkStroke>(Assert.Single(model.GetPage(ids[0]).Annotations));
        Assert.NotEqual(ink.Id, inkCopy.Id);
        Assert.Equal(ink.Points.Length, inkCopy.Points.Length);
        Assert.All(ink.Points.Zip(inkCopy.Points), p => Assert.True(Math.Abs(p.First.X - p.Second.X) < 1e-6 && Math.Abs(p.First.Y - p.Second.Y) < 1e-6));
        Assert.Equal(edit.Current.Pages[2].Setup.Content, model.GetPage(ids[1]).Setup.Content);
        Assert.Equal(PageSourceKind.Generated, model.GetPage(ids[2]).Setup.Source.Kind);
        Assert.Equal(PageBackgroundKind.Grid, model.GetPage(ids[2]).Setup.Background.Kind);
        Assert.Single(model.GetPage(ids[2]).Annotations);
        Assert.Equal(2, sources.Imported.Single().Pages.Count); // only source pages 1 and 3 are kept

        var (saved, pdf) = Save(sources, model);
        string path = Path.Combine(folder, "saved.pdf");
        File.WriteAllBytes(path, pdf);
        using var reopened = PdfSourceDocument.Open(path);
        Assert.Equal(0, reopened.AppData.ReadOnlyCount);
        Assert.Equal(saved.Pages.Select(x => x.Setup), reopened.CreateModel().Pages.Select(x => x.Setup));
        Assert.Equal(2, reopened.CreateModel().AllAnnotations.Count());
        using var engine = new PdfiumDocument(pdf);
        Assert.Contains("Page 1", Text(engine, 2));
        Assert.Contains("Page 3", Text(engine, 3)); // shrunk page keeps its text

        var flat = FlattenWriter.Write(main.Bytes, main.Pages, saved, sources.ForSave(model).Imported);
        FlattenWriter.Verify(flat.Pdf, 5);
    }

    [Fact]
    public void TheSameFileTwiceAndTheDocumentItselfCanBeImported()
    {
        string path = Sample("a.pdf");
        using var main = PdfSourceDocument.Open(path);
        using var sources = new DocumentSources(main, ownsMain: false);
        var session = new DocumentSession(main.CreateModel());
        using (var self = PdfSourceDocument.Open(path))
        {
            var a = Import(sources, session, self, [0], 3);
            var b = Import(sources, session, self, [0], 4);
            Assert.NotEqual(a[0], b[0]);
        }
        Assert.Equal(5, session.Current.Pages.Count);
        Assert.Equal(session.Current.Pages.Count, session.Current.Pages.Select(x => x.Id).Distinct().Count());
        var (_, pdf) = Save(sources, session.Current);
        using var engine = new PdfiumDocument(pdf);
        Assert.Equal(Text(engine, 0), Text(engine, 3));
        Assert.Equal(Text(engine, 0), Text(engine, 4));
    }

    [Fact]
    public void ImportedPagesCanBeShrunkLikeTheDocumentsOwn()
    {
        using var main = PdfSourceDocument.Open(Sample("a.pdf", 1));
        using var other = PdfSourceDocument.Open(Mixed("b.pdf"));
        using var sources = new DocumentSources(main, ownsMain: false);
        var session = new DocumentSession(main.CreateModel());
        var ids = Import(sources, session, other, [1, 4], 1);
        PagePlacementValidation.Validate(sources, ids.Select(id => session.Current.GetPage(id).Setup));
        session.Execute(PageTransforms.Shrink(session.Current, ids, 60));
        using (var renderer = new PageRenderer(sources))
            renderer.Render(session.Current.GetPage(ids[0]).Setup, 0.5, null, CancellationToken.None);
        var (model, pdf) = Save(sources, session.Current);
        string path = Path.Combine(folder, "saved.pdf");
        File.WriteAllBytes(path, pdf);
        using var reopened = PdfSourceDocument.Open(path);
        Assert.Equal(model.Pages.Select(x => x.Setup), reopened.CreateModel().Pages.Select(x => x.Setup));
    }

    [Fact]
    public void ImportRejectsBadPageChoices()
    {
        using var other = PdfSourceDocument.Open(Sample("a.pdf"));
        Assert.Throws<ArgumentException>(() => PdfPageImport.Build(other, [], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageImport.Build(other, [3], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageImport.Build(other, [0], -1));
    }
}
