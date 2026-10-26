using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>
/// D13j (spec 11.10): some pages as a PDF of their own — editable (reopens with the same notes) or flattened — carrying
/// only the source pages they show, also when they come from the opened PDF and from imported files.
/// </summary>
public sealed class PageExportPdfTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public PageExportPdfTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private string Sample(string name, int pages = 3, bool linked = false)
    {
        string path = Path.Combine(folder, name);
        if (linked) ProbeSamplePdf.CreateLinked(path, pages); else ProbeSamplePdf.Create(path, pages);
        return path;
    }

    private static string Text(PdfiumDocument d, int i) => string.Concat(d.ExtractText(i).Select(c => c.Text));

    private static int StoredSourcePages(byte[] saved)
    {
        var read = EditablePdfReader.Read(saved, PdfPageGeometryReader.Read(saved));
        return PdfPageGeometryReader.Read(read.SourcePdf ?? throw new InvalidDataException("No stored source.")).Length;
    }

    private static InkStroke Ink(double y) => new(Guid.NewGuid(), false, Affine.Identity, [new(60, y), new(160, y + 20), new(260, y)], 3, new(0xD0, 0x20, 0x20), 1);

    [Fact]
    public void ExportedPagesReopenWithTheirNotesAndOnlyTheirSourcePages()
    {
        using var main = PdfSourceDocument.Open(Sample("main.pdf", 6));
        using var other = PdfSourceDocument.Open(Sample("other.pdf", 4));
        using var sources = new DocumentSources(main, ownsMain: false);
        var session = new DocumentSession(main.CreateModel());
        var import = PdfPageImport.Build(other, [2], sources.NextIndex);
        sources.Append(import.Pdf!);
        session.Execute(PageImports.Insert(session.Current, 6, import.Pages, import.Resources).Command);
        var pages = session.Current.Pages;
        var ink = Ink(300);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, pages[4].Id, ink));
        session.Execute(DocumentEdits.AddAnnotation(session.Current, pages[6].Id, Ink(400)));

        // Page 5, the imported page and page 1 — in that order.
        var (model, pdf) = sources.Subset(session.Current, [pages[4].Id, pages[6].Id, pages[0].Id]);
        using (pdf)
        {
            Assert.Equal(3, model.Pages.Count);
            Assert.NotEqual(session.Current.Id, model.Id);
            Assert.Equal([0, 1, 2], model.Pages.Select(p => p.Setup.Source.PdfPageIndex));
            byte[] saved = EditablePdfWriter.Write(null, null, model, [pdf!]);
            EditablePdfWriter.Verify(saved, model);
            Assert.Equal(3, StoredSourcePages(saved)); // not the 6 + 1 source pages of the document
            string path = Path.Combine(folder, "export.pdf");
            File.WriteAllBytes(path, saved);
            using var reopened = PdfSourceDocument.Open(path);
            var back = reopened.CreateModel();
            Assert.Equal(3, back.Pages.Count);
            Assert.Equal([ink.Id], back.Pages[0].Annotations.Select(a => a.Id)); // editable again, same notes
            Assert.Single(back.Pages[1].Annotations);
            Assert.Empty(back.Pages[2].Annotations);
            using var shown = new PdfiumDocument(saved);
            using var mainShown = new PdfiumDocument(File.ReadAllBytes(main.Path));
            using var otherShown = new PdfiumDocument(File.ReadAllBytes(other.Path));
            Assert.Equal(Text(mainShown, 4), Text(shown, 0));
            Assert.Equal(Text(otherShown, 2), Text(shown, 1));
            Assert.Equal(Text(mainShown, 0), Text(shown, 2));

            var flat = FlattenWriter.Write(null, null, model, [pdf!]);
            FlattenWriter.Verify(flat.Pdf, 3);
            using var flatShown = new PdfiumDocument(flat.Pdf);
            Assert.Equal(Text(mainShown, 4), Text(flatShown, 0));
        }
        Assert.Equal(7, session.Current.Pages.Count); // the open document is not changed
    }

    [Fact]
    public void ARepeatedPageAndANotePageNeedNoExtraSource()
    {
        using var main = PdfSourceDocument.Open(Sample("main.pdf", 3));
        using var sources = new DocumentSources(main, ownsMain: false);
        var session = new DocumentSession(main.CreateModel());
        session.Execute(DocumentEdits.InsertPage(session.Current, 3, NewPageSpec.Default.ToPageSetup()));
        session.Execute(DocumentEdits.InsertPage(session.Current, 4, session.Current.Pages[1].Setup)); // a copy of page 2 (same source page)
        var pages = session.Current.Pages;
        var (model, pdf) = sources.Subset(session.Current, [pages[1].Id, pages[4].Id, pages[3].Id, pages[1].Id]);
        using (pdf)
        {
            Assert.Single(pdf!.Pages);
            Assert.Equal([0, 0, null], model.Pages.Select(p => p.Setup.Source.PdfPageIndex));
            byte[] saved = EditablePdfWriter.Write(null, null, model, [pdf]);
            EditablePdfWriter.Verify(saved, model);
            Assert.Equal(1, StoredSourcePages(saved));
        }
        var (notesOnly, none) = sources.Subset(session.Current, [pages[3].Id]);
        Assert.Null(none);
        EditablePdfWriter.Verify(EditablePdfWriter.Write(null, null, notesOnly), notesOnly);
    }

    [Fact]
    public void LinksToPagesLeftOutAreDropped()
    {
        using var main = PdfSourceDocument.Open(Sample("linked.pdf", 5, linked: true));
        using var sources = new DocumentSources(main, ownsMain: false);
        var model = main.CreateModel();
        int linksBefore = Enumerable.Range(0, 5).Sum(i => sources.Links(i).Length);
        Assert.True(linksBefore > 0);
        var (subset, pdf) = sources.Subset(model, [model.Pages[0].Id]);
        using (pdf)
        {
            byte[] saved = EditablePdfWriter.Write(null, null, subset, [pdf!]);
            EditablePdfWriter.Verify(saved, subset);
            using var doc = PdfReader.Open(new MemoryStream(saved), PdfDocumentOpenMode.Import);
            Assert.Equal(1, doc.PageCount);
            var annots = doc.Pages[0].Elements.GetArray("/Annots");
            int internalLinks = annots?.Elements.Select(e => e is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : e).OfType<PdfDictionary>()
                .Count(a => a.Elements.GetName("/Subtype") == "/Link" && (a.Elements.ContainsKey("/Dest") || a.Elements.GetDictionary("/A")?.Elements.GetName("/S") == "/GoTo")) ?? 0;
            Assert.Equal(0, internalLinks); // every internal link pointed at another page
        }
    }
}
