using System.Runtime.InteropServices;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

public class DocumentSessionTests
{
    private static (DocumentSession Session, Guid PageId) NewSession(HistoryBudget? budget = null)
    {
        var model = TestModels.Empty(2);
        return (new DocumentSession(model, budget), model.Pages[0].Id);
    }

    private static void AddInk(DocumentSession session, Guid pageId, double x = 10) =>
        Assert.True(session.Execute(DocumentEdits.AddAnnotation(session.Current, pageId, TestModels.Ink(x))));

    [Fact]
    public void EditSaveEditUndoReturnsToCleanAndRedoIsDirtyAgain()
    {
        var (session, page) = NewSession();
        Assert.False(session.IsDirty);
        AddInk(session, page);
        Assert.True(session.IsDirty);
        session.MarkSaved(session.CreateSnapshot());
        Assert.False(session.IsDirty);
        var saved = session.Current;
        AddInk(session, page, 50);
        Assert.True(session.IsDirty);
        Assert.True(session.Undo());
        Assert.False(session.IsDirty);
        Assert.Same(saved.Pages[0].Annotations[0], session.Current.Pages[0].Annotations[0]);
        Assert.Single(session.Current.Pages[0].Annotations);
        Assert.True(session.Redo());
        Assert.True(session.IsDirty);
        Assert.Equal(2, session.Current.Pages[0].Annotations.Count);
    }

    [Fact]
    public void NewEditAfterUndoDiscardsRedoAndASavedStateOnTheDiscardedBranchNeverLooksSaved()
    {
        var (session, page) = NewSession();
        AddInk(session, page);                       // r2
        session.MarkSaved(session.CreateSnapshot()); // saved = r2
        session.Undo();                              // back to r1
        Assert.True(session.CanRedo);
        Assert.True(session.IsDirty);
        AddInk(session, page, 70);                   // new branch
        Assert.False(session.CanRedo);
        Assert.Equal(0, session.RedoCount);
        Assert.True(session.IsDirty);
        session.Undo();                              // r1 again, still not the saved r2 content
        Assert.True(session.IsDirty);
        Assert.Empty(session.Current.Pages[0].Annotations);
        Assert.False(session.Redo() && session.Redo()); // only the new branch can be redone
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void CompositeDeleteUndoRestoresIdsStackingOrderAndImageResourceInOneStep()
    {
        var (session, page) = NewSession();
        var resource = TestModels.PngResource();
        var image = TestModels.Image(resource);
        var items = new Annotation[] { TestModels.Ink(1), TestModels.Ink(2), image, TestModels.Ink(4), TestModels.Ink(5) };
        foreach (var item in items)
            session.Execute(DocumentEdits.AddAnnotation(session.Current, page, item, item == image ? resource : null));
        var before = session.Current;
        int undoBefore = session.UndoCount;

        session.Execute(DocumentEdits.DeleteAnnotations(session.Current, [items[0].Id, image.Id, items[3].Id]));
        Assert.Equal([items[1].Id, items[4].Id], session.Current.Pages[0].Annotations.Select(a => a.Id));
        Assert.Empty(session.Current.Resources); // orphaned image data is released with the delete
        Assert.Equal(undoBefore + 1, session.UndoCount);

        Assert.True(session.Undo());
        Assert.Equal(before.Pages[0].Annotations, session.Current.Pages[0].Annotations); // same instances, same order
        Assert.Same(resource, session.Current.Resources[resource.Id]);
        Assert.True(session.Redo());
        Assert.Equal(2, session.Current.Pages[0].Annotations.Count);
        Assert.Empty(session.Current.Resources);
    }

    [Fact]
    public void FailingCommandChangesNothingAndLockedAnnotationsCannotBeDeleted()
    {
        var (session, page) = NewSession();
        var locked = TestModels.Ink(locked: true);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, page, locked));
        session.MarkSaved(session.CreateSnapshot());
        var model = session.Current;
        long revision = session.Revision;

        Assert.Throws<AnnotationLockedException>(() => DocumentEdits.DeleteAnnotations(model, [locked.Id]));
        // Second operation conflicts (wrong index), so the valid first operation must not be applied either.
        var broken = new EditCommand("broken", [
            new InsertAnnotationOperation(page, 0, TestModels.Ink(99)),
            new RemoveAnnotationOperation(page, 5, locked)]);
        Assert.Throws<EditConflictException>(() => session.Execute(broken));
        // Inserting an existing id or an invalid annotation is rejected as well.
        Assert.Throws<EditConflictException>(() => session.Execute(DocumentEdits.AddAnnotation(model, page, locked with { Locked = false })));
        Assert.Throws<EditConflictException>(() => session.Execute(DocumentEdits.AddAnnotation(model, page, TestModels.Ink(width: double.NaN))));

