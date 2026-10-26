using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Text;

namespace PdfNotes.Tests.Core;

/// <summary>D14g (spec 2.0): paste position, moving to another page, erasing text box highlights, history targets.</summary>
public sealed class D14gTests
{
    private static DocumentModel WithInk(params PageSetup[] setups)
    {
        var model = DocumentModel.Create(setups);
        return model with { Pages = [model.Pages[0] with { Annotations = [TestModels.Ink(100)] }, .. model.Pages.Skip(1)] };
    }

    private static Rect2 BoundsOf(DocumentModel model, Guid id) => AnnotationGeometry.Bounds([model.FindAnnotation(id)!.Annotation]);

    [Fact]
    public void APasteOnItsOwnPageStepsAsideAndKeepsSteppingPastEarlierCopies()
    {
        var model = WithInk(TestModels.A4());
        var page = model.Pages[0].Id;
        var ink = model.Pages[0].Annotations[0];
        var data = AnnotationClipboard.Capture(model, page, [ink.Id]);
        var session = new DocumentSession(model);
        var first = data.Paste(session.Current, page); session.Execute(first.Command);
        var second = data.Paste(session.Current, page); session.Execute(second.Command);
        var original = BoundsOf(session.Current, ink.Id);
        Assert.Equal(original.X + 10, BoundsOf(session.Current, first.Ids[0]).X, 6);
        Assert.Equal(original.X + 20, BoundsOf(session.Current, second.Ids[0]).X, 6); // past the first copy, not on it
        Assert.Equal(original.Y + 20, BoundsOf(session.Current, second.Ids[0]).Y, 6);
    }

