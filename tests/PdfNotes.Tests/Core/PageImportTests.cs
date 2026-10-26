using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

/// <summary>D13g / A33: range text of the import dialog and the one-Undo insert of imported pages (spec 11.9).</summary>
public sealed class PageImportTests
{
    [Theory]
    [InlineData("전체", 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData("1-3, 5", 5, new[] { 0, 1, 2, 4 })]
    [InlineData(" 5 , 1-2 ,2", 5, new[] { 0, 1, 4 })]
    [InlineData("3", 3, new[] { 2 })]
    [InlineData("2~3，1", 4, new[] { 0, 1, 2 })]
    public void RangesAreTakenInFileOrderOnce(string text, int count, int[] expected)
    {
        Assert.Null(PageRanges.TryParse(text, count, out var indices));
        Assert.Equal(expected, indices);
    }

    [Theory]
    [InlineData("", 5, PageRangeError.Empty)]
    [InlineData(" , ", 5, PageRangeError.Empty)]
    [InlineData("0", 5, PageRangeError.OutOfRange)]
    [InlineData("4-6", 5, PageRangeError.OutOfRange)]
    [InlineData("3-1", 5, PageRangeError.Syntax)]
    [InlineData("1-2-3", 5, PageRangeError.Syntax)]
    [InlineData("a", 5, PageRangeError.Syntax)]
    [InlineData("-2", 5, PageRangeError.Syntax)]
    [InlineData("1.5", 5, PageRangeError.Syntax)]
    public void InvalidRangesAreRefused(string text, int count, PageRangeError error) =>
        Assert.Equal(error, PageRanges.TryParse(text, count, out _));

    [Fact]
    public void FormatIsTheShortestTextAndParsesBack()
    {
        Assert.Equal("전체", PageRanges.Format([2, 0, 1], 3));
        Assert.Equal("1-3, 5, 7-8", PageRanges.Format([7, 0, 1, 2, 4, 6, 1], 9));
        Assert.Equal("", PageRanges.Format([], 9));
        Assert.Null(PageRanges.TryParse(PageRanges.Format([7, 0, 1, 2, 4, 6], 9), 9, out var back));
        Assert.Equal([0, 1, 2, 4, 6, 7], back);
    }

    [Fact]
    public void ImportIsOneUndoUnitAndKeepsResourcesApart()
    {
        var mine = TestModels.PngResource();
        var model = TestModels.Empty(2);
        var session = new DocumentSession(model);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, model.Pages[0].Id, TestModels.Image(mine), mine));
        var before = session.Current;

        // Same ID, different bytes (another file's image): renamed. Same ID and bytes: shared, not added twice.
        var clash = Resource.Create(mine.Id, "image/png", TestModels.TinyPng.Concat(new byte[7]).ToArray());
        var shared = mine;
        var fresh = TestModels.PngResource(3);
        var pageA = new Page(Guid.NewGuid(), TestModels.A4(), [TestModels.Image(clash), TestModels.Ink()]);
        var pageB = new Page(Guid.NewGuid(), TestModels.A4(90), [TestModels.Image(shared) with { Id = Guid.NewGuid() }, TestModels.Image(fresh)]);
        var (command, ids) = PageImports.Insert(before, 1, [pageA, pageB], [clash, shared, fresh]);
        Assert.Equal(PageImports.Label, command.Label);
        Assert.True(session.Execute(command));
        var after = session.Current;
        Assert.Equal([before.Pages[0].Id, pageA.Id, pageB.Id, before.Pages[1].Id], after.Pages.Select(p => p.Id));
        Assert.Equal(new[] { pageA.Id, pageB.Id }, ids);
        var renamed = after.Pages[1].Annotations.OfType<ImageAnnotation>().Single().ResourceId;
        Assert.NotEqual(mine.Id, renamed);
        Assert.Equal(clash.Sha256, after.Resources[renamed].Sha256);
        Assert.Equal(mine.Sha256, after.Resources[mine.Id].Sha256);
        Assert.Equal(3, after.Resources.Count);

        Assert.True(session.Undo());
        Assert.Equal(before.Pages.Select(p => p.Id), session.Current.Pages.Select(p => p.Id));
        Assert.Equal(before.Resources.Keys.Order(), session.Current.Resources.Keys.Order());
        Assert.True(session.Redo());
        Assert.Equal(after.Pages.Select(p => p.Id), session.Current.Pages.Select(p => p.Id));
    }

    [Fact]
    public void ImportRefusesNothingToInsertAndBadPositions()
    {
        var model = TestModels.Empty(1);
        var page = Page.Create(TestModels.A4());
        Assert.Throws<ArgumentException>(() => PageImports.Insert(model, 0, [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageImports.Insert(model, 2, [page], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageImports.Insert(model, -1, [page], []));
    }
}
