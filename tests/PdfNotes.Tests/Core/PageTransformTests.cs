using System.Collections.Immutable;
using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Text;

namespace PdfNotes.Tests.Core;

public sealed class PageTransformTests
{
    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void FreePlacementUsesDisplayedPaperAndUndoRestoresAllTargets(int rotation)
    {
        var model = DocumentModel.Create([TestModels.A4(rotation) with { Source = PageSource.Pdf(0) }, TestModels.A4()]);
        var session = new DocumentSession(model);
        session.Execute(PageTransforms.Shrink(model, model.Pages.Select(p => p.Id), 60, freePosition: new(.12, -.07)));
        var p = session.Current.Pages[0].Setup;
        var original = model.Pages[0].Setup;
        var pos = p.Frame.CanonicalToDisplay.Transform(p.Content!.Transform.Transform(original.Frame.DisplayToCanonical.Transform(new(0, 0))));
        Assert.Equal(.12 * original.Frame.DisplayWidthPt, pos.X, 6);
        Assert.Equal(-.07 * original.Frame.DisplayHeightPt, pos.Y, 6);
        Assert.Equal(1, session.UndoCount); session.Undo(); Same(model, session.Current);
        session.Execute(PageTransforms.Resize(model, [model.Pages[0].Id], Units.PointsToMillimeters(original.Frame.DisplayWidthPt), Units.PointsToMillimeters(original.Frame.DisplayHeightPt), PageResizeMode.ClipContent, freePosition: new(.1, .2)));
        Assert.NotNull(session.Current.Pages[0].Setup.Content);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageTransforms.Shrink(model, model.Pages.Select(p => p.Id), 60, freePosition: new(double.NaN, 0)));
    }
    private static void Same(DocumentModel a, DocumentModel b) => Assert.Equal(SchemaV4.Serialize(a).Header, SchemaV4.Serialize(b).Header);
    public static IEnumerable<object[]> Frames() => from r in new[] { 0, 90, 180, 270 } from a in Enumerable.Range(0, 9) select new object[] { r, a };
    [Theory, MemberData(nameof(Frames))]
    public void DisplayedAnchorsAndCropPlacementAreCorrect(int rotation, int alignment)
    {
        var setup = TestModels.A4(rotation) with { Source = PageSource.Pdf(0) };
        var model = DocumentModel.Create([setup]); var id = model.Pages[0].Id;
        var session = new DocumentSession(model); session.Execute(PageTransforms.Shrink(model, [id], 80, alignment));
        var after = session.Current.Pages[0].Setup;
        Assert.Equal(setup.WidthPt, after.WidthPt); Assert.Equal(setup.HeightPt, after.HeightPt);
        var displayed = after.Frame.CanonicalToDisplay.Transform(after.Content!.Transform.Transform(setup.Frame.DisplayToCanonical.Transform(new(0, 0))));
        Assert.Equal(alignment % 3 / 2.0 * .2 * setup.Frame.DisplayWidthPt, displayed.X, 6);
        Assert.Equal(alignment / 3 / 2.0 * .2 * setup.Frame.DisplayHeightPt, displayed.Y, 6);
        session.Execute(PageTransforms.Shrink(session.Current, [id], 80, alignment)); Assert.Equal(.64, session.Current.Pages[0].Setup.Content!.Transform.M11, 10);
        session.Undo(); session.Undo(); Same(model, session.Current); Assert.False(session.IsDirty);
        session.Redo(); Assert.True(session.IsDirty);
    }
    [Fact]
    public void AllKindsLockedAndOutsideContentScaleOnceAndNewEditsUseCurrentCoordinates()
    {
        var model = TestModels.AllKinds(out _);
        var page = model.Pages[0]; var fragment = TestModels.Ink() with { Points = [], Outline = [[new(-30, -20), new(20, -20), new(20, 50)]], Locked = true };
        model = model with { Pages = model.Pages.SetItem(0, page with { Annotations = page.Annotations.Add(fragment) }) };
        var session = new DocumentSession(model); session.Execute(PageTransforms.Shrink(model, model.Pages.Select(p => p.Id), 80));
        Assert.Equal(1, session.UndoCount);
        foreach (var p in model.Pages)
        {
            var after = session.Current.GetPage(p.Id);
            Assert.Equal(p.Annotations.Select(a => a.Id), after.Annotations.Select(a => a.Id));
            Assert.Equal(p.Annotations.Select(a => a.Locked), after.Annotations.Select(a => a.Locked));
        }
        var t = session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single(); Assert.Equal(.8, t.LayoutScale); Assert.Equal(320, t.Runs[^1].SizePt);
        var outline = session.Current.FindAnnotation(fragment.Id)!.Annotation as InkStroke; Assert.Equal(new Point2(-24, -16), outline!.Outline[0][0]);
        var h = session.Current.AllAnnotations.OfType<PdfTextHighlight>().Single(); Assert.Equal(9.6, h.ManualHeightPt!.Value, 6);
        var fresh = TestModels.Ink(100); session.Execute(DocumentEdits.AddAnnotation(session.Current, page.Id, fresh)); Assert.Same(fresh, session.Current.FindAnnotation(fresh.Id)!.Annotation);
        session.Undo(); session.Undo(); Same(model, session.Current);
    }
    [Fact]
    public void MixedResizeFitNeverEnlargesAndClippingKeepsDataAndOneUndo()
    {
        var model = DocumentModel.Create([new(600, 800, 90, PageSource.Pdf(0), PageBackground.Blank), new(100, 100, 0, PageSource.Generated, PageBackground.Grid)]);
        var session = new DocumentSession(model); var ids = model.Pages.Select(p => p.Id).ToArray();
        session.Execute(PageTransforms.Resize(model, ids, 200, 400, PageResizeMode.FitContent));
        Assert.Equal(Units.MillimetersToPoints(200) / 800, session.Current.Pages[0].Setup.Content!.Transform.M11, 9);
        Assert.Null(session.Current.Pages[1].Setup.Content); Assert.Equal(PageBackground.Grid, session.Current.Pages[1].Setup.Background);
        session.Undo(); Same(model, session.Current);
        session.Execute(PageTransforms.Resize(model, ids, 200, 400, PageResizeMode.ClipContent)); Assert.Equal(1, session.Current.Pages[0].Setup.Content!.Transform.M11);
        Assert.Equal(1, session.UndoCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageTransforms.Resize(session.Current, ids, double.NaN, 100, PageResizeMode.FitContent));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageTransforms.Shrink(session.Current, ids, 101));
        Assert.Throws<KeyNotFoundException>(() => PageTransforms.Shrink(session.Current, [ids[0], Guid.NewGuid()], 50));
        Assert.Equal(1, session.UndoCount);
    }
    [Fact]
    public void TextLayoutPreservesLinesPaddingAndBorderUnderUniformShrink()
    {
        var box = TextEditingTests.Box("한글 English words across several lines\n두번째 줄 😀", 180) with { BoxStyle = new(RgbColor.White, 1, true, RgbColor.Black, 1) };
        var model = TestModels.Empty(); model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [box] }) };
        var session = new DocumentSession(model); session.Execute(PageTransforms.Shrink(model, [model.Pages[0].Id], 80));
        var scaled = (TextBoxAnnotation)session.Current.AllAnnotations.Single(); var a = TextLayout.Measure(box); var b = TextLayout.Measure(scaled);
        Assert.Equal(a.Characters.Length, b.Characters.Length); Assert.InRange(Math.Abs(a.Height * .8 - b.Height), 0, .01);
        for (int i = 0; i < a.Characters.Length; i++)
        { Assert.Equal(a.Characters[i].Line, b.Characters[i].Line); Assert.InRange(Math.Abs(a.Characters[i].Bounds.X * .8 - b.Characters[i].Bounds.X), 0, .01); }
        Assert.Equal(3.2, WpfText.Document(scaled).PagePadding.Left);
    }
    [Fact]
    public void V4RoundTripsAndOldReadersRejectExtendedDataAndLegacyStillMigrates()
    {
        var model = TestModels.AllKinds(out var resource); model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Setup = model.Pages[0].Setup with { Source = PageSource.Pdf(7) } }) };
        var s = new DocumentSession(model); s.Execute(PageTransforms.Shrink(model, model.Pages.Select(p => p.Id), 80));
        var data = SchemaV4.Serialize(s.Current); var read = SchemaV4.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), id => id == resource.Id ? resource.Payload.ToArray() : null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus); Assert.Equal(data.Header, SchemaV4.Serialize(read.Model!).Header);
        Assert.Equal(PageSource.Pdf(7), read.Model!.Pages[0].Setup.Source);
        Assert.Equal(HeaderStatus.Unsupported, SchemaV3.Read(data.Header, [], _ => null).HeaderStatus);
        Assert.Throws<InvalidDataException>(() => SchemaV3.Serialize(s.Current));
        var legacy = SchemaV1.Serialize(model); Assert.Equal(HeaderStatus.Valid, SchemaV4.Read(legacy.Header, legacy.Annotations.Select(a => a.Envelope).ToArray(), _ => resource.Payload.ToArray()).HeaderStatus);
    }
}