    [Fact]
    public void APasteAtAPointCentersTheCopiesThere()
    {
        var model = WithInk(TestModels.A4(), TestModels.A4());
        var data = AnnotationClipboard.Capture(model, model.Pages[0].Id, [model.Pages[0].Annotations[0].Id]);
        var (command, ids) = data.Paste(model, model.Pages[1].Id, new Point2(300, 400));
        var session = new DocumentSession(model);
        session.Execute(command);
        var b = BoundsOf(session.Current, ids[0]);
        Assert.Equal(300, b.X + b.Width / 2, 6);
        Assert.Equal(400, b.Y + b.Height / 2, 6);
        Assert.Equal(1, session.Current.IndexOfPage(session.Current.FindAnnotation(ids[0])!.Page.Id));
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void APasteOnAnotherPageKeepsWhereItShowsOnThePaperWhateverTheRotation(int rotation)
    {
        var model = WithInk(TestModels.A4(), TestModels.A4(rotation));
        var ink = (StrokeAnnotation)model.Pages[0].Annotations[0];
        var data = AnnotationClipboard.Capture(model, model.Pages[0].Id, [ink.Id]);
        var (command, ids) = data.Paste(model, model.Pages[1].Id);
        var session = new DocumentSession(model);
        session.Execute(command);
        var pasted = (StrokeAnnotation)session.Current.FindAnnotation(ids[0])!.Annotation;
        var toDisplay = session.Current.Pages[1].Setup.Frame.CanonicalToDisplay;
        for (int i = 0; i < ink.Points.Length; i++)
        {
            var shown = pasted.Transform.Then(toDisplay).Transform(pasted.Points[i]);
            var was = ink.Transform.Transform(ink.Points[i]); // page 0 is not rotated: canonical = display
            Assert.Equal(was.X, shown.X, 6);
            Assert.Equal(was.Y, shown.Y, 6);
        }
    }

    [Fact]
    public void MovingToAnotherPageKeepsIdsAndOrderAndIsOneUndoUnit()
    {
        var model = TestModels.Empty(2);
        InkStroke a = TestModels.Ink(10), b = TestModels.Ink(40), c = TestModels.Ink(70);
        var other = TestModels.Ink(5);
        model = model with { Pages = [model.Pages[0] with { Annotations = [a, b, c] }, model.Pages[1] with { Annotations = [other] }] };
        var session = new DocumentSession(model);
        var moved = new Annotation[] { c, a }.Select(x => AnnotationGeometry.Similarity(x, default, move: new(0, 50))).ToArray();
        Assert.True(session.Execute(DocumentEdits.MoveToPage(session.Current, model.Pages[0].Id, model.Pages[1].Id, moved)));
        Assert.Equal([b.Id], session.Current.Pages[0].Annotations.Select(x => x.Id));
        Assert.Equal([other.Id, a.Id, c.Id], session.Current.Pages[1].Annotations.Select(x => x.Id)); // on top, in their stacking order
        Assert.Equal("undo.moveToPage", session.UndoLabel);
        session.Undo();
        Assert.Equal([a.Id, b.Id, c.Id], session.Current.Pages[0].Annotations.Select(x => x.Id));
        Assert.Equal([other.Id], session.Current.Pages[1].Annotations.Select(x => x.Id));
        Assert.Same(a, session.Current.Pages[0].Annotations[0]); // the very same annotations come back
        var locked = model with { Pages = [model.Pages[0] with { Annotations = [a with { Locked = true }] }, model.Pages[1]] };
        Assert.Throws<AnnotationLockedException>(() => DocumentEdits.MoveToPage(locked, locked.Pages[0].Id, locked.Pages[1].Id, [a]));
    }

    [Fact]
    public void TheEraserTakesATouchedTextBoxHighlightRangeAndLeavesTheText()
    {
        var box = TextEditingTests.Box("first second third", 300) with { Transform = Affine.Translation(100, 100) };
        var range = new TextHighlightRange(0, 0, HighlightHeightMode.Auto, null, new(255, 230, 0), .4);
        box = TextHighlights.Add(TextHighlights.Add(box, 0, 5, range), 13, 5, range); // "first", "third"
        var model = TestModels.Empty();
        model = model with { Pages = [model.Pages[0] with { Annotations = [box] }] };
        var lines = TextLayout.HighlightLines(box, TextLayout.Measure(box).Characters).ToArray();
        var first = lines[0].Lines[0];
        var touch = box.Transform.Transform(new(first.X + first.Width / 2, first.Y + first.Height / 2));
        var gesture = new EraseGesture(model, model.Pages[0].Id, EraserMode.Area, 4, b => TextLayout.HighlightLines(b, TextLayout.Measure(b).Characters));
        Assert.True(gesture.AddPoint(touch));
        var session = new DocumentSession(model);
        Assert.True(session.Execute(gesture.Commit()));
        var after = (TextBoxAnnotation)session.Current.Pages[0].Annotations.Single();
        Assert.Equal(box.Text, after.Text);
        Assert.Equal(13, Assert.Single(after.Highlights).Start); // only the touched range went
        // Without the layout the eraser leaves text box highlights alone.
        var blind = new EraseGesture(model, model.Pages[0].Id, EraserMode.Area, 4);
        Assert.False(blind.AddPoint(touch));
    }

    [Fact]
    public void HistoryTargetsNameThePagesAndAnnotationsOfAStep()
    {
        var model = WithInk(TestModels.A4(), TestModels.A4());
        var session = new DocumentSession(model);
        var ink = model.Pages[0].Annotations[0];
        session.Execute(DocumentEdits.MoveToPage(session.Current, model.Pages[0].Id, model.Pages[1].Id, [ink]));
        var (pages, annotations) = EditTargets.Of(session.UndoOperations);
        Assert.Equal([model.Pages[0].Id, model.Pages[1].Id], pages);
        Assert.Equal([ink.Id], annotations);
        session.Undo();
        Assert.Equal([ink.Id], EditTargets.Of(session.RedoOperations).Annotations);
        Assert.Empty(session.UndoOperations);
    }

    [Fact]
    public void TheConvertedFlagOfAPageSourceIsNeverSaved()
    {
        var setup = new PageSetup(600, 800, 0, PageSource.Pdf(0) with { ForeignConverted = true }, PageBackground.Blank);
        var model = DocumentModel.Create([setup]);
        var read = SchemaV6.Read(SchemaV6.Serialize(model).Header, [], _ => null).Model!;
        Assert.False(read.Pages[0].Setup.Source.ForeignConverted);
        Assert.Equal(0, read.Pages[0].Setup.Source.PdfPageIndex);
        Assert.NotEqual(setup, setup with { Source = PageSource.Pdf(0) }); // the render key tells the two apart
    }
}
