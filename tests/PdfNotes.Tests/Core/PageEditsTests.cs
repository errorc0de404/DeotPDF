using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

/// <summary>D10a: page reorder, rotation, copy/paste snapshots and their Undo (spec 11.1–11.5).</summary>
public sealed class PageEditsTests
{
    private static (DocumentSession Session, Guid[] Ids) Five()
    {
        var model = DocumentModel.Create(Enumerable.Range(0, 5).Select(i => TestModels.A4() with { WidthPt = 500 + i }));
        return (new DocumentSession(model), [.. model.Pages.Select(p => p.Id)]);
    }

    private static Guid[] Order(DocumentSession s) => [.. s.Current.Pages.Select(p => p.Id)];

    [Fact]
    public void MovingSeveralPagesKeepsTheirRelativeOrderAndUndoRestoresTheSamePages()
    {
        var (s, ids) = Five();
        var before = s.Current;
        Assert.True(s.Execute(PageEdits.MovePages(s.Current, [ids[3], ids[1]], 5)));
        Assert.Equal([ids[0], ids[2], ids[4], ids[1], ids[3]], Order(s));
        Assert.Equal("undo.movePages", s.UndoLabel);
        s.Undo();
        Assert.Equal(before.Pages, s.Current.Pages);
        Assert.True(s.Execute(PageEdits.MovePages(s.Current, [ids[3], ids[4]], 0)));
        Assert.Equal([ids[3], ids[4], ids[0], ids[1], ids[2]], Order(s));
        Assert.True(s.Execute(PageEdits.MovePages(s.Current, [ids[0]], 4))); // before the page now at 4 (ids[2])
        Assert.Equal([ids[3], ids[4], ids[1], ids[0], ids[2]], Order(s));
        Assert.True(PageEdits.MovePages(s.Current, [ids[1], ids[0]], 2).IsEmpty); // already there
    }

    [Fact]
    public void RotationTurnsByQuarterTurnsAndKeepsAnnotationsUnrotated()
    {
        var (s, ids) = Five();
        var ink = TestModels.Ink();
        s.Execute(DocumentEdits.AddAnnotation(s.Current, ids[0], ink));
        s.Execute(PageEdits.RotatePages(s.Current, [ids[0], ids[1]], clockwise: true));
        Assert.Equal([90, 90, 0], s.Current.Pages.Take(3).Select(p => p.Setup.Rotation));
        s.Execute(PageEdits.RotatePages(s.Current, [ids[0]], clockwise: false));
        s.Execute(PageEdits.RotatePages(s.Current, [ids[0]], clockwise: false));
        Assert.Equal(270, s.Current.Pages[0].Setup.Rotation);
        Assert.Same(ink, s.Current.Pages[0].Annotations[0]);
        var turned = s.Current.Pages[1].Setup;
        Assert.Equal((turned.HeightPt, turned.WidthPt), PageEdits.DisplayedSize(turned));
    }

    [Fact]
    public void CopiedPagesGetNewIdsKeepLocksAndShareOrReAddImages()
    {
        var (s, ids) = Five();
        var resource = TestModels.PngResource();
        var image = TestModels.Image(resource);
        var locked = TestModels.Ink(locked: true);
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(10, 10), "강조",
            [new TextRun(0, 2, "Malgun Gothic", 12, RgbColor.Black, false, false, false, false)], TextAlignment.Left, 100, TextBoxStyle.Default,
            [new TextHighlightRange(0, 1, HighlightHeightMode.Auto, null, new(255, 230, 0), .4)]);
        s.Execute(DocumentEdits.AddAnnotation(s.Current, ids[1], image, resource));
        s.Execute(DocumentEdits.AddAnnotation(s.Current, ids[1], locked));
        s.Execute(DocumentEdits.AddAnnotation(s.Current, ids[1], box));

        var clipboard = PageClipboard.Capture(s.Current, [ids[3], ids[1]], PageCopyMode.WithAnnotations);
        Assert.Equal([ids[1], ids[3]], clipboard.Pages.Select(p => p.Id)); // document order
        // Delete the original: its image resource leaves the model, the clipboard still has it.
        s.Execute(DocumentEdits.DeletePages(s.Current, [ids[1]]));
        Assert.Empty(s.Current.Resources);
        var (command, pasted) = clipboard.Paste(s.Current, 1);
        Assert.True(s.Execute(command));
        var copy = s.Current.Pages[1];
        Assert.Equal(pasted, new[] { s.Current.Pages[1].Id, s.Current.Pages[2].Id });
        Assert.DoesNotContain(copy.Id, ids);
        Assert.Equal(3, copy.Annotations.Count);
        Assert.All(copy.Annotations, a => Assert.NotEqual(Guid.Empty, a.Id));
        Assert.DoesNotContain(copy.Annotations, a => a.Id == image.Id || a.Id == locked.Id || a.Id == box.Id);
        Assert.True(copy.Annotations[1].Locked);
        Assert.Equal(box.Highlights, ((TextBoxAnnotation)copy.Annotations[2]).Highlights);
        Assert.True(s.Current.Resources.ContainsKey(resource.Id));
        Assert.Null(ModelValidator.ValidateDocument(s.Current));

        // A second paste shares the (immutable) resource instead of adding it twice.
        var (again, _) = clipboard.Paste(s.Current, s.Current.Pages.Count);
        Assert.True(s.Execute(again));
        Assert.Single(s.Current.Resources);
        Assert.Equal(2, s.Current.AllAnnotations.OfType<ImageAnnotation>().Count(i => i.ResourceId == resource.Id));
        s.Undo(); s.Undo();
        Assert.Equal(4, s.Current.Pages.Count);
    }

    [Fact]
    public void PageOnlyCopiesLeaveAppAnnotationsOutAndClipboardsStayInTheirDocument()
    {
        var (s, ids) = Five();
        s.Execute(DocumentEdits.AddAnnotation(s.Current, ids[0], TestModels.Ink()));
        var pageOnly = PageClipboard.Capture(s.Current, [ids[0]], PageCopyMode.PageOnly);
        var (command, pasted) = pageOnly.Paste(s.Current, 5);
        s.Execute(command);
        var copy = s.Current.GetPage(pasted[0]);
        Assert.Empty(copy.Annotations);
        Assert.Equal(s.Current.Pages[0].Setup, copy.Setup);
        Assert.Equal("undo.pastePagesOnly", s.UndoLabel);
        var other = TestModels.Empty();
        Assert.False(pageOnly.CanPasteInto(other));
        Assert.Throws<InvalidOperationException>(() => pageOnly.Paste(other, 0));
    }

    [Fact]
    public void DeletingEveryPageIsRefusedAsAWhole()
    {
        var (s, ids) = Five();
        Assert.Throws<InvalidOperationException>(() => DocumentEdits.DeletePages(s.Current, ids));
        Assert.True(s.Execute(DocumentEdits.DeletePages(s.Current, ids.Skip(1))));
        Assert.Throws<InvalidOperationException>(() => DocumentEdits.DeletePages(s.Current, [ids[0]]));
        s.Undo();
        Assert.Equal(ids, Order(s));
    }
}
