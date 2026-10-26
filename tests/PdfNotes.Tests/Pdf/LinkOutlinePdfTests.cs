using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>D13h: links and the outline of a PDF (spec 24.3–24.4), permission-restricted PDFs (24.6).</summary>
public sealed class LinkOutlinePdfTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string linked;

    public LinkOutlinePdfTests()
    {
        Directory.CreateDirectory(folder);
        linked = Path.Combine(folder, "linked.pdf");
        ProbeSamplePdf.CreateLinked(linked);
    }

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void LinksAreReadInCanonicalPointsWithTheirTargets()
    {
        using var source = PdfSourceDocument.Open(linked);
        using var sources = new DocumentSources(source, ownsMain: false);
        var links = sources.Links(0);
        Assert.Equal(5, links.Length); // the javascript: URI is read too; the app refuses to open it
        var page3 = links.Single(l => l.TargetPage == 2);
        // Canonical: y down from the top of the 792 pt page.
        Assert.Equal(70, page3.Area.UpperLeft.X, 3);
        Assert.Equal(792 - 664, page3.Area.UpperLeft.Y, 3);
        Assert.Equal(792 - 640, page3.Area.LowerLeft.Y, 3);
        Assert.NotNull(page3.TargetPoint);
        Assert.Equal(792 - ProbeSamplePdf.LinkTargetY, page3.TargetPoint!.Value.Y, 3);
        var page5 = links.Single(l => l.TargetPage == 4);
        Assert.Null(page5.TargetPoint); // /Fit has no point
        Assert.Contains(links, l => l.Uri == ProbeSamplePdf.LinkUrl && l.TargetPage is null);
        Assert.Contains(links, l => l.Uri == "mailto:notes@example.com");
        Assert.Empty(sources.Links(1));
    }

    [Fact]
    public void JavascriptLinksAreReadAsUrisForTheAppToRefuse()
    {
        using var engine = new PdfiumDocument(File.ReadAllBytes(linked));
        Assert.Contains(engine.Links(0), l => l.Uri == "javascript:alert(1)");
    }

    [Fact]
    public void OutlineIsATreeOfPageTargets()
    {
        using var source = PdfSourceDocument.Open(linked);
        using var sources = new DocumentSources(source, ownsMain: false);
        var outline = sources.Outline();
        Assert.Equal(["1장 시작", "2장"], outline.Select(o => o.Title));
        Assert.Equal(0, outline[0].TargetPage);
        Assert.Equal("1.1 둘째 쪽", outline[0].Children.Single().Title);
        Assert.Equal(1, outline[0].Children.Single().TargetPage);
        Assert.Equal(3, outline[1].TargetPage);
        Assert.Equal(4, outline[1].Children.Single().TargetPage);
    }

    [Fact]
    public void SaveKeepsTheOutlineWithoutItemsOfDeletedPagesAndFlattenKeepsItToo()
    {
        using var source = PdfSourceDocument.Open(linked);
        var session = new DocumentSession(source.CreateModel());
        var p = session.Current.Pages.Select(x => x.Id).ToArray();
        session.Execute(DocumentEdits.DeletePages(session.Current, [p[1]]));   // 1.1 points only here
        session.Execute(PageEdits.MovePages(session.Current, [p[3]], 0));       // 2장 moves to the front
        var saved = EditablePdfWriter.Write(source.Bytes, source.Pages, session.Current);
        EditablePdfWriter.Verify(saved, session.Current);
        string file = Path.Combine(folder, "saved.pdf");
        File.WriteAllBytes(file, saved);

        using (var engine = new PdfiumDocument(saved))
        {
            var outline = engine.Bookmarks();
            Assert.Equal(["1장 시작", "2장"], outline.Select(o => o.Title));
            Assert.Empty(outline[0].Children);                       // its only page was deleted
            Assert.Equal(1, outline[0].TargetPage);                   // page 1 is now second
            Assert.Equal(0, outline[1].TargetPage);                   // page 4 moved to the front
            Assert.Equal(3, outline[1].Children.Single().TargetPage); // page 5 is last
            // Links still lead to the right pages; the one to the deleted page is gone or harmless.
            var links = engine.Links(1);
            Assert.Contains(links, l => l.Uri == ProbeSamplePdf.LinkUrl);
            Assert.Contains(links, l => l.TargetPage == 3);
        }
        using (var reopened = PdfSourceDocument.Open(file))
        using (var sources = new DocumentSources(reopened, ownsMain: false))
            Assert.Equal(2, sources.Outline().Length);

        var flat = FlattenWriter.Write(source.Bytes, source.Pages, session.Current);
        using (var engine = new PdfiumDocument(flat.Pdf))
        {
            Assert.Equal(["1장 시작", "2장"], engine.Bookmarks().Select(o => o.Title));
            Assert.Contains(engine.Links(1), l => l.Uri == ProbeSamplePdf.LinkUrl);
        }
    }

    [Fact]
    public void PermissionRestrictedPdfOpensFromAnUnsecuredCopyAndSavesWithoutTheRestriction()
    {
        string restricted = Path.Combine(folder, "owner.pdf");
        ProbeSamplePdf.CreateOwnerEncrypted(linked, restricted);
        // Refused where it is not allowed (page import, spec 11.9) …
        Assert.Equal(PdfOpenFailure.Encrypted, Assert.Throws<PdfOpenException>(() => PdfSourceDocument.Open(restricted).Dispose()).Failure);
        // … opened for viewing, search and notes otherwise (24.6).
        using var source = PdfSourceDocument.Open(restricted, allowPermissionRestricted: true);
        Assert.True(source.PermissionRestricted);
        Assert.False(source.Engine.IsEncrypted);
        using (var sources = new DocumentSources(source, ownsMain: false))
        {
            Assert.Contains(sources.Glyphs(0), g => g.Text == "P");
            Assert.Equal(2, sources.Outline().Length);
        }
        var model = source.CreateModel();
        var page = model.Pages[0];
        model = model with { Pages = model.Pages.SetItem(0, page with { Annotations = page.Annotations.Add(
            new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 100), new(200, 150)], 2, new(0, 0, 0), 1)) }) };
        var saved = EditablePdfWriter.Write(source.Bytes, source.Pages, model);
        EditablePdfWriter.Verify(saved, model);
        using (var engine = new PdfiumDocument(saved)) Assert.False(engine.IsEncrypted);
        using (var reopened = PdfReader.Open(new MemoryStream(saved), PdfDocumentOpenMode.Modify)) Assert.Equal(5, reopened.PageCount); // no owner password needed

        // A user password is still refused even when restrictions are allowed.
        string password = Path.Combine(folder, "password.pdf");
        ProbeSamplePdf.CreatePasswordProtected(linked, password);
        Assert.Equal(PdfOpenFailure.PasswordProtected,
            Assert.Throws<PdfOpenException>(() => PdfSourceDocument.Open(password, allowPermissionRestricted: true).Dispose()).Failure);
    }
}