        Assert.Same(model, session.Current);
        Assert.Equal(revision, session.Revision);
        Assert.False(session.IsDirty);
        Assert.Equal(1, session.UndoCount);
    }

    [Fact]
    public void LastPageCannotBeDeletedAndMultiDeleteToZeroIsRejectedWhole()
    {
        var (session, _) = NewSession();
        var ids = session.Current.Pages.Select(p => p.Id).ToArray();
        Assert.Throws<InvalidOperationException>(() => DocumentEdits.DeletePages(session.Current, ids));
        // Even a hand-built command cannot leave zero pages.
        var model = session.Current;
        var removeAll = new EditCommand("all", [new RemovePageOperation(1, model.Pages[1]), new RemovePageOperation(0, model.Pages[0])]);
        Assert.Throws<EditConflictException>(() => session.Execute(removeAll));
        Assert.Same(model, session.Current);

        session.Execute(DocumentEdits.DeletePages(session.Current, [ids[0]]));
        Assert.Equal([ids[1]], session.Current.Pages.Select(p => p.Id));
        session.Undo();
        Assert.Equal(ids, session.Current.Pages.Select(p => p.Id));
        Assert.Same(model.Pages[0], session.Current.Pages[0]);
    }

    [Fact]
    public void SaveCompletingAfterNewEditKeepsDocumentDirty()
    {
        var (session, page) = NewSession();
        AddInk(session, page);
        var snapshot = session.CreateSnapshot();
        AddInk(session, page, 40); // user keeps drawing while the save runs in the background
        Assert.Single(snapshot.Model.Pages[0].Annotations); // snapshot is unaffected by the new edit
        session.MarkSaved(snapshot);
        Assert.True(session.IsDirty);
        session.Undo();
        Assert.False(session.IsDirty); // undo reaches exactly the saved revision
        Assert.Throws<ArgumentException>(() => new DocumentSession(TestModels.Empty()).MarkSaved(snapshot));
    }

    [Fact]
    public void SnapshotsShareImageBytesInsteadOfCopyingThem()
    {
        var (session, page) = NewSession();
        var resource = TestModels.PngResource(1024 * 1024);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, page, TestModels.Image(resource), resource));
        var first = session.CreateSnapshot();
        AddInk(session, page);
        var second = session.CreateSnapshot();
        Assert.Same(ImmutableCollectionsMarshal.AsArray(first.Model.Resources[resource.Id].Payload),
            ImmutableCollectionsMarshal.AsArray(second.Model.Resources[resource.Id].Payload));
        Assert.Same(first.Model.Pages[1], second.Model.Pages[1]); // untouched pages are shared too
    }

    [Fact]
    public void EntryBudgetDropsOldestHistoryWithoutEverReportingSaved()
    {
        var (session, page) = NewSession();
        for (int i = 0; i < 250; i++) AddInk(session, page, i);
        Assert.Equal(HistoryBudget.Default.MaxEntries, session.UndoCount);
        while (session.Undo()) { }
        Assert.Equal(50, session.Current.Pages[0].Annotations.Count); // the oldest 50 edits can no longer be undone
        Assert.True(session.IsDirty);                                   // and the saved (empty) state is unreachable
        Assert.Equal(200, session.RedoCount);
    }

    [Fact]
    public void ByteBudgetDropsOldestEntriesButKeepsASingleOversizedEdit()
    {
        var budget = new HistoryBudget(200, 40_000);
        var (session, page) = NewSession(budget);
        for (int i = 0; i < 6; i++)
        {
            var resource = TestModels.PngResource(10_000);
            session.Execute(DocumentEdits.AddAnnotation(session.Current, page, TestModels.Image(resource), resource));
            Assert.InRange(session.HistoryBytes, 1, budget.MaxBytes);
        }
        Assert.InRange(session.UndoCount, 2, 4);
        Assert.Equal(6, session.Current.Pages[0].Annotations.Count);

        var huge = TestModels.PngResource(100_000);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, page, TestModels.Image(huge), huge));
        Assert.Equal(1, session.UndoCount);
        Assert.True(session.HistoryBytes > budget.MaxBytes);
        Assert.True(session.Undo()); // the oversized edit itself is still undoable
        Assert.False(session.Current.Resources.ContainsKey(huge.Id));
    }

    [Fact]
    public void CoalescedEditsFormOneUndoUnitUntilSealedOrSaved()
    {
        var (session, page) = NewSession();
        var ink = TestModels.Ink();
        session.Execute(DocumentEdits.AddAnnotation(session.Current, page, ink));
        void Widen(double width)
        {
            var current = (InkStroke)session.Current.FindAnnotation(ink.Id)!.Annotation;
            session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [current with { WidthPt = width }], "굵기", coalesceKey: "width"));
        }
        Widen(3); Widen(4); Widen(5); // one slider drag
        Assert.Equal(2, session.UndoCount);
        session.SealCoalescing();
        Widen(6);
        Assert.Equal(3, session.UndoCount);
        session.MarkSaved(session.CreateSnapshot());
        Widen(7); // must not merge across the save point, otherwise Undo would skip the saved state
        Assert.Equal(4, session.UndoCount);
        session.Undo();
        Assert.False(session.IsDirty);
        session.Undo(); session.Undo();
        Assert.Equal(2, ((InkStroke)session.Current.FindAnnotation(ink.Id)!.Annotation).WidthPt);
    }

    [Fact]
    public void ZOrderChangesKeepRelativeOrderAndUndoRestoresIt()
    {
        var (session, page) = NewSession();
        var items = Enumerable.Range(0, 5).Select(i => (Annotation)TestModels.Ink(i)).ToArray();
        foreach (var item in items) session.Execute(DocumentEdits.AddAnnotation(session.Current, page, item));
        var original = session.Current.Pages[0].Annotations;
        Guid[] Order() => session.Current.Pages[0].Annotations.Select(a => a.Id).ToArray();
        Guid[] Ids(params int[] i) => i.Select(n => items[n].Id).ToArray();

        session.Execute(DocumentEdits.ChangeZOrder(session.Current, page, Ids(0, 2), ZOrderChange.ToFront));
        Assert.Equal(Ids(1, 3, 4, 0, 2), Order());
        session.Execute(DocumentEdits.ChangeZOrder(session.Current, page, Ids(0, 2), ZOrderChange.ToBack));
        Assert.Equal(Ids(0, 2, 1, 3, 4), Order());
        session.Execute(DocumentEdits.ChangeZOrder(session.Current, page, Ids(2), ZOrderChange.Forward));
        Assert.Equal(Ids(0, 1, 2, 3, 4), Order());
        session.Execute(DocumentEdits.ChangeZOrder(session.Current, page, Ids(3, 4), ZOrderChange.Backward));
        Assert.Equal(Ids(0, 1, 3, 4, 2), Order());
        Assert.True(DocumentEdits.ChangeZOrder(session.Current, page, Ids(0), ZOrderChange.ToBack).IsEmpty);

        while (session.UndoCount > items.Length) session.Undo();
        Assert.Equal(original, session.Current.Pages[0].Annotations);
    }

    [Fact]
    public void PageRotationAndLockingAreUndoableAndRaiseStateChanged()
    {
        var (session, page) = NewSession();
        int events = 0;
        session.StateChanged += (_, _) => events++;
        session.Execute(DocumentEdits.SetPageRotation(session.Current, page, 90));
        Assert.Equal(90, session.Current.Pages[0].Setup.Rotation);
        var ink = TestModels.Ink();
        session.Execute(DocumentEdits.AddAnnotation(session.Current, page, ink));
        session.Execute(DocumentEdits.SetLocked(session.Current, [ink.Id], true));
        Assert.Throws<AnnotationLockedException>(() =>
            DocumentEdits.UpdateAnnotations(session.Current, [ink with { Locked = true, WidthPt = 9 }]));
        Assert.True(DocumentEdits.SetLocked(session.Current, [ink.Id], true).IsEmpty);
        Assert.False(session.Execute(DocumentEdits.SetLocked(session.Current, [ink.Id], true))); // no-op: no history, not dirty-changing
        session.Undo(); session.Undo(); session.Undo();
        Assert.Equal(0, session.Current.Pages[0].Setup.Rotation);
        Assert.False(session.IsDirty);
        Assert.Equal(6, events);
    }
}
