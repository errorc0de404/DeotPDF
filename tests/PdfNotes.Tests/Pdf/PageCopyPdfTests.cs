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

/// <summary>D10a / A15–A16: copied source pages, page-only copies, rotation, reorder and delete through save/reopen.</summary>
public sealed class PageCopyPdfTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public PageCopyPdfTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private static string Text(PdfiumDocument d, int i) => string.Concat(d.ExtractText(i).Select(c => c.Text));

    private static string[] AnnotationNames(byte[] pdf, int page)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var annots = doc.Pages[page].Elements.GetArray("/Annots");
        if (annots is null) return [];
        return [.. annots.Elements.Select(e => e is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : e).OfType<PdfDictionary>().Select(a => a.Elements.GetString("/NM"))];
    }

    [Fact]
    public void CopiedPdfPagesKeepTheirContentAndOnlyTheChosenAnnotations()
    {
        string path = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(path, 3); // every page carries a foreign annotation "Foreign-D01"
        byte[] source = File.ReadAllBytes(path);
        var first = EditablePdfReader.Read(source, PdfPageGeometryReader.Read(source)).Model;
        var ink = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 300), new(300, 300)], 6, new(220, 30, 30), 1);
        var session = new DocumentSession(first);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, first.Pages[0].Id, ink));
        byte[] saved = EditablePdfWriter.Write(source, PdfPageGeometryReader.Read(source), session.Current);

        // Reopen the saved file: its page 1 now carries our ink as an app annotation to be stripped from copies.
        var geometry = PdfPageGeometryReader.Read(saved);
        var model = EditablePdfReader.Read(saved, geometry).Model;
        session = new DocumentSession(model);
        var p = model.Pages.Select(x => x.Id).ToArray();
        var withNotes = PageClipboard.Capture(session.Current, [p[0]], PageCopyMode.WithAnnotations);
        var pageOnly = PageClipboard.Capture(session.Current, [p[0]], PageCopyMode.PageOnly);
        var (paste1, copyA) = withNotes.Paste(session.Current, 1);
        session.Execute(paste1);
        var (paste2, copyB) = pageOnly.Paste(session.Current, 2);
        session.Execute(paste2);
        session.Execute(PageEdits.RotatePages(session.Current, [copyA[0]], clockwise: true));
        session.Execute(PageEdits.MovePages(session.Current, [p[2]], 0));
        session.Execute(DocumentEdits.DeletePages(session.Current, [p[1]]));
        var edited = session.Current;
        Assert.Equal([p[2], p[0], copyA[0], copyB[0]], edited.Pages.Select(x => x.Id));

        byte[] pdf = EditablePdfWriter.Write(saved, geometry, edited);
        EditablePdfWriter.Verify(pdf, edited);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(edited.Pages.Select(x => x.Id), read.Model.Pages.Select(x => x.Id));
        Assert.Equal(0, read.ReadOnlyCount);
        Assert.Equal(2, read.RestoredCount); // the ink and its copy, nothing duplicated
        Assert.Single(read.Model.Pages[1].Annotations);
        Assert.Single(read.Model.Pages[2].Annotations);
        Assert.Empty(read.Model.Pages[3].Annotations);
        Assert.NotEqual(read.Model.Pages[1].Annotations[0].Id, read.Model.Pages[2].Annotations[0].Id);
        Assert.Equal(90, read.Model.Pages[2].Setup.Rotation);

        using var original = new PdfiumDocument(source);
        using var engine = new PdfiumDocument(pdf);
        Assert.Equal(4, engine.PageCount);
        Assert.Equal(Text(original, 2), Text(engine, 0));
        Assert.Equal(Text(original, 0), Text(engine, 1));
        // Copies keep the original, extractable text; on the turned copy the engine may list its lines in another order.
        static string[] Lines(string text) => [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];
        Assert.Equal(Lines(Text(original, 0)), Lines(Text(engine, 2)));
        Assert.Equal(Text(original, 0), Text(engine, 3));
        for (int i = 1; i <= 3; i++) Assert.Contains("Foreign-D01", AnnotationNames(pdf, i)); // foreign annotations are content, kept on copies
        Assert.Single(AnnotationNames(pdf, 3)); // page-only copy: no app annotation, not even the old one

        // The page-only copy shows no ink where the original page has it (the background never contained it).
        var page = engine.Render(3, 2);
        var setup = read.Model.Pages[3].Setup;
        var at = setup.Frame.CanonicalToDisplay.Transform(new(200, 300));
        int k = (int)(at.Y * 2) * page.Stride + (int)(at.X * 2) * 4;
        Assert.True(page.Bgra[k] > 240 && page.Bgra[k + 2] > 240, "page-only copy has no app ink");

        // Saving the reopened file again: every page is its own PDF page now.
        var again = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), read.Model);
        EditablePdfWriter.Verify(again, read.Model);
        Assert.True(again.Length < pdf.Length * 1.2, $"no growth on resave: {pdf.Length} → {again.Length}");
    }
}
