using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

/// <summary>D13j (spec 11.9–11.11): export range groups, several files in one insert, clearing the notes of pages.</summary>
public sealed class PageEditingTests
{
    [Fact]
    public void SemicolonsSplitTheRangeIntoFiles()
    {
        Assert.Null(PageRanges.TryParseGroups("1,3-10; 2,11-20; 21-30", 30, out var groups, out _));
        Assert.Equal(3, groups.Length);
        Assert.Equal([0, 2, 3, 4, 5, 6, 7, 8, 9], groups[0]);
        Assert.Equal([1, .. Enumerable.Range(10, 10)], groups[1]);
        Assert.Equal(Enumerable.Range(20, 10), groups[2]);
        Assert.Null(PageRanges.TryParseGroups("전체", 4, out groups, out _));
        Assert.Equal([[0, 1, 2, 3]], groups);
        Assert.Null(PageRanges.TryParseGroups(" 2 ; ", 4, out groups, out _)); // a trailing ; is no file
        Assert.Equal([[1]], groups);
        Assert.Null(PageRanges.TryParseGroups("1-2；3", 4, out groups, out _)); // full-width semicolon (Korean IME)
        Assert.Equal(2, groups.Length);
    }

    [Theory]
    [InlineData("", PageRangeError.Empty, -1)]
    [InlineData(" ; ", PageRangeError.Empty, -1)]
    [InlineData("1-3; x", PageRangeError.Syntax, 1)]
    [InlineData("1-3; 2-4; 9", PageRangeError.OutOfRange, 2)]
    [InlineData("5-2", PageRangeError.Syntax, 0)]
    public void ARangeGroupErrorNamesItsFile(string text, PageRangeError error, int bad)
    {
        Assert.Equal(error, PageRanges.TryParseGroups(text, 5, out var groups, out int badGroup));
        Assert.Empty(groups);
        Assert.Equal(bad, badGroup);
    }

    [Fact]
    public void SeveralFilesAreOneUndoUnitInOrderWithResourcesKeptApart()
    {
        var model = TestModels.Empty(2);
        var session = new DocumentSession(model);
        var a = TestModels.PngResource();
        var b = Resource.Create(a.Id, "image/png", TestModels.TinyPng.Concat(new byte[5]).ToArray()); // same ID, other bytes
        var first = new[] { new Page(Guid.NewGuid(), TestModels.A4(), [TestModels.Image(a)]), new Page(Guid.NewGuid(), TestModels.A4(), []) };
        var second = new[] { new Page(Guid.NewGuid(), TestModels.A4(90), [TestModels.Image(b)]) };
        var (command, ids) = PageImports.Insert(session.Current, 1, [(first, [a]), (second, [b])]);
        Assert.True(session.Execute(command));
        Assert.Equal([first[0].Id, first[1].Id, second[0].Id], ids);
        var after = session.Current;
        Assert.Equal([model.Pages[0].Id, first[0].Id, first[1].Id, second[0].Id, model.Pages[1].Id], after.Pages.Select(p => p.Id));
        var renamed = after.Pages[3].Annotations.OfType<ImageAnnotation>().Single().ResourceId;
        Assert.NotEqual(a.Id, renamed);
        Assert.Equal(b.Sha256, after.Resources[renamed].Sha256);
        Assert.Equal(a.Sha256, after.Resources[a.Id].Sha256);
        Assert.Equal(PageImports.Label, session.UndoLabel);
        Assert.True(session.Undo());
        Assert.Equal(model.Pages.Select(p => p.Id), session.Current.Pages.Select(p => p.Id));
        Assert.Empty(session.Current.Resources);
    }

    [Fact]
    public void ClearingPagesRemovesTheirNotesLockedOnesTooInOneUndo()
    {
        var resource = TestModels.PngResource();
        var session = new DocumentSession(TestModels.Empty(3));
        var pages = session.Current.Pages;
        session.Execute(DocumentEdits.AddAnnotation(session.Current, pages[0].Id, TestModels.Ink()));
        session.Execute(DocumentEdits.AddAnnotation(session.Current, pages[0].Id, TestModels.Ink(20, locked: true)));
        session.Execute(DocumentEdits.AddAnnotation(session.Current, pages[1].Id, TestModels.Image(resource), resource));
        var keep = TestModels.Ink(30);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, pages[2].Id, keep));
        var before = session.Current;

        Assert.True(session.Execute(DocumentEdits.ClearPages(session.Current, [pages[0].Id, pages[1].Id])));
        Assert.Equal(DocumentEdits.ClearPagesLabel, session.UndoLabel);
        Assert.Empty(session.Current.Pages[0].Annotations);
        Assert.Empty(session.Current.Pages[1].Annotations);
        Assert.Equal([keep.Id], session.Current.Pages[2].Annotations.Select(a => a.Id)); // other pages untouched
        Assert.Empty(session.Current.Resources); // the image nobody uses any more

        Assert.True(session.Undo());
        Assert.Equal(before.Pages.Select(p => p.Annotations.Select(a => a.Id).ToArray()), session.Current.Pages.Select(p => p.Annotations.Select(a => a.Id).ToArray()));
        Assert.Single(session.Current.Resources);
        var blank = TestModels.Empty(1);
        Assert.Empty(DocumentEdits.ClearPages(blank, [blank.Pages[0].Id]).Operations); // nothing to delete: an empty command
    }

    [Fact]
    public void AnUnsavedSessionIsDirtyWithNothingToUndo()
    {
        var session = new DocumentSession(TestModels.Empty(2), unsaved: true);
        Assert.True(session.IsDirty);
        Assert.False(session.CanUndo);
        session.MarkSaved(session.CreateSnapshot());
        Assert.False(session.IsDirty);
        Assert.False(new DocumentSession(TestModels.Empty(1)).IsDirty);
    }
}
