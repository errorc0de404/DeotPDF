using System.IO;
using PdfNotes.App.Services;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using Alignment = PdfNotes.Core.Models.TextAlignment;

namespace PdfNotes.Tests.App;

/// <summary>D11: document search over PDF text and text boxes (spec 12), including D10 page transforms.</summary>
public sealed class SearchTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public SearchTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private static string[] Units(string text) => text.Select(c => c.ToString()).ToArray();

    [Fact]
    public void MatchingIgnoresCaseAndTreatsWhiteSpaceAndLineEndsAsOneSpace()
    {
        Assert.Equal([(0, 3)], TextSearch.Find(Units("PDF english"), "pdf"));
        Assert.Equal([(4, 11)], TextSearch.Find(Units("PDF english"), "ENGLISH"));
        // An engine line end ("\r\n" units) or a line break of a box matches one space.
        Assert.Equal([(0, 12)], TextSearch.Find(["P", "D", "F", "\r", "\n", "E", "n", "g", "l", "i", "s", "h"], "pdf  English"));
        Assert.Equal([(0, 6)], TextSearch.Find(Units("abcdef"), "abc def", i => i == 3));
        Assert.Empty(TextSearch.Find(Units("abcdef"), "abc def"));
        Assert.Equal([(0, 2), (2, 4)], TextSearch.Find(Units("aaaaa"), "aa")); // non-overlapping
        Assert.Equal([(3, 5)], TextSearch.Find(Units("복사 가능한 한글"), "가능"));
        Assert.Empty(TextSearch.Find(Units("abc"), "   "));
        Assert.Equal("a b", TextSearch.NormalizeQuery("  a \t\n b "));
        // Multi-character units (ligatures) map back to whole units.
        Assert.Equal([(1, 3)], TextSearch.Find(["o", "ffi", "ce"], "fice"));
    }

    private static TextBoxAnnotation Box(string text, double x, double y, double width = 300) =>
        new(Guid.NewGuid(), false, Affine.Translation(x, y), text, [new TextRun(0, text.Length, "Malgun Gothic", 16, RgbColor.Black, false, false, false, false)],
            Alignment.Left, width, TextBoxStyle.Default, []);

    [Fact]
    public void TextBoxesAreSearchedLineByLineAndOffPaperCharactersAreNotFound()
    {
        var model = NewPageSpec.Default.CreateDocument();
        var page = model.Pages[0];
        var top = Box("Hello World\nhello again", 50, 300);
        var above = Box("say HELLO", 50, 100);
        var gone = Box("hello outside", -2000, 100);
        var half = Box("cut hello", -40, 500); // "cut " hangs off the left edge, "hello" is on the paper
        page = page with { Annotations = [top, above, gone, half] };
        var hits = DocumentSearch.SearchPage(page, [], "hello");
        Assert.Equal([above.Id, top.Id, top.Id, half.Id], hits.Select(h => h.BoxId!.Value)); // reading order by position, not z-order
        Assert.Equal((12, 17), (hits[2].Start, hits[2].End));
        Assert.All(hits, h => Assert.Single(h.Quads));
        var q = hits[1].Quads[0];
        Assert.True(q.UpperLeft.X >= 50 && q.UpperLeft.Y >= 300 && q.LowerRight.X > q.UpperLeft.X && q.LowerRight.Y > q.UpperLeft.Y);
        Assert.Empty(DocumentSearch.SearchPage(page, [], "outside"));
        // A phrase over a box line break is found as one result with two line quads.
        var across = Assert.Single(DocumentSearch.SearchPage(page, [], "world hello"));
        Assert.Equal(2, across.Quads.Length);
    }

    [Fact]
    public void PdfTextIsFoundInPageOrderAndFollowsPageTransforms()
    {
        string path = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(path, 3);
        using var source = PdfSourceDocument.Open(path);
        var model = source.CreateModel();
        var glyphs = new PageGlyphSource(source);
        var hits = DocumentSearch.Run(model, glyphs.Glyphs, "english 123", CancellationToken.None);
        Assert.Equal(model.Pages.Select(p => p.Id), hits.Select(h => h.PageId));
        Assert.All(hits, h => Assert.Null(h.BoxId));
        var before = hits[0].Quads[0];

        // Page 1 moved and reordered: results follow the page order; shrinking moves the quads with the content.
        var session = new DocumentSession(model);
        session.Execute(PageEdits.MovePages(session.Current, [model.Pages[2].Id], 0));
        session.Execute(PageTransforms.Shrink(session.Current, [model.Pages[0].Id], 50, alignment: 0));
        hits = DocumentSearch.Run(session.Current, glyphs.Glyphs, "English 123", CancellationToken.None);
        Assert.Equal(session.Current.Pages.Select(p => p.Id), hits.Select(h => h.PageId));
        var after = hits.Single(h => h.PageId == model.Pages[0].Id).Quads[0];
        Assert.Equal(before.UpperLeft.X * .5, after.UpperLeft.X, 1);
        Assert.Equal(before.UpperLeft.Y * .5, after.UpperLeft.Y, 1);

        // Content pushed off the paper is not found; moved back it is found again.
        var page = session.Current.Pages.Single(p => p.Id == model.Pages[1].Id);
        session.Execute(PageTransforms.Resize(session.Current, [page.Id], 20, 20, PageResizeMode.ClipContent, alignment: 8));
        Assert.DoesNotContain(DocumentSearch.Run(session.Current, glyphs.Glyphs, "English", CancellationToken.None), h => h.PageId == page.Id);
        session.Undo();
        Assert.Contains(DocumentSearch.Run(session.Current, glyphs.Glyphs, "English", CancellationToken.None), h => h.PageId == page.Id);
    }

    [Fact]
    public void ACancelledSearchStops()
    {
        var model = NewPageSpec.Default.CreateDocument();
        model = model with { Pages = model.Pages.Add(model.Pages[0] with { Id = Guid.NewGuid() }) };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DocumentSearch.Run(model, _ => [], "a", cancelled.Token));
        Assert.Empty(DocumentSearch.Run(model, _ => [], " ", CancellationToken.None));
    }
}
